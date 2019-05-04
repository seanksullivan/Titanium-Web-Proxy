﻿#if NETCOREAPP2_1
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Compression;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2.Hpack;

namespace Titanium.Web.Proxy.Http2
{
    internal class Http2Helper
    {
        /// <summary>
        ///     relays the input clientStream to the server at the specified host name and port with the given httpCmd and headers
        ///     as prefix
        ///     Useful for websocket requests
        ///     Task-based Asynchronous Pattern
        /// </summary>
        /// <returns></returns>
        internal static async Task SendHttp2(Stream clientStream, Stream serverStream, int bufferSize,
            Action<byte[], int, int> onDataSend, Action<byte[], int, int> onDataReceive,
            Func<SessionEventArgs> sessionFactory,
            Func<SessionEventArgs, Task> onBeforeRequest, Func<SessionEventArgs, Task> onBeforeResponse,
            CancellationTokenSource cancellationTokenSource, Guid connectionId,
            ExceptionHandler exceptionFunc)
        {
            var clientSettings = new Http2Settings();
            var serverSettings = new Http2Settings();

            var sessions = new ConcurrentDictionary<int, SessionEventArgs>();

            // Now async relay all server=>client & client=>server data
            var sendRelay =
                copyHttp2FrameAsync(clientStream, serverStream, onDataSend, clientSettings, serverSettings, 
                    sessionFactory, sessions, onBeforeRequest, 
                    bufferSize, connectionId, true, cancellationTokenSource.Token, exceptionFunc);
            var receiveRelay =
                copyHttp2FrameAsync(serverStream, clientStream, onDataReceive, serverSettings, clientSettings, 
                    sessionFactory, sessions, onBeforeResponse, 
                    bufferSize, connectionId, false, cancellationTokenSource.Token, exceptionFunc);

            await Task.WhenAny(sendRelay, receiveRelay);
            cancellationTokenSource.Cancel();

            await Task.WhenAll(sendRelay, receiveRelay);
        }

        private static async Task copyHttp2FrameAsync(Stream input, Stream output, Action<byte[], int, int> onCopy,
            Http2Settings localSettings, Http2Settings remoteSettings,
            Func<SessionEventArgs> sessionFactory, ConcurrentDictionary<int, SessionEventArgs>  sessions, 
            Func<SessionEventArgs, Task> onBeforeRequestResponse,
            int bufferSize, Guid connectionId, bool isClient, CancellationToken cancellationToken,
            ExceptionHandler exceptionFunc)
        {
            int headerTableSize = 0;
            Decoder decoder = null;

            var frameHeader = new Http2FrameHeader();
            frameHeader.Buffer = new byte[9];
            byte[] buffer = null;
            while (true)
            {
                var frameHeaderBuffer = frameHeader.Buffer;
                int read = await forceRead(input, frameHeaderBuffer, 0, 9, cancellationToken);
                onCopy(frameHeaderBuffer, 0, read);
                if (read != 9)
                {
                    return;
                }

                int length = (frameHeaderBuffer[0] << 16) + (frameHeaderBuffer[1] << 8) + frameHeaderBuffer[2];
                var type = (Http2FrameType)frameHeaderBuffer[3];
                var flags = (Http2FrameFlag)frameHeaderBuffer[4];
                int streamId = ((frameHeaderBuffer[5] & 0x7f) << 24) + (frameHeaderBuffer[6] << 16) + 
                               (frameHeaderBuffer[7] << 8) + frameHeaderBuffer[8];

                frameHeader.Length = length;
                frameHeader.Type = type;
                frameHeader.Flags = flags;
                frameHeader.StreamId = streamId;

                if (buffer == null || buffer.Length < localSettings.MaxFrameSize)
                {
                    buffer = new byte[localSettings.MaxFrameSize];
                }

                read = await forceRead(input, buffer, 0, length, cancellationToken);
                onCopy(buffer, 0, read);
                if (read != length)
                {
                    return;
                }

                bool sendPacket = true;
                bool endStream = false;

                SessionEventArgs args = null;
                RequestResponseBase rr = null;
                if (type == Http2FrameType.Data || type == Http2FrameType.Headers/* || type == Http2FrameType.PushPromise*/)
                {
                    if (!sessions.TryGetValue(streamId, out args))
                    {
                        //if (type == Http2FrameType.Data)
                        //{
                        //    throw new ProxyHttpException("HTTP Body data received before any header frame.", null, args);
                        //}

                        //if (type == Http2FrameType.Headers && !isClient)
                        //{
                        //    throw new ProxyHttpException("HTTP Response received before any Request header frame.", null, args);
                        //}

                        if (type == Http2FrameType.PushPromise && isClient)
                        {
                            throw new ProxyHttpException("HTTP Push promise received from the client.", null, args);
                        }
                    }
                }

                //System.Diagnostics.Debug.WriteLine("CONN: " + connectionId + ", CLIENT: " + isClient + ", STREAM: " + streamId + ", TYPE: " + type);
                if (type == Http2FrameType.Data && args != null)
                {
                    rr = isClient ? (RequestResponseBase)args.HttpClient.Request : args.HttpClient.Response;

                    bool padded = (flags & Http2FrameFlag.Padded) != 0;
                    bool endStreamFlag = (flags & Http2FrameFlag.EndStream) != 0;
                    if (endStreamFlag)
                    {
                        endStream = true;
                    }

                    if (rr.Http2IgnoreBodyFrames)
                    {
                        sendPacket = false;
                    }

                    if (rr.ReadHttp2BodyTaskCompletionSource != null)
                    {
                        // Get body method was called in the "before" event handler

                        var data = rr.Http2BodyData;
                        int offset = 0;
                        if (padded)
                        {
                            offset++;
                            length--;
                            length -= buffer[0];
                        }

                        data.Write(buffer, offset, length);
                    }
                }
                else if (type == Http2FrameType.Headers/* || type == Http2FrameType.PushPromise*/)
                {
                    bool endHeaders = (flags & Http2FrameFlag.EndHeaders) != 0;
                    bool padded = (flags & Http2FrameFlag.Padded) != 0;
                    bool priority = (flags & Http2FrameFlag.Priority) != 0;
                    bool endStreamFlag = (flags & Http2FrameFlag.EndStream) != 0;
                    if (endStreamFlag)
                    {
                        endStream = true;
                    }

                    int offset = 0;
                    if (padded)
                    {
                        offset = 1;
                        breakpoint();
                    }

                    if (type == Http2FrameType.PushPromise)
                    {
                        int promisedStreamId = (buffer[offset++] << 24) + (buffer[offset++] << 16) + (buffer[offset++] << 8) + buffer[offset++];
                        if (!sessions.TryGetValue(streamId, out args))
                        {
                            args = sessionFactory();
                            args.IsPromise = true;
                            sessions.TryAdd(streamId, args);
                            sessions.TryAdd(promisedStreamId, args);
                        }

                        System.Diagnostics.Debug.WriteLine("PROMISE STREAM: " + streamId + ", " + promisedStreamId +
                                                           ", CONN: " + connectionId);
                        rr = args.HttpClient.Request;

                        if (isClient)
                        {
                            // push_promise from client???
                            breakpoint();
                        }
                    }
                    else
                    {
                        if (!sessions.TryGetValue(streamId, out args))
                        {
                            args = sessionFactory();
                            sessions.TryAdd(streamId, args);
                        }

                        rr = isClient ? (RequestResponseBase)args.HttpClient.Request : args.HttpClient.Response;
                        if (priority)
                        {
                            var priorityData = ((long)buffer[offset++] << 32) + ((long)buffer[offset++] << 24) +
                                               (buffer[offset++] << 16) + (buffer[offset++] << 8) + buffer[offset++];
                            rr.Priority = priorityData;
                        }
                    }


                    int dataLength = length - offset;
                    if (padded)
                    {
                        dataLength -= buffer[0];
                    }

                    var headerListener = new MyHeaderListener(
                        (name, value) =>
                        {
                            var headers = isClient ? args.HttpClient.Request.Headers : args.HttpClient.Response.Headers;
                            headers.AddHeader(name, value);
                        });
                    try
                    {
                        // recreate the decoder when new value is bigger
                        // should we recreate when smaller, too?
                        if (decoder == null || headerTableSize < localSettings.HeaderTableSize)
                        {
                            headerTableSize = localSettings.HeaderTableSize;
                            decoder = new Decoder(8192, headerTableSize);
                        }

                        decoder.Decode(new BinaryReader(new MemoryStream(buffer, offset, dataLength)),
                            headerListener);
                        decoder.EndHeaderBlock();

                        if (rr is Request request)
                        {
                            request.HttpVersion = HttpVersion.Version20;
                            request.Method = headerListener.Method;
                            request.OriginalUrl = headerListener.Path;

                            request.RequestUri = headerListener.GetUri();
                        }
                        else
                        {
                            var response = (Response)rr;
                            response.HttpVersion = HttpVersion.Version20;
                            int.TryParse(headerListener.Status, out int statusCode);
                            response.StatusCode = statusCode;
                        }
                    }
                    catch (Exception ex)
                    {
                        exceptionFunc(new ProxyHttpException("Failed to decode HTTP/2 headers", ex, args));
                    }

                    if (!endHeaders)
                    {
                        breakpoint();
                    }

                    if (endHeaders)
                    {
                        var tcs = new TaskCompletionSource<bool>();
                        rr.ReadHttp2BeforeHandlerTaskCompletionSource = tcs;

                        var handler = onBeforeRequestResponse(args);
                        rr.Http2BeforeHandlerTask = handler;

                        if (handler == await Task.WhenAny(tcs.Task, handler))
                        {
                            rr.ReadHttp2BeforeHandlerTaskCompletionSource = null;
                            tcs.SetResult(true);
                            await sendHeader(remoteSettings, frameHeader, rr, endStream, output, args.IsPromise);
                        }
                        else
                        {
                            rr.Http2IgnoreBodyFrames = true;
                        }

                        rr.Locked = true;
                    }

                    sendPacket = false;
                }
                else if (type == Http2FrameType.Continuation)
                {
                    // todo: implementing this type is mandatory for multi-part headers
                    breakpoint();
                }
                else if (type == Http2FrameType.Settings)
                {
                    if (length % 6 != 0)
                    {
                        // https://httpwg.org/specs/rfc7540.html#SETTINGS
                        // 6.5. SETTINGS
                        // A SETTINGS frame with a length other than a multiple of 6 octets MUST be treated as a connection error (Section 5.4.1) of type FRAME_SIZE_ERROR
                        throw new ProxyHttpException("Invalid settings length", null, null);
                    }

                    int pos = 0;
                    while (pos < length)
                    {
                        int identifier = (buffer[pos++] << 8) + buffer[pos++];
                        int value = (buffer[pos++] << 24) + (buffer[pos++] << 16) + (buffer[pos++] << 8) + buffer[pos++];
                        if (identifier == 1 /*SETTINGS_HEADER_TABLE_SIZE*/)
                        {
                            //System.Diagnostics.Debug.WriteLine("HEADER SIZE CONN: " + connectionId + ", CLIENT: " + isClient + ", value: " + value);
                            remoteSettings.HeaderTableSize = value;
                        }
                        else if (identifier == 5 /*SETTINGS_MAX_FRAME_SIZE*/)
                        {
                            remoteSettings.MaxFrameSize = value;
                        }
                    }
                }

                if (type == Http2FrameType.RstStream)
                {
                    int errorCode = (buffer[0] << 24) + (buffer[1] << 16) + (buffer[2] << 8) + buffer[3];
                    if (streamId == 0)
                    {
                        // connection error
                        exceptionFunc(new ProxyHttpException("HTTP/2 connection error. Error code: " + errorCode, null, args));
                        return;
                    }
                    else
                    {
                        // stream error
                        sessions.TryRemove(streamId, out _);

                        if (errorCode != 8 /*cancel*/)
                        {
                            exceptionFunc(new ProxyHttpException("HTTP/2 stream error. Error code: " + errorCode, null, args));
                        }
                    }
                }

                if (endStream && rr.ReadHttp2BodyTaskCompletionSource != null)
                {
                    if (!rr.BodyAvailable)
                    {
                        var data = rr.Http2BodyData;
                        var body = data.ToArray();

                        if (rr.ContentEncoding != null)
                        {
                            using (var ms = new MemoryStream())
                            {
                                using (var zip =
                                    DecompressionFactory.Create(rr.ContentEncoding, new MemoryStream(body)))
                                {
                                    zip.CopyTo(ms);
                                }

                                body = ms.ToArray();
                            }
                        }

                        rr.Body = body;
                    }

                    rr.IsBodyRead = true;

                    var tcs = rr.ReadHttp2BodyTaskCompletionSource;
                    rr.ReadHttp2BodyTaskCompletionSource = null;

                    if (!tcs.Task.IsCompleted)
                    {
                        tcs.SetResult(true);
                    }

                    rr.Http2BodyData = null;

                    if (rr.Http2BeforeHandlerTask != null)
                    {
                        await rr.Http2BeforeHandlerTask;
                    }

                    if (args.IsPromise)
                    {
                        breakpoint();
                    }

                    await sendBody(remoteSettings, rr, frameHeader, buffer, output);
                }

                if (!isClient && endStream)
                {
                    sessions.TryRemove(streamId, out _);
                    System.Diagnostics.Debug.WriteLine("REMOVED CONN: " + connectionId + ", CLIENT: " + isClient + ", STREAM: " + streamId + ", TYPE: " + type);
                }

                if (sendPacket)
                {
                    // do not cancel the write operation
                    var buf = frameHeader.CopyToBuffer();
                    await output.WriteAsync(buf, 0, buf.Length/*, cancellationToken*/);
                    await output.WriteAsync(buffer, 0, length /*, cancellationToken*/);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                /*using (var fs = new System.IO.FileStream($@"c:\temp\{connectionId}.{streamId}.dat", FileMode.Append))
                {
                    fs.Write(headerBuffer, 0, headerBuffer.Length);
                    fs.Write(buffer, 0, length);
                }*/
            }
        }

        [Conditional("DEBUG")]
        private static void breakpoint()
        {
            // when this method is called something received which is not yet implemented
            ;
        }

        private static async Task sendHeader(Http2Settings settings, Http2FrameHeader frameHeader, RequestResponseBase rr, bool endStream, Stream output, bool pushPromise)
        {
            var encoder = new Encoder(settings.HeaderTableSize);
            var ms = new MemoryStream();
            var writer = new BinaryWriter(ms);
            if (rr.Priority.HasValue)
            {
                long p = rr.Priority.Value;
                writer.Write((byte)((p >> 32) & 0xff));
                writer.Write((byte)((p >> 24) & 0xff));
                writer.Write((byte)((p >> 16) & 0xff));
                writer.Write((byte)((p >> 8) & 0xff));
                writer.Write((byte)(p & 0xff));
            }

            if (rr is Request request)
            {
                encoder.EncodeHeader(writer, ":method", request.Method);
                encoder.EncodeHeader(writer, ":authority", request.RequestUri.Host);
                encoder.EncodeHeader(writer, ":scheme", request.RequestUri.Scheme);
                encoder.EncodeHeader(writer, ":path", request.RequestUriString, false,
                    HpackUtil.IndexType.None, false);
            }
            else
            {
                var response = (Response)rr;
                encoder.EncodeHeader(writer, ":status", response.StatusCode.ToString());
            }

            foreach (var header in rr.Headers)
            {
                encoder.EncodeHeader(writer, header.Name.ToLower(), header.Value);
            }

            var data = ms.ToArray();
            int newLength = data.Length;

            frameHeader.Length = newLength;
            frameHeader.Type = pushPromise ? Http2FrameType.PushPromise : Http2FrameType.Headers;

            var flags = Http2FrameFlag.EndHeaders;
            if (endStream)
            {
                flags |= Http2FrameFlag.EndStream;
            }

            if (rr.Priority.HasValue)
            {
                flags |= Http2FrameFlag.Priority;
            }

            frameHeader.Flags = flags;

            // clear the padding flag
            //headerBuffer[4] = (byte)(flags & ~((int)Http2FrameFlag.Padded));

            // send the header
            var buf = frameHeader.CopyToBuffer();
            await output.WriteAsync(buf, 0, buf.Length/*, cancellationToken*/);
            await output.WriteAsync(data, 0, data.Length /*, cancellationToken*/);
        }

        private static async Task sendBody(Http2Settings settings, RequestResponseBase rr, Http2FrameHeader frameHeader, byte[] buffer, Stream output)
        {
            var body = rr.CompressBodyAndUpdateContentLength();
            await sendHeader(settings, frameHeader, rr, !(rr.HasBody && rr.IsBodyRead), output, false);

            if (rr.HasBody && rr.IsBodyRead)
            {
                int pos = 0;
                while (pos < body.Length)
                {
                    int bodyFrameLength = Math.Min(buffer.Length, body.Length - pos);
                    Buffer.BlockCopy(body, pos, buffer, 0, bodyFrameLength);
                    pos += bodyFrameLength;

                    frameHeader.Length = bodyFrameLength;
                    frameHeader.Type = Http2FrameType.Data;
                    frameHeader.Flags = pos < body.Length ? (Http2FrameFlag)0 : Http2FrameFlag.EndStream;

                    var buf = frameHeader.CopyToBuffer();
                    await output.WriteAsync(buf, 0, buf.Length/*, cancellationToken*/);
                    await output.WriteAsync(buffer, 0, bodyFrameLength /*, cancellationToken*/);
                }
            }
            else
            {
                ;
            }
        }

        private static async Task<int> forceRead(Stream input, byte[] buffer, int offset, int bytesToRead,
            CancellationToken cancellationToken)
        {
            int totalRead = 0;
            while (bytesToRead > 0)
            {
                int read = await input.ReadAsync(buffer, offset, bytesToRead, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
                bytesToRead -= read;
                offset += read;
            }

            return totalRead;
        }


        class Http2Settings
        {
            public int HeaderTableSize { get; set; } = 4096;

            public int MaxFrameSize { get; set; } = 16384;
        }

        class MyHeaderListener : IHeaderListener
        {
            private readonly Action<string, string> addHeaderFunc;

            public string Method { get; private set; }

            public string Status { get; private set; }

            private string authority;

            private string scheme;

            public string Path { get; private set; }

            public MyHeaderListener(Action<string, string> addHeaderFunc)
            {
                this.addHeaderFunc = addHeaderFunc;
            }

            public void AddHeader(string name, string value, bool sensitive)
            {
                if (name[0] == ':')
                {
                    switch (name)
                    {
                        case ":method":
                            Method = value;
                            return;
                        case ":authority":
                            authority = value;
                            return;
                        case ":scheme":
                            scheme = value;
                            return;
                        case ":path":
                            Path = value;
                            return;
                        case ":status":
                            Status = value;
                            return;
                    }
                }

                addHeaderFunc(name, value);
            }

            public Uri GetUri()
            {
                if (authority == null)
                {
                    // todo
                    authority = "abc.abc";
                }

                return new Uri(scheme + "://" + authority + Path);
            }
        }
    }
}
#endif
