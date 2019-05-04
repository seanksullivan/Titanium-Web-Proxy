﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using StreamExtended;
using StreamExtended.Network;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.EventArguments
{
    /// <summary>
    ///     Holds info related to a single proxy session (single request/response sequence).
    ///     A proxy session is bounded to a single connection from client.
    ///     A proxy session ends when client terminates connection to proxy
    ///     or when server terminates connection from proxy.
    /// </summary>
    public abstract class SessionEventArgsBase : EventArgs, IDisposable
    {
        internal readonly CancellationTokenSource CancellationTokenSource;
        internal TcpServerConnection ServerConnection => HttpClient.Connection;
        internal TcpClientConnection ClientConnection => ProxyClient.Connection;

        protected readonly int BufferSize;
        protected readonly IBufferPool BufferPool;
        protected readonly ExceptionHandler ExceptionFunc;

        /// <summary>
        /// Relative milliseconds for various events.
        /// </summary>
        public Dictionary<string, DateTime> TimeLine { get; } = new Dictionary<string, DateTime>();

        /// <summary>
        ///     Initializes a new instance of the <see cref="SessionEventArgsBase" /> class.
        /// </summary>
        private SessionEventArgsBase(ProxyServer server, ProxyEndPoint endPoint,
            CancellationTokenSource cancellationTokenSource)
        {
            BufferSize = server.BufferSize;
            BufferPool = server.BufferPool;
            ExceptionFunc = server.ExceptionFunc;
            TimeLine["Session Created"] = DateTime.Now;
        }

        protected SessionEventArgsBase(ProxyServer server, ProxyEndPoint endPoint,
            CancellationTokenSource cancellationTokenSource,
            Request request) : this(server, endPoint, cancellationTokenSource)
        {
            CancellationTokenSource = cancellationTokenSource;

            ProxyClient = new ProxyClient();
            HttpClient = new HttpWebClient(request);
            LocalEndPoint = endPoint;

            HttpClient.ProcessId = new Lazy<int>(() => ProxyClient.Connection.GetProcessId(endPoint));
        }

        /// <summary>
        ///     Holds a reference to client
        /// </summary>
        internal ProxyClient ProxyClient { get; }

        /// <summary>
        ///     Returns a user data for this request/response session which is
        ///     same as the user data of HttpClient.
        /// </summary>
        public object UserData
        {
            get => HttpClient.UserData;
            set => HttpClient.UserData = value;
        }

        /// <summary>
        ///     Does this session uses SSL?
        /// </summary>
        public bool IsHttps => HttpClient.Request.IsHttps;

        /// <summary>
        ///     Client End Point.
        /// </summary>
        public IPEndPoint ClientEndPoint => (IPEndPoint)ProxyClient.Connection.RemoteEndPoint;

        /// <summary>
        ///    The web client used to communicate with server for this session.
        /// </summary>
        public HttpWebClient HttpClient { get; }

        [Obsolete("Use HttpClient instead.")]
        public HttpWebClient WebSession => HttpClient;

        /// <summary>
        ///     Are we using a custom upstream HTTP(S) proxy?
        /// </summary>
        public ExternalProxy CustomUpStreamProxyUsed { get; internal set; }

        /// <summary>
        ///     Local endpoint via which we make the request.
        /// </summary>
        public ProxyEndPoint LocalEndPoint { get; }

        /// <summary>
        ///     Is this a transparent endpoint?
        /// </summary>
        public bool IsTransparent => LocalEndPoint is TransparentProxyEndPoint;

        /// <summary>
        ///     The last exception that happened.
        /// </summary>
        public Exception Exception { get; internal set; }

        /// <summary>
        ///     Implements cleanup here.
        /// </summary>
        public virtual void Dispose()
        {
            CustomUpStreamProxyUsed = null;

            DataSent = null;
            DataReceived = null;
            Exception = null;

            HttpClient.FinishSession();
        }

        /// <summary>
        ///     Fired when data is sent within this session to server/client.
        /// </summary>
        public event EventHandler<DataEventArgs> DataSent;

        /// <summary>
        ///     Fired when data is received within this session from client/server.
        /// </summary>
        public event EventHandler<DataEventArgs> DataReceived;

        internal void OnDataSent(byte[] buffer, int offset, int count)
        {
            try
            {
                DataSent?.Invoke(this, new DataEventArgs(buffer, offset, count));
            }
            catch (Exception ex)
            {
                ExceptionFunc(new Exception("Exception thrown in user event", ex));
            }
        }

        internal void OnDataReceived(byte[] buffer, int offset, int count)
        {
            try
            {
                DataReceived?.Invoke(this, new DataEventArgs(buffer, offset, count));
            }
            catch (Exception ex)
            {
                ExceptionFunc(new Exception("Exception thrown in user event", ex));
            }
        }

        /// <summary>
        ///     Terminates the session abruptly by terminating client/server connections.
        /// </summary>
        public void TerminateSession()
        {
            CancellationTokenSource.Cancel();
        }
    }
}
