﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using IpMatcher;

namespace WatsonWebserver
{
    /// <summary>
    /// Watson webserver.
    /// </summary>
    public class Server : IDisposable
    {
        #region Public-Members
        
        /// <summary>
        /// Indicates whether or not the server is listening.
        /// </summary>
        public bool IsListening
        {
            get
            {
                return (_HttpListener != null) ? _HttpListener.IsListening : false;
            } 
        }
         
        /// <summary>
        /// Indicate the buffer size to use when reading from a stream to send data to a requestor.
        /// </summary>
        public int StreamReadBufferSize
        {
            get
            {
                return _StreamReadBufferSize;
            }
            set
            {
                if (value < 1) throw new ArgumentException("StreamReadBufferSize must be greater than zero.");
                _StreamReadBufferSize = value;
            }
        }

        /// <summary>
        /// Maximum number of concurrent requests.
        /// </summary>
        public int MaxRequests
        {
            get
            {
                return _MaxRequests;
            }
            set
            {
                if (value < 1) throw new ArgumentException("Maximum requests must be greater than zero.");
                _MaxRequests = value;
            }
        }

        /// <summary>
        /// Number of requests being serviced currently.
        /// </summary>
        public int RequestCount
        {
            get
            {
                return _RequestCount;
            }
        }

        /// <summary>
        /// Default HTTP header values.
        /// </summary>
        public DefaultHeaderValues HeaderValues
        {
            get
            {
                return _HeaderValues;
            }
            set
            {
                if (value == null) _HeaderValues = new DefaultHeaderValues();
                else _HeaderValues = value;
            }
        }

        /// <summary>
        /// Function to call when an OPTIONS request is received.  Often used to handle CORS.  Leave as 'null' to use the default OPTIONS handler.
        /// </summary>
        public Func<HttpContext, Task> OptionsRoute = null;

        /// <summary>
        /// Function to call prior to routing.  
        /// Return 'true' if the connection should be terminated.
        /// Return 'false' to allow the connection to continue routing.
        /// </summary>
        public Func<HttpContext, Task<bool>> PreRoutingHandler = null;

        /// <summary>
        /// Dynamic routes; i.e. routes with regex matching and any HTTP method.
        /// </summary>
        public DynamicRouteManager DynamicRoutes = new DynamicRouteManager();

        /// <summary>
        /// Static routes; i.e. routes with explicit matching and any HTTP method.
        /// </summary>
        public StaticRouteManager StaticRoutes = new StaticRouteManager();

        /// <summary>
        /// Content routes; i.e. routes to specific files or folders for GET and HEAD requests.
        /// </summary>
        public ContentRouteManager ContentRoutes = new ContentRouteManager();
         
        /// <summary>
        /// Access control manager, i.e. default mode of operation, permit list, and deny list.
        /// </summary>
        public AccessControlManager AccessControl = new AccessControlManager(AccessControlMode.DefaultPermit);

        /// <summary>
        /// Set specific actions/callbacks to use when events are raised.
        /// </summary>
        public EventCallbacks Events = new EventCallbacks();

        /// <summary>
        /// Watson webserver statistics.
        /// </summary>
        public Statistics Stats
        {
            get
            {
                return _Stats;
            }
        }
         
        #endregion

        #region Private-Members

        private readonly EventWaitHandle _Terminator = new EventWaitHandle(false, EventResetMode.ManualReset);

        private DefaultHeaderValues _HeaderValues = new DefaultHeaderValues();
        private HttpListener _HttpListener = null;
        private List<string> _ListenerUris = null;
        private List<string> _ListenerHostnames = null;
        private int _ListenerPort;
        private bool _ListenerSsl = false;
        private int _StreamReadBufferSize = 65536;
        private int _MaxRequests = 1024;
        private int _RequestCount = 0;

        private ContentRouteProcessor _ContentRouteProcessor;
        private Func<HttpContext, Task> _DefaultRoute = null;

        private CancellationTokenSource _TokenSource = new CancellationTokenSource();
        private CancellationToken _Token;

        private Statistics _Stats = new Statistics();

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Creates a new instance of the Watson Webserver.
        /// </summary>
        /// <param name="hostname">Hostname or IP address on which to listen.</param>
        /// <param name="port">TCP port on which to listen.</param>
        /// <param name="ssl">Specify whether or not SSL should be used (HTTPS).</param>
        /// <param name="defaultRoute">Method used when a request is received and no matching routes are found.  Commonly used as the 404 handler when routes are used.</param>
        public Server(string hostname, int port, bool ssl, Func<HttpContext, Task> defaultRoute)
        {
            if (String.IsNullOrEmpty(hostname)) hostname = "*";
            if (port < 1) throw new ArgumentOutOfRangeException(nameof(port));
            if (defaultRoute == null) throw new ArgumentNullException(nameof(defaultRoute));

            _HttpListener = new HttpListener();

            _ListenerHostnames = new List<string>();
            _ListenerHostnames.Add(hostname); 
            _ListenerPort = port;
            _ListenerSsl = ssl; 
            _DefaultRoute = defaultRoute; 
            _Token = _TokenSource.Token;
            _ContentRouteProcessor = new ContentRouteProcessor(ContentRoutes); 
        }

        /// <summary>
        /// Creates a new instance of the Watson Webserver.
        /// </summary>
        /// <param name="hostnames">Hostnames or IP addresses on which to listen.  Note: multiple listener endpoints is not supported on all platforms.</param>
        /// <param name="port">TCP port on which to listen.</param>
        /// <param name="ssl">Specify whether or not SSL should be used (HTTPS).</param>
        /// <param name="defaultRoute">Method used when a request is received and no matching routes are found.  Commonly used as the 404 handler when routes are used.</param>
        public Server(List<string> hostnames, int port, bool ssl, Func<HttpContext, Task> defaultRoute)
        {
            if (port < 1) throw new ArgumentOutOfRangeException(nameof(port));
            if (defaultRoute == null) throw new ArgumentNullException(nameof(defaultRoute));

            _HttpListener = new HttpListener();

            _ListenerHostnames = new List<string>();
            if (hostnames == null || hostnames.Count < 1)
            {
                _ListenerHostnames.Add("*");
            }
            else
            {
                foreach (string curr in hostnames)
                {
                    _ListenerHostnames.Add(curr);
                }
            }
            
            _ListenerPort = port;
            _ListenerSsl = ssl;
            _DefaultRoute = defaultRoute;  
            _Token = _TokenSource.Token;
            _ContentRouteProcessor = new ContentRouteProcessor(ContentRoutes); 
        }

        /// <summary>
        /// Creates a new instance of the Watson Webserver.
        /// </summary>
        /// <param name="uris">URIs on which to listen.  
        /// URIs should be of the form: http://[hostname]:[port]/[url]
        /// Note: multiple listener endpoints is not supported on all platforms.</param>
        /// <param name="defaultRoute">Method used when a request is received and no matching routes are found.  Commonly used as the 404 handler when routes are used.</param>
        public Server(List<string> uris, Func<HttpContext, Task> defaultRoute)
        {
            if (defaultRoute == null) throw new ArgumentNullException(nameof(defaultRoute));
            if (uris == null) throw new ArgumentNullException(nameof(uris));
            if (uris.Count < 1) throw new ArgumentException("At least one URI must be supplied.");

            _HttpListener = new HttpListener(); 
            _ListenerUris = new List<string>(uris); 
              
            _DefaultRoute = defaultRoute; 
            _Token = _TokenSource.Token;
            _ContentRouteProcessor = new ContentRouteProcessor(ContentRoutes); 
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Tear down the server and dispose of background workers.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        /// <summary>
        /// Start accepting new connections.
        /// </summary>
        public void Start()
        {
            if (_HttpListener != null && _HttpListener.IsListening) throw new InvalidOperationException("WatsonWebserver is already listening.");

            _TokenSource = new CancellationTokenSource();
            _Token = _TokenSource.Token;
            _Stats = new Statistics();

            Task.Run(() => AcceptConnections(), _Token);
        }

        /// <summary>
        /// Start accepting new connections.
        /// </summary>
        /// <returns>Task.</returns>
        public Task StartAsync()
        {
            if (_HttpListener != null && _HttpListener.IsListening) throw new InvalidOperationException("WatsonWebserver is already listening.");

            _TokenSource = new CancellationTokenSource();
            _Token = _TokenSource.Token;
            _Stats = new Statistics();

            return AcceptConnections();
        }

        /// <summary>
        /// Stop accepting new connections.
        /// </summary>
        public void Stop()
        {
            if (!_HttpListener.IsListening) throw new InvalidOperationException("WatsonWebserver is already stopped.");

            _HttpListener.Stop();
            _TokenSource.Cancel();
        }

        #endregion

        #region Private-Methods

        /// <summary>
        /// Tear down the server and dispose of background workers.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_HttpListener != null)
                {
                    if (_HttpListener.IsListening) _HttpListener.Stop();
                    _HttpListener.Close();
                }
                
                _TokenSource.Cancel();
            }

            Events.ServerDisposed?.Invoke();
        }
           
        private async Task AcceptConnections()
        {
            try
            {
                #region Start-Listeners

                if (_ListenerHostnames != null)
                {
                    foreach (string curr in _ListenerHostnames)
                    {
                        string prefix = null;
                        if (_ListenerSsl) prefix = "https://" + curr + ":" + _ListenerPort + "/";
                        else prefix = "http://" + curr + ":" + _ListenerPort + "/";
                        _HttpListener.Prefixes.Add(prefix);
                    }
                }
                else if (_ListenerUris != null)
                {
                    foreach (string curr in _ListenerUris)
                    {
                        _HttpListener.Prefixes.Add(curr);
                    }
                }

                _HttpListener.Start();

                #endregion

                #region Listen-and-Process-Requests

                while (_HttpListener.IsListening && !_TokenSource.IsCancellationRequested)
                {
                    if (_RequestCount >= _MaxRequests)
                    {
                        Task.Delay(100).Wait();
                        continue;
                    }

                    HttpListenerContext listenerContext = await _HttpListener.GetContextAsync();

                    Interlocked.Increment(ref _RequestCount);
                    
                    HttpContext ctx = null;

                    Task unawaited = Task.Run(async () =>
                    {
                        DateTime startTime = DateTime.Now;

                        try
                        {
                            #region Build-Context

                            Events.ConnectionReceived?.Invoke(
                                listenerContext.Request.RemoteEndPoint.Address.ToString(),
                                listenerContext.Request.RemoteEndPoint.Port);

                            ctx = new HttpContext(listenerContext, Events);

                            Events.RequestReceived?.Invoke(
                                ctx.Request.SourceIp,
                                ctx.Request.SourcePort,
                                ctx.Request.Method.ToString(),
                                ctx.Request.FullUrl);

                            _Stats.IncrementRequestCounter(ctx.Request.Method);
                            _Stats.ReceivedPayloadBytes += ctx.Request.ContentLength;

                            #endregion

                            #region Check-Access-Control

                            if (!AccessControl.Permit(ctx.Request.SourceIp))
                            {
                                Events.AccessControlDenied?.Invoke(
                                    ctx.Request.SourceIp,
                                    ctx.Request.SourcePort,
                                    ctx.Request.Method.ToString(),
                                    ctx.Request.FullUrl);

                                listenerContext.Response.Close();
                                return;
                            }

                            #endregion

                            #region Process-Preflight-Requests

                            if (ctx.Request.Method == HttpMethod.OPTIONS)
                            {
                                if (OptionsRoute != null)
                                {
                                    await OptionsRoute(ctx);
                                    return;
                                }
                                else
                                {
                                    OptionsProcessor(listenerContext, ctx.Request);
                                    return;
                                }
                            }

                            #endregion

                            #region Pre-Routing-Handler

                            bool terminate = false;
                            if (PreRoutingHandler != null)
                            {
                                terminate = await PreRoutingHandler(ctx);
                                if (terminate) return;
                            }

                            #endregion

                            #region Content-Routes

                            if (ctx.Request.Method == HttpMethod.GET || ctx.Request.Method == HttpMethod.HEAD)
                            {
                                if (ContentRoutes.Exists(ctx.Request.RawUrlWithoutQuery))
                                {
                                    await _ContentRouteProcessor.Process(ctx); 
                                    return;
                                }
                            }

                            #endregion

                            #region Static-Routes

                            Func<HttpContext, Task> handler = StaticRoutes.Match(ctx.Request.Method, ctx.Request.RawUrlWithoutQuery);
                            if (handler != null)
                            {
                                await handler(ctx);
                                return;
                            }

                            #endregion

                            #region Dynamic-Routes

                            handler = DynamicRoutes.Match(ctx.Request.Method, ctx.Request.RawUrlWithoutQuery);
                            if (handler != null)
                            {
                                await handler(ctx);
                                return;
                            }

                            #endregion

                            #region Default-Route

                            await _DefaultRoute(ctx);
                            return;

                            #endregion
                        }
                        catch (Exception eInner)
                        {
                            if (ctx == null || ctx.Request == null) Events.ExceptionEncountered?.Invoke(null, 0, eInner);
                            else Events.ExceptionEncountered?.Invoke(ctx.Request.SourceIp, ctx.Request.SourcePort, eInner);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _RequestCount);

                            if (ctx != null && ctx.Response != null && ctx.Response.ResponseSent)
                            {
                                Events.ResponseSent?.Invoke(
                                    ctx.Request.SourceIp,
                                    ctx.Request.SourcePort,
                                    ctx.Request.Method.ToString(),
                                    ctx.Request.FullUrl,
                                    ctx.Response.StatusCode,
                                    TotalMsFrom(startTime));

                                _Stats.SentPayloadBytes += ctx.Response.ContentLength;
                            }
                        }

                    }, _Token);
                }

                #endregion
            } 
            catch (Exception e)
            {
                Events.ExceptionEncountered?.Invoke(null, 0, e);
            } 
            finally
            {
                Events.ServerStopped?.Invoke();
            }
        }
         
        private void OptionsProcessor(HttpListenerContext context, HttpRequest req)
        { 
            HttpListenerResponse response = context.Response;
            response.StatusCode = 200;

            string[] requestedHeaders = null;
            if (req.Headers != null)
            {
                foreach (KeyValuePair<string, string> curr in req.Headers)
                {
                    if (String.IsNullOrEmpty(curr.Key)) continue;
                    if (String.IsNullOrEmpty(curr.Value)) continue;
                    if (String.Compare(curr.Key.ToLower(), "access-control-request-headers") == 0)
                    {
                        requestedHeaders = curr.Value.Split(',');
                        break;
                    }
                }
            }

            string headers = "";

            if (requestedHeaders != null)
            {
                int addedCount = 0;
                foreach (string curr in requestedHeaders)
                {
                    if (String.IsNullOrEmpty(curr)) continue;
                    if (addedCount > 0) headers += ", ";
                    headers += ", " + curr;
                    addedCount++;
                }
            }

            if (!String.IsNullOrEmpty(_HeaderValues.Host)) response.AddHeader("Host", _HeaderValues.Host);
            else response.AddHeader("Host", req.DestHostname + ":" + req.DestPort);

            if (!String.IsNullOrEmpty(_HeaderValues.AccessControlAllowMethods)) response.AddHeader("Access-Control-Allow-Methods", _HeaderValues.AccessControlAllowMethods);
            else response.AddHeader("Access-Control-Allow-Methods", "OPTIONS, HEAD, GET, PUT, POST, DELETE");

            if (!String.IsNullOrEmpty(_HeaderValues.AccessControlAllowHeaders)) response.AddHeader("Access-Control-Allow-Headers", _HeaderValues.AccessControlAllowHeaders);
            else response.AddHeader("Access-Control-Allow-Headers", "*, Content-Type, X-Requested-With, " + headers);

            if (!String.IsNullOrEmpty(_HeaderValues.AccessControlExposeHeaders)) response.AddHeader("Access-Control-Expose-Headers", _HeaderValues.AccessControlExposeHeaders);
            else response.AddHeader("Access-Control-Expose-Headers", "Content-Type, X-Requested-With, " + headers);

            if (!String.IsNullOrEmpty(_HeaderValues.AccessControlAllowOrigin)) response.AddHeader("Access-Control-Allow-Origin", _HeaderValues.AccessControlAllowOrigin);
            else response.AddHeader("Access-Control-Allow-Origin", "*");

            if (!String.IsNullOrEmpty(_HeaderValues.Accept)) response.AddHeader("Accept", _HeaderValues.Accept);
            else response.AddHeader("Accept", "*/*");

            if (!String.IsNullOrEmpty(_HeaderValues.AcceptLanguage)) response.AddHeader("Accept-Language", _HeaderValues.AcceptLanguage);
            else response.AddHeader("Accept-Language", "en-US, en");

            if (!String.IsNullOrEmpty(_HeaderValues.AcceptCharset)) response.AddHeader("Accept-Charset", _HeaderValues.AcceptCharset);
            else response.AddHeader("Accept-Charset", "ISO-8859-1, utf-8");

            if (!String.IsNullOrEmpty(_HeaderValues.Connection)) response.AddHeader("Connection", _HeaderValues.Connection);
            else response.AddHeader("Connection", "keep-alive"); 

            response.ContentLength64 = 0;
            response.Close(); 
            return;
        }

        private double TotalMsFrom(DateTime startTime)
        {
            try
            {
                DateTime endTime = DateTime.Now;
                TimeSpan totalTime = (endTime - startTime);
                return totalTime.TotalMilliseconds;
            }
            catch (Exception)
            {
                return -1;
            }
        }
         
        #endregion
    }
}
