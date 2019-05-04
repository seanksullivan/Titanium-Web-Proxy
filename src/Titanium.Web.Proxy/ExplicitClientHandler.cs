﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using StreamExtended;
using StreamExtended.Network;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy
{
    public partial class ProxyServer
    {
        /// <summary>
        ///     This is called when client is aware of proxy
        ///     So for HTTPS requests client would send CONNECT header to negotiate a secure tcp tunnel via proxy
        /// </summary>
        /// <param name="endPoint">The explicit endpoint.</param>
        /// <param name="clientConnection">The client connection.</param>
        /// <returns>The task.</returns>
        private async Task handleClient(ExplicitProxyEndPoint endPoint, TcpClientConnection clientConnection)
        {
            var cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;

            var clientStream = new CustomBufferedStream(clientConnection.GetStream(), BufferPool, BufferSize);
            var clientStreamWriter = new HttpResponseWriter(clientStream, BufferPool, BufferSize);

            Task<TcpServerConnection> prefetchConnectionTask = null;
            bool closeServerConnection = false;
            bool calledRequestHandler = false;

            SslStream sslStream = null;

            try
            {
                string connectHostname = null;
                TunnelConnectSessionEventArgs connectArgs = null;
                
                // Client wants to create a secure tcp tunnel (probably its a HTTPS or Websocket request)
                if (await HttpHelper.IsConnectMethod(clientStream, BufferPool, BufferSize, cancellationToken) == 1)
                {
                    // read the first line HTTP command
                    string httpCmd = await clientStream.ReadLineAsync(cancellationToken);
                    if (string.IsNullOrEmpty(httpCmd))
                    {
                        return;
                    }

                    Request.ParseRequestLine(httpCmd, out string _, out string httpUrl, out var version);

                    var httpRemoteUri = new Uri("http://" + httpUrl);
                    connectHostname = httpRemoteUri.Host;

                    var connectRequest = new ConnectRequest
                    {
                        RequestUri = httpRemoteUri,
                        OriginalUrl = httpUrl,
                        HttpVersion = version
                    };

                    await HeaderParser.ReadHeaders(clientStream, connectRequest.Headers, cancellationToken);

                    connectArgs = new TunnelConnectSessionEventArgs(this, endPoint, connectRequest,
                        cancellationTokenSource);
                    connectArgs.ProxyClient.Connection = clientConnection;
                    connectArgs.ProxyClient.ClientStream = clientStream;

                    await endPoint.InvokeBeforeTunnelConnectRequest(this, connectArgs, ExceptionFunc);

                    // filter out excluded host names
                    bool decryptSsl = endPoint.DecryptSsl && connectArgs.DecryptSsl;

                    if (connectArgs.DenyConnect)
                    {
                        if (connectArgs.HttpClient.Response.StatusCode == 0)
                        {
                            connectArgs.HttpClient.Response = new Response
                            {
                                HttpVersion = HttpHeader.Version11,
                                StatusCode = (int)HttpStatusCode.Forbidden,
                                StatusDescription = "Forbidden"
                            };
                        }

                        // send the response
                        await clientStreamWriter.WriteResponseAsync(connectArgs.HttpClient.Response,
                            cancellationToken: cancellationToken);
                        return;
                    }

                    if (await checkAuthorization(connectArgs) == false)
                    {
                        await endPoint.InvokeBeforeTunnelConnectResponse(this, connectArgs, ExceptionFunc);

                        // send the response
                        await clientStreamWriter.WriteResponseAsync(connectArgs.HttpClient.Response,
                            cancellationToken: cancellationToken);
                        return;
                    }

                    // write back successful CONNECT response
                    var response = ConnectResponse.CreateSuccessfulConnectResponse(version);

                    // Set ContentLength explicitly to properly handle HTTP 1.0
                    response.ContentLength = 0;
                    response.Headers.FixProxyHeaders();
                    connectArgs.HttpClient.Response = response;

                    await clientStreamWriter.WriteResponseAsync(response, cancellationToken: cancellationToken);

                    var clientHelloInfo = await SslTools.PeekClientHello(clientStream, BufferPool, cancellationToken);

                    bool isClientHello = clientHelloInfo != null;
                    if (isClientHello)
                    {
                        connectRequest.TunnelType = TunnelType.Https;
                        connectRequest.ClientHelloInfo = clientHelloInfo;
                    }

                    await endPoint.InvokeBeforeTunnelConnectResponse(this, connectArgs, ExceptionFunc, isClientHello);

                    if (decryptSsl && isClientHello)
                    {
                        connectRequest.RequestUri = new Uri("https://" + httpUrl);

                        bool http2Supported = false;

                        var alpn = clientHelloInfo.GetAlpn();
                        if (alpn != null && alpn.Contains(SslApplicationProtocol.Http2))
                        {
                            // test server HTTP/2 support
                            try
                            {
                                // todo: this is a hack, because Titanium does not support HTTP protocol changing currently
                                var connection = await tcpConnectionFactory.GetServerConnection(this, connectArgs,
                                    isConnect: true, applicationProtocols: SslExtensions.Http2ProtocolAsList,
                                    noCache: true, cancellationToken: cancellationToken);

                                http2Supported = connection.NegotiatedApplicationProtocol ==
                                                 SslApplicationProtocol.Http2;
                                //release connection back to pool instead of closing when connection pool is enabled.
                                await tcpConnectionFactory.Release(connection, true);
                            }
                            catch (Exception)
                            {
                                // ignore
                            }
                        }

                        if (EnableTcpServerConnectionPrefetch)
                        {
                            IPAddress[] ipAddresses = null;
                            try
                            {
                                //make sure the host can be resolved before creating the prefetch task
                                ipAddresses = await Dns.GetHostAddressesAsync(connectArgs.HttpClient.Request.RequestUri.Host);
                            }
                            catch (SocketException) { }

                            if (ipAddresses != null && ipAddresses.Length > 0)
                            {
                                //don't pass cancellation token here
                                //it could cause floating server connections when client exits
                                prefetchConnectionTask = tcpConnectionFactory.GetServerConnection(this, connectArgs,
                                                        isConnect: true, applicationProtocols: null, noCache: false,
                                                        cancellationToken: CancellationToken.None);
                            }
                        }

                        X509Certificate2 certificate = null;
                        try
                        {
                            sslStream = new SslStream(clientStream, true);

                            string certName = HttpHelper.GetWildCardDomainName(connectHostname);
                            certificate = endPoint.GenericCertificate ??
                                              await CertificateManager.CreateServerCertificate(certName);

                            // Successfully managed to authenticate the client using the fake certificate
                            var options = new SslServerAuthenticationOptions();
                            if (EnableHttp2 && http2Supported)
                            {
                                options.ApplicationProtocols = clientHelloInfo.GetAlpn();
                                if (options.ApplicationProtocols == null || options.ApplicationProtocols.Count == 0)
                                {
                                    options.ApplicationProtocols = SslExtensions.Http11ProtocolAsList;
                                }
                            }

                            options.ServerCertificate = certificate;
                            options.ClientCertificateRequired = false;
                            options.EnabledSslProtocols = SupportedSslProtocols;
                            options.CertificateRevocationCheckMode = X509RevocationMode.NoCheck;
                            await sslStream.AuthenticateAsServerAsync(options, cancellationToken);

#if NETCOREAPP2_1
                            clientConnection.NegotiatedApplicationProtocol = sslStream.NegotiatedApplicationProtocol;
#endif

                            // HTTPS server created - we can now decrypt the client's traffic
                            clientStream = new CustomBufferedStream(sslStream, BufferPool, BufferSize);
                            clientStreamWriter = new HttpResponseWriter(clientStream, BufferPool, BufferSize);
                        }
                        catch (Exception e)
                        {
                            var certName = certificate?.GetNameInfo(X509NameType.SimpleName, false);
                            throw new ProxyConnectException(
                                $"Couldn't authenticate host '{connectHostname}' with certificate '{certName}'.", e, connectArgs);
                        }

                        if (await HttpHelper.IsConnectMethod(clientStream, BufferPool, BufferSize, cancellationToken) == -1)
                        {
                            decryptSsl = false;
                        }

                        if (!decryptSsl)
                        {
                            await tcpConnectionFactory.Release(prefetchConnectionTask, true);
                            prefetchConnectionTask = null;
                        }
                    }

                    if (cancellationTokenSource.IsCancellationRequested)
                    {
                        throw new Exception("Session was terminated by user.");
                    }

                    // Hostname is excluded or it is not an HTTPS connect
                    if (!decryptSsl || !isClientHello)
                    {
                        if (!isClientHello)
                        {
                            connectRequest.TunnelType = TunnelType.Websocket;
                        }

                        // create new connection to server.
                        // If we detected that client tunnel CONNECTs without SSL by checking for empty client hello then 
                        // this connection should not be HTTPS.
                        var connection = await tcpConnectionFactory.GetServerConnection(this, connectArgs,
                                                        isConnect: true, applicationProtocols: SslExtensions.Http2ProtocolAsList,
                                                        noCache: true, cancellationToken: cancellationToken);

                        try
                        {
                            if (isClientHello)
                            {
                                int available = clientStream.Available;
                                if (available > 0)
                                {
                                    // send the buffered data
                                    var data = BufferPool.GetBuffer(BufferSize);

                                    try
                                    {
                                        await clientStream.ReadAsync(data, 0, available, cancellationToken);
                                        // clientStream.Available should be at most BufferSize because it is using the same buffer size
                                        await connection.StreamWriter.WriteAsync(data, 0, available, true, cancellationToken);
                                    }
                                    finally
                                    {
                                        BufferPool.ReturnBuffer(data);
                                    }
                                }

                                var serverHelloInfo = await SslTools.PeekServerHello(connection.Stream, BufferPool, cancellationToken);
                                ((ConnectResponse)connectArgs.HttpClient.Response).ServerHelloInfo = serverHelloInfo;
                            }

                            await TcpHelper.SendRaw(clientStream, connection.Stream, BufferPool, BufferSize,
                                (buffer, offset, count) => { connectArgs.OnDataSent(buffer, offset, count); },
                                (buffer, offset, count) => { connectArgs.OnDataReceived(buffer, offset, count); },
                                connectArgs.CancellationTokenSource, ExceptionFunc);
                        }
                        finally
                        {
                            await tcpConnectionFactory.Release(connection, true);
                        }

                        return;
                    }
                }

                if (connectArgs != null && await HttpHelper.IsPriMethod(clientStream, BufferPool, BufferSize, cancellationToken) == 1)
                {
                    // todo
                    string httpCmd = await clientStream.ReadLineAsync(cancellationToken);
                    if (httpCmd == "PRI * HTTP/2.0")
                    {
                        connectArgs.HttpClient.ConnectRequest.TunnelType = TunnelType.Http2;

                        // HTTP/2 Connection Preface
                        string line = await clientStream.ReadLineAsync(cancellationToken);
                        if (line != string.Empty)
                        {
                            throw new Exception($"HTTP/2 Protocol violation. Empty string expected, '{line}' received");
                        }

                        line = await clientStream.ReadLineAsync(cancellationToken);
                        if (line != "SM")
                        {
                            throw new Exception($"HTTP/2 Protocol violation. 'SM' expected, '{line}' received");
                        }

                        line = await clientStream.ReadLineAsync(cancellationToken);
                        if (line != string.Empty)
                        {
                            throw new Exception($"HTTP/2 Protocol violation. Empty string expected, '{line}' received");
                        }

                        var connection = await tcpConnectionFactory.GetServerConnection(this, connectArgs,
                                                        isConnect: true, applicationProtocols: SslExtensions.Http2ProtocolAsList,
                                                        noCache: true, cancellationToken: cancellationToken);
                        try
                        {
                            await connection.StreamWriter.WriteLineAsync("PRI * HTTP/2.0", cancellationToken);
                            await connection.StreamWriter.WriteLineAsync(cancellationToken);
                            await connection.StreamWriter.WriteLineAsync("SM", cancellationToken);
                            await connection.StreamWriter.WriteLineAsync(cancellationToken);
#if NETCOREAPP2_1
                            await Http2Helper.SendHttp2(clientStream, connection.Stream, BufferSize,
                                (buffer, offset, count) => { connectArgs.OnDataSent(buffer, offset, count); },
                                (buffer, offset, count) => { connectArgs.OnDataReceived(buffer, offset, count); },
                                () => new SessionEventArgs(this, endPoint, cancellationTokenSource)
                                {
                                    ProxyClient = { Connection = clientConnection },
                                    HttpClient = { ConnectRequest = connectArgs?.HttpClient.ConnectRequest },
                                    UserData = connectArgs?.UserData
                                },
                                async args => { await invokeBeforeRequest(args); },
                                async args => { await invokeBeforeResponse(args); },
                                connectArgs.CancellationTokenSource, clientConnection.Id, ExceptionFunc);
#endif
                        }
                        finally
                        {
                            await tcpConnectionFactory.Release(connection, true);
                        }
                    }
                }

                calledRequestHandler = true;
                // Now create the request
                await handleHttpSessionRequest(endPoint, clientConnection, clientStream, clientStreamWriter,
                    cancellationTokenSource, connectHostname, connectArgs, prefetchConnectionTask);
            }
            catch (ProxyException e)
            {
                closeServerConnection = true;
                onException(clientStream, e);
            }
            catch (IOException e)
            {
                closeServerConnection = true;
                onException(clientStream, new Exception("Connection was aborted", e));
            }
            catch (SocketException e)
            {
                closeServerConnection = true;
                onException(clientStream, new Exception("Could not connect", e));
            }
            catch (Exception e)
            {
                closeServerConnection = true;
                onException(clientStream, new Exception("Error occured in whilst handling the client", e));
            }
            finally
            {
                if (!calledRequestHandler)
                {
                    await tcpConnectionFactory.Release(prefetchConnectionTask, closeServerConnection);
                }

                sslStream?.Dispose();
                clientStream.Dispose();

                if (!cancellationTokenSource.IsCancellationRequested)
                {
                    cancellationTokenSource.Cancel();
                }
            }
        }
    }
}
