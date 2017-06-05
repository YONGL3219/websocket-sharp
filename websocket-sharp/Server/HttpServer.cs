#region License
/*
 * HttpServer.cs
 *
 * A simple HTTP server that allows to accept the WebSocket connection requests.
 *
 * The MIT License
 *
 * Copyright (c) 2012-2016 sta.blockhead
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 */
#endregion

#region Contributors
/*
 * Contributors:
 * - Juan Manuel Lallana <juan.manuel.lallana@gmail.com>
 * - Liryna <liryna.stark@gmail.com>
 * - Rohan Singh <rohan-singh@hotmail.com>
 */
#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Threading;
using WebSocketSharp.Net;
using WebSocketSharp.Net.WebSockets;

namespace WebSocketSharp.Server
{
  /// <summary>
  /// Provides a simple HTTP server that allows to accept the WebSocket connection requests.
  /// </summary>
  /// <remarks>
  /// The HttpServer class can provide multiple WebSocket services.
  /// </remarks>
  public class HttpServer
  {
    #region Private Fields

    private System.Net.IPAddress    _address;
    private string                  _hostname;
    private HttpListener            _listener;
    private Logger                  _log;
    private int                     _port;
    private Thread                  _receiveThread;
    private string                  _rootPath;
    private bool                    _secure;
    private WebSocketServiceManager _services;
    private volatile ServerState    _state;
    private object                  _sync;
    private bool                    _windows;

    #endregion

    #region Public Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpServer"/> class.
    /// </summary>
    /// <remarks>
    /// An instance initialized by this constructor listens for the incoming requests on port 80.
    /// </remarks>
    public HttpServer ()
    {
      init ("*", System.Net.IPAddress.Any, 80, false);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpServer"/> class with
    /// the specified <paramref name="port"/>.
    /// </summary>
    /// <remarks>
    ///   <para>
    ///   An instance initialized by this constructor listens for the incoming requests on
    ///   <paramref name="port"/>.
    ///   </para>
    ///   <para>
    ///   If <paramref name="port"/> is 443, that instance provides a secure connection.
    ///   </para>
    /// </remarks>
    /// <param name="port">
    /// An <see cref="int"/> that represents the port number on which to listen.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="port"/> isn't between 1 and 65535 inclusive.
    /// </exception>
    public HttpServer (int port)
      : this (port, port == 443)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpServer"/> class with
    /// the specified HTTP URL.
    /// </summary>
    /// <remarks>
    ///   <para>
    ///   An instance initialized by this constructor listens for the incoming requests on
    ///   the host name and port in <paramref name="url"/>.
    ///   </para>
    ///   <para>
    ///   If <paramref name="url"/> doesn't include a port, either port 80 or 443 is used on
    ///   which to listen. It's determined by the scheme (http or https) in <paramref name="url"/>.
    ///   (Port 80 if the scheme is http.)
    ///   </para>
    /// </remarks>
    /// <param name="url">
    /// A <see cref="string"/> that represents the HTTP URL of the server.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="url"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    ///   <para>
    ///   <paramref name="url"/> is empty.
    ///   </para>
    ///   <para>
    ///   -or-
    ///   </para>
    ///   <para>
    ///   <paramref name="url"/> is invalid.
    ///   </para>
    /// </exception>
    public HttpServer (string url)
    {
      if (url == null)
        throw new ArgumentNullException ("url");

      if (url.Length == 0)
        throw new ArgumentException ("An empty string.", "url");

      Uri uri;
      string msg;
      if (!tryCreateUri (url, out uri, out msg))
        throw new ArgumentException (msg, "url");

      var host = getHost (uri);
      var addr = host.ToIPAddress ();
      if (!addr.IsLocal ())
        throw new ArgumentException ("The host part isn't a local host name: " + url, "url");

      init (host, addr, uri.Port, uri.Scheme == "https");
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpServer"/> class with
    /// the specified <paramref name="port"/> and <paramref name="secure"/>.
    /// </summary>
    /// <remarks>
    /// An instance initialized by this constructor listens for the incoming requests on
    /// <paramref name="port"/>.
    /// </remarks>
    /// <param name="port">
    /// An <see cref="int"/> that represents the port number on which to listen.
    /// </param>
    /// <param name="secure">
    /// A <see cref="bool"/> that indicates providing a secure connection or not.
    /// (<c>true</c> indicates providing a secure connection.)
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="port"/> isn't between 1 and 65535 inclusive.
    /// </exception>
    public HttpServer (int port, bool secure)
    {
      if (!port.IsPortNumber ())
        throw new ArgumentOutOfRangeException (
          "port", "Not between 1 and 65535 inclusive: " + port);

      init ("*", System.Net.IPAddress.Any, port, secure);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpServer"/> class with
    /// the specified <paramref name="address"/> and <paramref name="port"/>.
    /// </summary>
    /// <remarks>
    ///   <para>
    ///   An instance initialized by this constructor listens for the incoming requests on
    ///   <paramref name="address"/> and <paramref name="port"/>.
    ///   </para>
    ///   <para>
    ///   If <paramref name="port"/> is 443, that instance provides a secure connection.
    ///   </para>
    /// </remarks>
    /// <param name="address">
    /// A <see cref="System.Net.IPAddress"/> that represents the local IP address of the server.
    /// </param>
    /// <param name="port">
    /// An <see cref="int"/> that represents the port number on which to listen.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="address"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="address"/> isn't a local IP address.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="port"/> isn't between 1 and 65535 inclusive.
    /// </exception>
    public HttpServer (System.Net.IPAddress address, int port)
      : this (address, port, port == 443)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpServer"/> class with
    /// the specified <paramref name="address"/>, <paramref name="port"/>,
    /// and <paramref name="secure"/>.
    /// </summary>
    /// <remarks>
    /// An instance initialized by this constructor listens for the incoming requests on
    /// <paramref name="address"/> and <paramref name="port"/>.
    /// </remarks>
    /// <param name="address">
    /// A <see cref="System.Net.IPAddress"/> that represents the local IP address of the server.
    /// </param>
    /// <param name="port">
    /// An <see cref="int"/> that represents the port number on which to listen.
    /// </param>
    /// <param name="secure">
    /// A <see cref="bool"/> that indicates providing a secure connection or not.
    /// (<c>true</c> indicates providing a secure connection.)
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="address"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="address"/> isn't a local IP address.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="port"/> isn't between 1 and 65535 inclusive.
    /// </exception>
    public HttpServer (System.Net.IPAddress address, int port, bool secure)
    {
      if (address == null)
        throw new ArgumentNullException ("address");

      if (!address.IsLocal ())
        throw new ArgumentException ("Not a local IP address: " + address, "address");

      if (!port.IsPortNumber ())
        throw new ArgumentOutOfRangeException (
          "port", "Not between 1 and 65535 inclusive: " + port);

      init (null, address, port, secure);
    }

    #endregion

    #region Public Properties

    /// <summary>
    /// Gets the local IP address of the server.
    /// </summary>
    /// <value>
    /// A <see cref="System.Net.IPAddress"/> that represents the local IP address of the server.
    /// </value>
    public System.Net.IPAddress Address {
      get {
        return _address;
      }
    }

    /// <summary>
    /// Gets or sets the scheme used to authenticate the clients.
    /// </summary>
    /// <remarks>
    /// The set operation does nothing if the server has already
    /// started or it is shutting down.
    /// </remarks>
    /// <value>
    ///   <para>
    ///   One of the <see cref="WebSocketSharp.Net.AuthenticationSchemes"/>
    ///   enum values.
    ///   </para>
    ///   <para>
    ///   It represents the scheme used to authenticate the clients.
    ///   </para>
    ///   <para>
    ///   The default value is
    ///   <see cref="WebSocketSharp.Net.AuthenticationSchemes.Anonymous"/>.
    ///   </para>
    /// </value>
    public AuthenticationSchemes AuthenticationSchemes {
      get {
        return _listener.AuthenticationSchemes;
      }

      set {
        string msg;
        if (!canSet (out msg)) {
          _log.Warn (msg);
          return;
        }

        lock (_sync) {
          if (!canSet (out msg)) {
            _log.Warn (msg);
            return;
          }

          _listener.AuthenticationSchemes = value;
        }
      }
    }

    /// <summary>
    /// Gets a value indicating whether the server has started.
    /// </summary>
    /// <value>
    /// <c>true</c> if the server has started; otherwise, <c>false</c>.
    /// </value>
    public bool IsListening {
      get {
        return _state == ServerState.Start;
      }
    }

    /// <summary>
    /// Gets a value indicating whether the server provides a secure connection.
    /// </summary>
    /// <value>
    /// <c>true</c> if the server provides a secure connection; otherwise, <c>false</c>.
    /// </value>
    public bool IsSecure {
      get {
        return _secure;
      }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the server cleans up
    /// the inactive sessions periodically.
    /// </summary>
    /// <remarks>
    /// The set operation does nothing if the server has already
    /// started or it is shutting down.
    /// </remarks>
    /// <value>
    ///   <para>
    ///   <c>true</c> if the server cleans up the inactive sessions
    ///   every 60 seconds; otherwise, <c>false</c>.
    ///   </para>
    ///   <para>
    ///   The default value is <c>true</c>.
    ///   </para>
    /// </value>
    public bool KeepClean {
      get {
        return _services.KeepClean;
      }

      set {
        string msg;
        if (!canSet (out msg)) {
          _log.Warn (msg);
          return;
        }

        lock (_sync) {
          if (!canSet (out msg)) {
            _log.Warn (msg);
            return;
          }

          _services.KeepClean = value;
        }
      }
    }

    /// <summary>
    /// Gets the logging functions.
    /// </summary>
    /// <remarks>
    /// The default logging level is <see cref="LogLevel.Error"/>. If you would like to change it,
    /// you should set the <c>Log.Level</c> property to any of the <see cref="LogLevel"/> enum
    /// values.
    /// </remarks>
    /// <value>
    /// A <see cref="Logger"/> that provides the logging functions.
    /// </value>
    public Logger Log {
      get {
        return _log;
      }
    }

    /// <summary>
    /// Gets the port on which to listen for incoming requests.
    /// </summary>
    /// <value>
    /// An <see cref="int"/> that represents the port number on which to listen.
    /// </value>
    public int Port {
      get {
        return _port;
      }
    }

    /// <summary>
    /// Gets or sets the realm used for authentication.
    /// </summary>
    /// <remarks>
    ///   <para>
    ///   SECRET AREA will be used as the name if the value is
    ///   <see langword="null"/> or an empty string.
    ///   </para>
    ///   <para>
    ///   The set operation does nothing if the server has already
    ///   started or it is shutting down.
    ///   </para>
    /// </remarks>
    /// <value>
    ///   <para>
    ///   A <see cref="string"/> or <see langword="null"/>
    ///   by default.
    ///   </para>
    ///   <para>
    ///   That string represents the name of the realm.
    ///   </para>
    /// </value>
    public string Realm {
      get {
        return _listener.Realm;
      }

      set {
        string msg;
        if (!canSet (out msg)) {
          _log.Warn (msg);
          return;
        }

        lock (_sync) {
          if (!canSet (out msg)) {
            _log.Warn (msg);
            return;
          }

          _listener.Realm = value;
        }
      }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the server is allowed to
    /// be bound to an address that is already in use.
    /// </summary>
    /// <remarks>
    ///   <para>
    ///   You should set this property to <c>true</c> if you would
    ///   like to resolve to wait for socket in TIME_WAIT state.
    ///   </para>
    ///   <para>
    ///   The set operation does nothing if the server has already
    ///   started or it is shutting down.
    ///   </para>
    /// </remarks>
    /// <value>
    ///   <para>
    ///   <c>true</c> if the server is allowed to be bound to an address
    ///   that is already in use; otherwise, <c>false</c>.
    ///   </para>
    ///   <para>
    ///   The default value is <c>false</c>.
    ///   </para>
    /// </value>
    public bool ReuseAddress {
      get {
        return _listener.ReuseAddress;
      }

      set {
        string msg;
        if (!canSet (out msg)) {
          _log.Warn (msg);
          return;
        }

        lock (_sync) {
          if (!canSet (out msg)) {
            _log.Warn (msg);
            return;
          }

          _listener.ReuseAddress = value;
        }
      }
    }

    /// <summary>
    /// Gets or sets the document root path of the server.
    /// </summary>
    /// <value>
    /// A <see cref="string"/> that represents the document root path of the server.
    /// The default value is <c>"./Public"</c>.
    /// </value>
    public string RootPath {
      get {
        return _rootPath != null && _rootPath.Length > 0 ? _rootPath : (_rootPath = "./Public");
      }

      set {
        var msg = _state.CheckIfAvailable (true, false, false);
        if (msg != null) {
          _log.Error (msg);
          return;
        }

        _rootPath = value;
      }
    }

    /// <summary>
    /// Gets or sets the SSL configuration used to authenticate the server and
    /// optionally the client for secure connection.
    /// </summary>
    /// <value>
    /// A <see cref="ServerSslConfiguration"/> that represents the configuration used to
    /// authenticate the server and optionally the client for secure connection.
    /// </value>
    public ServerSslConfiguration SslConfiguration {
      get {
        return _listener.SslConfiguration;
      }

      set {
        var msg = _state.CheckIfAvailable (true, false, false);
        if (msg != null) {
          _log.Error (msg);
          return;
        }

        _listener.SslConfiguration = value;
      }
    }

    /// <summary>
    /// Gets or sets the delegate called to find the credentials for an identity used to
    /// authenticate a client.
    /// </summary>
    /// <value>
    /// A <c>Func&lt;<see cref="IIdentity"/>, <see cref="NetworkCredential"/>&gt;</c> delegate
    /// that references the method(s) used to find the credentials. The default value is
    /// <see langword="null"/>.
    /// </value>
    public Func<IIdentity, NetworkCredential> UserCredentialsFinder {
      get {
        return _listener.UserCredentialsFinder;
      }

      set {
        var msg = _state.CheckIfAvailable (true, false, false);
        if (msg != null) {
          _log.Error (msg);
          return;
        }

        _listener.UserCredentialsFinder = value;
      }
    }

    /// <summary>
    /// Gets or sets the wait time for the response to the WebSocket Ping or Close.
    /// </summary>
    /// <value>
    /// A <see cref="TimeSpan"/> that represents the wait time. The default value is
    /// the same as 1 second.
    /// </value>
    public TimeSpan WaitTime {
      get {
        return _services.WaitTime;
      }

      set {
        var msg = _state.CheckIfAvailable (true, false, false) ?? value.CheckIfValidWaitTime ();
        if (msg != null) {
          _log.Error (msg);
          return;
        }

        _services.WaitTime = value;
      }
    }

    /// <summary>
    /// Gets the access to the WebSocket services provided by the server.
    /// </summary>
    /// <value>
    /// A <see cref="WebSocketServiceManager"/> that manages the WebSocket services.
    /// </value>
    public WebSocketServiceManager WebSocketServices {
      get {
        return _services;
      }
    }

    #endregion

    #region Public Events

    /// <summary>
    /// Occurs when the server receives an HTTP CONNECT request.
    /// </summary>
    public event EventHandler<HttpRequestEventArgs> OnConnect;

    /// <summary>
    /// Occurs when the server receives an HTTP DELETE request.
    /// </summary>
    public event EventHandler<HttpRequestEventArgs> OnDelete;

    /// <summary>
    /// Occurs when the server receives an HTTP GET request.
    /// </summary>
    public event EventHandler<HttpRequestEventArgs> OnGet;

    /// <summary>
    /// Occurs when the server receives an HTTP HEAD request.
    /// </summary>
    public event EventHandler<HttpRequestEventArgs> OnHead;

    /// <summary>
    /// Occurs when the server receives an HTTP OPTIONS request.
    /// </summary>
    public event EventHandler<HttpRequestEventArgs> OnOptions;

    /// <summary>
    /// Occurs when the server receives an HTTP PATCH request.
    /// </summary>
    public event EventHandler<HttpRequestEventArgs> OnPatch;

    /// <summary>
    /// Occurs when the server receives an HTTP POST request.
    /// </summary>
    public event EventHandler<HttpRequestEventArgs> OnPost;

    /// <summary>
    /// Occurs when the server receives an HTTP PUT request.
    /// </summary>
    public event EventHandler<HttpRequestEventArgs> OnPut;

    /// <summary>
    /// Occurs when the server receives an HTTP TRACE request.
    /// </summary>
    public event EventHandler<HttpRequestEventArgs> OnTrace;

    #endregion

    #region Private Methods

    private void abort ()
    {
      lock (_sync) {
        if (_state != ServerState.Start)
          return;

        _state = ServerState.ShuttingDown;
      }

      try {
        try {
          _services.Stop (1006, String.Empty);
        }
        finally {
          _listener.Abort ();
        }
      }
      catch {
      }

      _state = ServerState.Stop;
    }

    private bool canSet (out string message)
    {
      message = null;

      if (_state == ServerState.Start) {
        message = "The server has already started.";
        return false;
      }

      if (_state == ServerState.ShuttingDown) {
        message = "The server is shutting down.";
        return false;
      }

      return true;
    }

    private bool checkCertificate (out string message)
    {
      message = null;

      if (!_secure)
        return true;

      var user = _listener.SslConfiguration.ServerCertificate != null;

      var path = _listener.CertificateFolderPath;
      var port = EndPointListener.CertificateExists (_port, path);

      if (user && port) {
        _log.Warn ("The certificate associated with the port will be used.");
        return true;
      }

      if (!(user || port)) {
        message = "There is no certificate used to authenticate the server.";
        return false;
      }

      return true;
    }

    private bool checkIfAvailable (
      bool ready, bool start, bool shutting, bool stop, out string message
    )
    {
      message = null;

      if (!ready && _state == ServerState.Ready) {
        message = "This operation is not available in: ready";
        return false;
      }

      if (!start && _state == ServerState.Start) {
        message = "This operation is not available in: start";
        return false;
      }

      if (!shutting && _state == ServerState.ShuttingDown) {
        message = "This operation is not available in: shutting down";
        return false;
      }

      if (!stop && _state == ServerState.Stop) {
        message = "This operation is not available in: stop";
        return false;
      }

      return true;
    }

    private static string convertToString (System.Net.IPAddress address)
    {
      return address.AddressFamily == AddressFamily.InterNetworkV6
             ? String.Format ("[{0}]", address.ToString ())
             : address.ToString ();
    }

    private static string getHost (Uri uri)
    {
      return uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.DnsSafeHost;
    }

    private void init (string hostname, System.Net.IPAddress address, int port, bool secure)
    {
      _hostname = hostname ?? convertToString (address);
      _address = address;
      _port = port;
      _secure = secure;

      _listener = new HttpListener ();
      _listener.Prefixes.Add (
        String.Format ("http{0}://{1}:{2}/", secure ? "s" : "", _hostname, port));

      _log = _listener.Log;
      _services = new WebSocketServiceManager (_log);
      _sync = new object ();

      var os = Environment.OSVersion;
      _windows = os.Platform != PlatformID.Unix && os.Platform != PlatformID.MacOSX;
    }

    private void processRequest (HttpListenerContext context)
    {
      var method = context.Request.HttpMethod;
      var evt = method == "GET"
                ? OnGet
                : method == "HEAD"
                  ? OnHead
                  : method == "POST"
                    ? OnPost
                    : method == "PUT"
                      ? OnPut
                      : method == "DELETE"
                        ? OnDelete
                        : method == "OPTIONS"
                          ? OnOptions
                          : method == "TRACE"
                            ? OnTrace
                            : method == "CONNECT"
                              ? OnConnect
                              : method == "PATCH"
                                ? OnPatch
                                : null;

      if (evt != null)
        evt (this, new HttpRequestEventArgs (context));
      else
        context.Response.StatusCode = (int) HttpStatusCode.NotImplemented;

      context.Response.Close ();
    }

    private void processRequest (HttpListenerWebSocketContext context)
    {
      WebSocketServiceHost host;
      if (!_services.InternalTryGetServiceHost (context.RequestUri.AbsolutePath, out host)) {
        context.Close (HttpStatusCode.NotImplemented);
        return;
      }

      host.StartSession (context);
    }

    private void receiveRequest ()
    {
      while (true) {
        HttpListenerContext ctx = null;
        try {
          ctx = _listener.GetContext ();
          ThreadPool.QueueUserWorkItem (
            state => {
              try {
                if (ctx.Request.IsUpgradeTo ("websocket")) {
                  processRequest (ctx.AcceptWebSocket (null));
                  return;
                }

                processRequest (ctx);
              }
              catch (Exception ex) {
                _log.Fatal (ex.Message);
                _log.Debug (ex.ToString ());

                ctx.Connection.Close (true);
              }
            }
          );
        }
        catch (HttpListenerException ex) {
          if (_state == ServerState.ShuttingDown) {
            _log.Info ("The receiving is stopped.");
            break;
          }

          _log.Fatal (ex.Message);
          _log.Debug (ex.ToString ());

          break;
        }
        catch (Exception ex) {
          _log.Fatal (ex.Message);
          _log.Debug (ex.ToString ());

          if (ctx != null)
            ctx.Connection.Close (true);

          break;
        }
      }

      if (_state != ServerState.ShuttingDown)
        abort ();
    }

    private void start ()
    {
      if (_state == ServerState.Start) {
        _log.Info ("The server has already started.");
        return;
      }

      if (_state == ServerState.ShuttingDown) {
        _log.Warn ("The server is shutting down.");
        return;
      }

      lock (_sync) {
        if (_state == ServerState.Start) {
          _log.Info ("The server has already started.");
          return;
        }

        if (_state == ServerState.ShuttingDown) {
          _log.Warn ("The server is shutting down.");
          return;
        }

        _services.Start ();

        try {
          startReceiving ();
        }
        catch {
          _services.Stop (1011, String.Empty);
          throw;
        }

        _state = ServerState.Start;
      }
    }

    private void startReceiving ()
    {
      _listener.Start ();
      _receiveThread = new Thread (new ThreadStart (receiveRequest));
      _receiveThread.IsBackground = true;
      _receiveThread.Start ();
    }

    private void stop (ushort code, string reason)
    {
      if (_state == ServerState.Ready) {
        _log.Info ("The server is not started.");
        return;
      }

      if (_state == ServerState.ShuttingDown) {
        _log.Info ("The server is shutting down.");
        return;
      }

      if (_state == ServerState.Stop) {
        _log.Info ("The server has already stopped.");
        return;
      }

      lock (_sync) {
        if (_state == ServerState.ShuttingDown) {
          _log.Info ("The server is shutting down.");
          return;
        }

        if (_state == ServerState.Stop) {
          _log.Info ("The server has already stopped.");
          return;
        }

        _state = ServerState.ShuttingDown;
      }

      try {
        var threw = false;
        try {
          _services.Stop (code, reason);
        }
        catch {
          threw = true;
          throw;
        }
        finally {
          try {
            stopReceiving (5000);
          }
          catch {
            if (!threw)
              throw;
          }
        }
      }
      finally {
        _state = ServerState.Stop;
      }
    }

    private void stopReceiving (int millisecondsTimeout)
    {
      _listener.Close ();
      _receiveThread.Join (millisecondsTimeout);
    }

    private static bool tryCreateUri (string uriString, out Uri result, out string message)
    {
      result = null;

      var uri = uriString.ToUri ();
      if (uri == null) {
        message = "An invalid URI string: " + uriString;
        return false;
      }

      if (!uri.IsAbsoluteUri) {
        message = "Not an absolute URI: " + uriString;
        return false;
      }

      var schm = uri.Scheme;
      if (!(schm == "http" || schm == "https")) {
        message = "The scheme part isn't 'http' or 'https': " + uriString;
        return false;
      }

      if (uri.PathAndQuery != "/") {
        message = "Includes the path or query component: " + uriString;
        return false;
      }

      if (uri.Fragment.Length > 0) {
        message = "Includes the fragment component: " + uriString;
        return false;
      }

      if (uri.Port == 0) {
        message = "The port part is zero: " + uriString;
        return false;
      }

      result = uri;
      message = String.Empty;

      return true;
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Adds the WebSocket service with the specified behavior, <paramref name="path"/>,
    /// and <paramref name="initializer"/>.
    /// </summary>
    /// <remarks>
    ///   <para>
    ///   This method converts <paramref name="path"/> to URL-decoded string,
    ///   and removes <c>'/'</c> from tail end of <paramref name="path"/>.
    ///   </para>
    ///   <para>
    ///   <paramref name="initializer"/> returns an initialized specified typed
    ///   <see cref="WebSocketBehavior"/> instance.
    ///   </para>
    /// </remarks>
    /// <param name="path">
    /// A <see cref="string"/> that represents the absolute path to the service to add.
    /// </param>
    /// <param name="initializer">
    /// A <c>Func&lt;T&gt;</c> delegate that references the method used to initialize
    /// a new specified typed <see cref="WebSocketBehavior"/> instance (a new
    /// <see cref="IWebSocketSession"/> instance).
    /// </param>
    /// <typeparam name="TBehavior">
    /// The type of the behavior of the service to add. The TBehavior must inherit
    /// the <see cref="WebSocketBehavior"/> class.
    /// </typeparam>
    public void AddWebSocketService<TBehavior> (string path, Func<TBehavior> initializer)
      where TBehavior : WebSocketBehavior
    {
      var msg = path.CheckIfValidServicePath () ??
                (initializer == null ? "'initializer' is null." : null);

      if (msg != null) {
        _log.Error (msg);
        return;
      }

      _services.Add<TBehavior> (path, initializer);
    }

    /// <summary>
    /// Adds a WebSocket service with the specified behavior and <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// This method converts <paramref name="path"/> to URL-decoded string,
    /// and removes <c>'/'</c> from tail end of <paramref name="path"/>.
    /// </remarks>
    /// <param name="path">
    /// A <see cref="string"/> that represents the absolute path to the service to add.
    /// </param>
    /// <typeparam name="TBehaviorWithNew">
    /// The type of the behavior of the service to add. The TBehaviorWithNew must inherit
    /// the <see cref="WebSocketBehavior"/> class, and must have a public parameterless
    /// constructor.
    /// </typeparam>
    public void AddWebSocketService<TBehaviorWithNew> (string path)
      where TBehaviorWithNew : WebSocketBehavior, new ()
    {
      AddWebSocketService<TBehaviorWithNew> (path, () => new TBehaviorWithNew ());
    }

    /// <summary>
    /// Gets the contents of the file with the specified <paramref name="path"/>.
    /// </summary>
    /// <returns>
    /// An array of <see cref="byte"/> that receives the contents of the file,
    /// or <see langword="null"/> if it doesn't exist.
    /// </returns>
    /// <param name="path">
    /// A <see cref="string"/> that represents the virtual path to the file to find.
    /// </param>
    public byte[] GetFile (string path)
    {
      path = RootPath + path;
      if (_windows)
        path = path.Replace ("/", "\\");

      return File.Exists (path) ? File.ReadAllBytes (path) : null;
    }

    /// <summary>
    /// Removes the WebSocket service with the specified <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// This method converts <paramref name="path"/> to URL-decoded string,
    /// and removes <c>'/'</c> from tail end of <paramref name="path"/>.
    /// </remarks>
    /// <returns>
    /// <c>true</c> if the service is successfully found and removed; otherwise, <c>false</c>.
    /// </returns>
    /// <param name="path">
    /// A <see cref="string"/> that represents the absolute path to the service to find.
    /// </param>
    public bool RemoveWebSocketService (string path)
    {
      var msg = path.CheckIfValidServicePath ();
      if (msg != null) {
        _log.Error (msg);
        return false;
      }

      return _services.Remove (path);
    }

    /// <summary>
    /// Starts receiving incoming requests.
    /// </summary>
    /// <remarks>
    /// This method does nothing if the server has already
    /// started or it is shutting down.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// There is no certificate used to authenticate the server.
    /// </exception>
    public void Start ()
    {
      string msg;
      if (!checkCertificate (out msg))
        throw new InvalidOperationException (msg);

      start ();
    }

    /// <summary>
    /// Stops receiving incoming requests and closes each connection.
    /// </summary>
    /// <remarks>
    /// This method does nothing if the server is not started,
    /// it is shutting down, or it has already stopped.
    /// </remarks>
    public void Stop ()
    {
      stop (1005, String.Empty);
    }

    /// <summary>
    /// Stops receiving incoming requests and closes each connection.
    /// </summary>
    /// <remarks>
    /// This method does nothing if the server is not started,
    /// it is shutting down, or it has already stopped.
    /// </remarks>
    /// <param name="code">
    ///   <para>
    ///   A <see cref="ushort"/> that represents the status code
    ///   indicating the reason for the WebSocket connection close.
    ///   </para>
    ///   <para>
    ///   The status codes are defined in
    ///   <see href="http://tools.ietf.org/html/rfc6455#section-7.4">
    ///   Section 7.4</see> of RFC 6455.
    ///   </para>
    /// </param>
    /// <param name="reason">
    ///   <para>
    ///   A <see cref="string"/> that represents the reason for
    ///   the WebSocket connection close.
    ///   </para>
    ///   <para>
    ///   The size must be 123 bytes or less in UTF-8.
    ///   </para>
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///   <para>
    ///   <paramref name="code"/> is less than 1000 or greater than 4999.
    ///   </para>
    ///   <para>
    ///   -or-
    ///   </para>
    ///   <para>
    ///   The size of <paramref name="reason"/> is greater than 123 bytes.
    ///   </para>
    /// </exception>
    /// <exception cref="ArgumentException">
    ///   <para>
    ///   <paramref name="code"/> is 1010 (mandatory extension).
    ///   </para>
    ///   <para>
    ///   -or-
    ///   </para>
    ///   <para>
    ///   <paramref name="code"/> is 1005 (no status) and
    ///   there is <paramref name="reason"/>.
    ///   </para>
    ///   <para>
    ///   -or-
    ///   </para>
    ///   <para>
    ///   <paramref name="reason"/> could not be UTF-8-encoded.
    ///   </para>
    /// </exception>
    public void Stop (ushort code, string reason)
    {
      if (!code.IsCloseStatusCode ()) {
        var msg = "Less than 1000 or greater than 4999.";
        throw new ArgumentOutOfRangeException ("code", msg);
      }

      if (code == 1010) {
        var msg = "1010 cannot be used.";
        throw new ArgumentException (msg, "code");
      }

      if (!reason.IsNullOrEmpty ()) {
        if (code == 1005) {
          var msg = "1005 cannot be used.";
          throw new ArgumentException (msg, "code");
        }

        byte[] bytes;
        if (!reason.TryGetUTF8EncodedBytes (out bytes)) {
          var msg = "It could not be UTF-8-encoded.";
          throw new ArgumentException (msg, "reason");
        }

        if (bytes.Length > 123) {
          var msg = "Its size is greater than 123 bytes.";
          throw new ArgumentOutOfRangeException ("reason", msg);
        }
      }

      stop (code, reason);
    }

    /// <summary>
    /// Stops receiving incoming requests and closes each connection.
    /// </summary>
    /// <remarks>
    /// This method does nothing if the server is not started,
    /// it is shutting down, or it has already stopped.
    /// </remarks>
    /// <param name="code">
    ///   <para>
    ///   One of the <see cref="CloseStatusCode"/> enum values.
    ///   </para>
    ///   <para>
    ///   It represents the status code indicating the reason for
    ///   the WebSocket connection close.
    ///   </para>
    /// </param>
    /// <param name="reason">
    ///   <para>
    ///   A <see cref="string"/> that represents the reason for
    ///   the WebSocket connection close.
    ///   </para>
    ///   <para>
    ///   The size must be 123 bytes or less in UTF-8.
    ///   </para>
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The size of <paramref name="reason"/> is greater than 123 bytes.
    /// </exception>
    /// <exception cref="ArgumentException">
    ///   <para>
    ///   <paramref name="code"/> is
    ///   <see cref="CloseStatusCode.MandatoryExtension"/>.
    ///   </para>
    ///   <para>
    ///   -or-
    ///   </para>
    ///   <para>
    ///   <paramref name="code"/> is
    ///   <see cref="CloseStatusCode.NoStatus"/> and
    ///   there is <paramref name="reason"/>.
    ///   </para>
    ///   <para>
    ///   -or-
    ///   </para>
    ///   <para>
    ///   <paramref name="reason"/> could not be UTF-8-encoded.
    ///   </para>
    /// </exception>
    public void Stop (CloseStatusCode code, string reason)
    {
      if (code == CloseStatusCode.MandatoryExtension) {
        var msg = "MandatoryExtension cannot be used.";
        throw new ArgumentException (msg, "code");
      }

      if (!reason.IsNullOrEmpty ()) {
        if (code == CloseStatusCode.NoStatus) {
          var msg = "NoStatus cannot be used.";
          throw new ArgumentException (msg, "code");
        }

        byte[] bytes;
        if (!reason.TryGetUTF8EncodedBytes (out bytes)) {
          var msg = "It could not be UTF-8-encoded.";
          throw new ArgumentException (msg, "reason");
        }

        if (bytes.Length > 123) {
          var msg = "Its size is greater than 123 bytes.";
          throw new ArgumentOutOfRangeException ("reason", msg);
        }
      }

      stop ((ushort) code, reason);
    }

    #endregion
  }
}
