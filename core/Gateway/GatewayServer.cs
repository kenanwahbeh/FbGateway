using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Threading;
using System.Threading.Tasks;
using FirebirdSql.Data.FirebirdClient;
using ByteBridge.Configuration;
using ByteBridge.Data;

namespace ByteBridge.Gateway;

/*
 * The HTTP front door for the configured Firebird databases.
 *
 * Built on HttpListener rather than Kestrel so the app keeps
 * its current dependency set and its current publish layout:
 * no ASP.NET Core runtime to ship alongside the WPF app.
 *
 * Connections are read from SQLite on every request, so turning
 * one Online or Offline in the window takes effect immediately
 * without restarting the listener.
 */
public sealed class GatewayServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            PropertyNamingPolicy =
                JsonNamingPolicy.CamelCase,

            PropertyNameCaseInsensitive = true,

            /*
             * Emit non-ASCII text as itself rather than as a
             * \uXXXX escape.
             *
             * Arabic column values are the normal case here, and
             * escaping every character of them roughly triples
             * the payload that has to cross the tunnel.
             */
            Encoder =
                JavaScriptEncoder.Create(UnicodeRanges.All)
        };

    /*
     * A request body larger than this is refused outright. The
     * gateway takes SQL and parameters, never uploads.
     */
    private const int MaxRequestBytes = 1024 * 1024;

    /*
     * How much of an oversized body is drained so the 413 can be
     * delivered cleanly. Beyond this the connection is dropped.
     */
    private const int MaxDrainBytes = 64 * 1024 * 1024;

    /*
     * How much of the body is read before answering a request that is
     * being refused without being parsed.
     */
    private const int MaxEarlyDrainBytes = 64 * 1024;

    private readonly SqliteDatabase _database;

    private readonly object _sync = new();

    private HttpListener? _listener;

    private CancellationTokenSource? _cancellation;

    private Task? _acceptLoop;

    private volatile GatewayConfig _config;

    private volatile OAuthConfig _oauthConfig;

    private readonly RequestLog _log;

    private readonly CloudflareAccessValidator? _oauthValidator;

    private readonly OAuthSessionManager? _sessionManager;

    public GatewayServer(
        SqliteDatabase database,
        RequestLog? log = null,
        CloudflareAccessValidator? oauthValidator = null,
        OAuthSessionManager? sessionManager = null)
    {
        _database = database;

        _config = database.GetGatewayConfig();

        _oauthConfig = database.GetOAuthConfig();

        _log = log ?? new RequestLog(database.LogDirectory);

        _oauthValidator = oauthValidator;

        _sessionManager = sessionManager;
    }

    /*
     * The audit trail. Exposed so the window can point someone at it and
     * show the most recent requests.
     */
    public RequestLog Log => _log;

    public GatewayConfig Config => _config;

    public bool IsRunning =>
        _listener?.IsListening == true;

    /*
     * Rotates the key in place so a running listener starts
     * rejecting the old one without being restarted.
     */
    public void UpdateApiKey(string apiKey)
    {
        _config.ApiKey = apiKey;
    }

    public void Start(GatewayConfig config)
    {
        lock (_sync)
        {
            if (IsRunning)
            {
                return;
            }

            var listener = new HttpListener();

            listener.Prefixes.Add(config.Prefix);

            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                listener.Close();

                throw new InvalidOperationException(
                    DescribeListenerFailure(ex, config),
                    ex);
            }

            _config = config;
            _listener = listener;
            _cancellation = new CancellationTokenSource();

            var token = _cancellation.Token;

            _acceptLoop =
                Task.Run(
                    () => AcceptLoopAsync(listener, token));
        }
    }

    public void Stop()
    {
        HttpListener? listener;
        CancellationTokenSource? cancellation;
        Task? acceptLoop;

        lock (_sync)
        {
            listener = _listener;
            cancellation = _cancellation;
            acceptLoop = _acceptLoop;

            _listener = null;
            _cancellation = null;
            _acceptLoop = null;
        }

        if (listener == null)
        {
            return;
        }

        cancellation?.Cancel();

        /*
         * Stopping the listener is what unblocks the pending
         * GetContextAsync, so the loop is awaited afterwards.
         */
        try
        {
            listener.Stop();
            listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            acceptLoop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        cancellation?.Dispose();
    }

    public void Dispose()
    {
        Stop();
    }

    /*
     * Turns the Windows level failures into something a user can
     * act on, instead of "Access is denied".
     */
    private static string DescribeListenerFailure(
        HttpListenerException exception,
        GatewayConfig config)
    {
        return exception.ErrorCode switch
        {
            5 =>
                $"Windows refused to reserve {config.Prefix}\n\n" +
                "Either run ByteBridge as administrator once, or grant the " +
                "reservation from an elevated prompt:\n\n" +
                $"netsh http add urlacl url={config.Prefix} user=\"%USERNAME%\"",

            32 or 183 =>
                $"Port {config.Port} is already in use by another program.\n\n" +
                "Pick a different port, or stop whatever is holding it:\n\n" +
                $"netstat -ano | findstr :{config.Port}",

            _ => exception.Message
        };
    }

    private async Task AcceptLoopAsync(
        HttpListener listener,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (
                cancellationToken.IsCancellationRequested
                || !listener.IsListening)
            {
                // The listener was stopped.
                return;
            }
            catch (Exception)
            {
                /*
                 * Anything else, on a listener that is still started, is
                 * about one connection rather than the listener. This
                 * loop used to return on it, which ended accepting for
                 * good while IsRunning still said true: the service saw a
                 * healthy gateway and never rebound it, and every request
                 * after that queued with nothing to take it. So it keeps
                 * accepting, after a pause that stops a failure that
                 * repeats from spinning.
                 */
                try
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(100),
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            _ = Task.Run(
                () => HandleContextAsync(
                    context,
                    cancellationToken),
                CancellationToken.None);
        }
    }

    private async Task HandleContextAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        var record = new RequestRecord
        {
            Method = context.Request.HttpMethod,
            Path = context.Request.Url?.AbsolutePath ?? "/",
            LocalPeer = context.Request.RemoteEndPoint?.Address.ToString(),

            /*
             * Behind a tunnel the socket peer is always cloudflared on
             * loopback, so the address that identifies the caller comes
             * from Cloudflare's header instead.
             */
            ClientIp = context.Request.Headers["CF-Connecting-IP"]
                ?? context.Request.Headers["X-Forwarded-For"]?.Split(',')[0].Trim()
        };

        try
        {
            ApplyCorsHeaders(context.Response);

            var method = context.Request.HttpMethod;

            if (string.Equals(
                    method,
                    "OPTIONS",
                    StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 204;
                return;
            }

            var path = NormalizePath(context.Request.Url);

            /*
             * /health is deliberately unauthenticated: it is how
             * you confirm the tunnel reaches the gateway before
             * any key is involved. It exposes no data.
             */
            if (path == "/health")
            {
                if (method != "GET")
                {
                    await WriteMethodNotAllowedAsync(context, "GET");
                    return;
                }

                /*
                 * Nothing on this path reads a body, so whatever a caller
                 * sent is drained first, the same as every refusal below.
                 * Left in the connection it would corrupt the next
                 * request on it.
                 */
                await DrainOrCloseAsync(context);

                await WriteHealthAsync(context);
                return;
            }

            /*
             * /auth/* endpoints handle OAuth login/logout.
             */
            if (path.StartsWith("/auth/", StringComparison.OrdinalIgnoreCase))
            {
                await HandleAuthAsync(context, path, cancellationToken);
                return;
            }

            record.Authenticated = IsAuthorized(context.Request);

            if (!record.Authenticated)
            {
                await DrainOrCloseAsync(context);

                await WriteJsonAsync(
                    context,
                    401,
                    new ErrorResponse(
                        "Missing or invalid API key. " +
                        "Send it in the X-API-Key header."));

                return;
            }

            switch (path)
            {
                case "/databases":

                    if (method != "GET")
                    {
                        await WriteMethodNotAllowedAsync(context, "GET");
                        return;
                    }

                    // No body is read here either; see /health above.
                    await DrainOrCloseAsync(context);

                    await WriteJsonAsync(
                        context,
                        200,
                        BuildDatabaseList());

                    return;

                case "/query":

                    if (method != "POST")
                    {
                        await WriteMethodNotAllowedAsync(context, "POST");
                        return;
                    }

                    await HandleQueryAsync(context, record, cancellationToken);
                    return;

                case "/execute":

                    if (method != "POST")
                    {
                        await WriteMethodNotAllowedAsync(context, "POST");
                        return;
                    }

                    await HandleExecuteAsync(context, record, cancellationToken);
                    return;

                default:

                    await DrainOrCloseAsync(context);

                    await WriteJsonAsync(
                        context,
                        404,
                        new ErrorResponse(
                            $"Unknown endpoint \"{path}\". " +
                            "Available: /health, /databases, /query, /execute."));

                    return;
            }
        }
        catch (Exception ex)
        {
            record.Error = ex.Message;

            try
            {
                await WriteJsonAsync(
                    context,
                    500,
                    new ErrorResponse(ex.Message));
            }
            catch
            {
                // The client is gone; nothing left to report to.
            }
        }
        finally
        {
            try
            {
                record.Status = context.Response.StatusCode;
            }
            catch
            {
                // The response was already disposed.
            }

            try
            {
                context.Response.Close();
            }
            catch
            {
                // Already closed or the client disconnected.
            }

            stopwatch.Stop();

            record.ElapsedMs = stopwatch.ElapsedMilliseconds;

            _log.Write(record);
        }
    }

    private async Task HandleQueryAsync(
        HttpListenerContext context,
        RequestRecord record,
        CancellationToken cancellationToken)
    {
        var body =
            await ReadRequestAsync<QueryRequest>(context);

        if (body.Failed)
        {
            await WriteJsonAsync(
                context,
                body.ErrorStatus,
                new ErrorResponse(body.Error!));

            return;
        }

        var request = body.Value!;

        if (string.IsNullOrWhiteSpace(request.Sql))
        {
            await WriteJsonAsync(
                context,
                400,
                new ErrorResponse("\"sql\" is required."));

            return;
        }

        /*
         * The statement is recorded, the parameters are not: the values
         * are the customer's data, and keeping them separate is the
         * whole point of binding them.
         */
        record.Sql = request.Sql;
        record.Database = request.Database;

        if (!FirebirdExecutor.IsReadOnlyStatement(request.Sql))
        {
            await WriteJsonAsync(
                context,
                400,
                new ErrorResponse(
                    "/query only accepts SELECT or WITH statements. " +
                    "Use /execute for writes."));

            return;
        }

        var connection =
            await ResolveConnectionAsync(
                context,
                request.Database);

        if (connection == null)
        {
            return;
        }

        var config = _config;

        var maxRows =
            Math.Clamp(
                request.MaxRows ?? config.MaxRows,
                1,
                config.MaxRows);

        try
        {
            var result =
                await FirebirdExecutor.QueryAsync(
                    connection,
                    request.Sql,
                    request.Parameters,
                    maxRows,
                    config.CommandTimeoutSeconds,
                    cancellationToken);

            record.Rows = result.RowCount;

            await WriteJsonAsync(context, 200, result);
        }
        catch (FbException ex)
        {
            record.Error = ex.Message;

            await WriteJsonAsync(
                context,
                400,
                new ErrorResponse(ex.Message));
        }
    }

    private async Task HandleExecuteAsync(
        HttpListenerContext context,
        RequestRecord record,
        CancellationToken cancellationToken)
    {
        var body =
            await ReadRequestAsync<QueryRequest>(context);

        if (body.Failed)
        {
            await WriteJsonAsync(
                context,
                body.ErrorStatus,
                new ErrorResponse(body.Error!));

            return;
        }

        var request = body.Value!;

        if (string.IsNullOrWhiteSpace(request.Sql))
        {
            await WriteJsonAsync(
                context,
                400,
                new ErrorResponse("\"sql\" is required."));

            return;
        }

        record.Sql = request.Sql;
        record.Database = request.Database;

        var connection =
            await ResolveConnectionAsync(
                context,
                request.Database);

        if (connection == null)
        {
            return;
        }

        try
        {
            var result =
                await FirebirdExecutor.ExecuteAsync(
                    connection,
                    request.Sql,
                    request.Parameters,
                    _config.CommandTimeoutSeconds,
                    cancellationToken);

            record.Rows = result.RowsAffected;

            await WriteJsonAsync(context, 200, result);
        }
        catch (FbException ex)
        {
            record.Error = ex.Message;

            await WriteJsonAsync(
                context,
                400,
                new ErrorResponse(ex.Message));
        }
    }

    private async Task<DatabaseConfig?> ResolveConnectionAsync(
        HttpListenerContext context,
        string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            await WriteJsonAsync(
                context,
                400,
                new ErrorResponse(
                    "\"database\" is required. " +
                    "Call GET /databases to list the available names."));

            return null;
        }

        var connection =
            _database.FindConnection(identifier, out var sharedName);

        /*
         * A name two connections share, from a settings file written
         * before names had to be unique. Picking either would run the
         * statement -- a write, even -- against a database the caller
         * may never have meant, so the caller is sent to the ids.
         */
        if (sharedName.Count > 1)
        {
            await WriteJsonAsync(
                context,
                400,
                new ErrorResponse(
                    $"\"{identifier}\" is the name of {sharedName.Count} connections. " +
                    "Send the id of the one you mean; GET /databases lists them."));

            return null;
        }

        if (connection == null)
        {
            await WriteJsonAsync(
                context,
                404,
                new ErrorResponse(
                    $"No connection matches \"{identifier}\"."));

            return null;
        }

        if (!connection.Enabled)
        {
            await WriteJsonAsync(
                context,
                409,
                new ErrorResponse(
                    $"Connection \"{connection.Name}\" is offline. " +
                    "Turn it online in ByteBridge first."));

            return null;
        }

        return connection;
    }

    private List<DatabaseSummary> BuildDatabaseList()
    {
        var summaries = new List<DatabaseSummary>();

        foreach (var connection in _database.GetConnections())
        {
            summaries.Add(
                new DatabaseSummary
                {
                    Id = connection.Id,
                    Name = connection.Name,

                    Online =
                        connection.Enabled &&
                        connection.LastTestSuccessful
                });
        }

        return summaries;
    }

    private async Task WriteHealthAsync(
        HttpListenerContext context)
    {
        var connections = _database.GetConnections();

        var online = 0;

        foreach (var connection in connections)
        {
            if (connection.Enabled)
            {
                online++;
            }
        }

        await WriteJsonAsync(
            context,
            200,
            new
            {
                Status = "ok",
                Service = "ByteBridge",
                Connections = connections.Count,
                Online = online,
                TimeUtc = DateTime.UtcNow
            });
    }

    /*
     * Answering without reading the request body leaves those bytes in
     * the connection, so the next request on it is parsed from the
     * middle of this one and comes back as a spurious 400 on a request
     * that was fine. cloudflared holds keep-alive connections to the
     * origin, which is exactly where that would show up.
     *
     * A refused request is usually small -- a statement sent with the
     * wrong key -- so draining it is cheap and leaves the connection
     * reusable and the reply readable. Past the cap the caller is not
     * worth reading from, and dropping the connection is both safe and
     * enough: an unauthenticated sender gets no promise of a reply.
     */
    private static async Task DrainOrCloseAsync(
        HttpListenerContext context)
    {
        if (!context.Request.HasEntityBody)
        {
            return;
        }

        var buffer = new byte[8192];
        var total = 0;

        try
        {
            while (total <= MaxEarlyDrainBytes)
            {
                var read =
                    await context.Request.InputStream.ReadAsync(buffer);

                if (read == 0)
                {
                    // Fully drained; the connection stays usable.
                    return;
                }

                total += read;
            }
        }
        catch (Exception error)
            when (error is IOException or HttpListenerException)
        {
            /*
             * The caller went away mid-body; nothing left to tidy. On
             * Windows that surfaces as an HttpListenerException rather
             * than an IOException, which is why both are caught.
             */
        }

        context.Response.KeepAlive = false;
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        // Check session cookie first
        if (_sessionManager != null &&
            _sessionManager.Enabled)
        {
            var cookieHeader =
                request.Headers["Cookie"];

            var token =
                OAuthSessionManager.ExtractTokenFromCookie(
                    cookieHeader);

            if (token != null)
            {
                var user =
                    _sessionManager.ValidateSession(token);

                if (user != null)
                {
                    return true;
                }
            }
        }

        // Fall back to API key
        var expected = _config.ApiKey;

        if (string.IsNullOrEmpty(expected))
        {
            return false;
        }

        var provided = request.Headers["X-API-Key"];

        if (string.IsNullOrEmpty(provided))
        {
            var authorization =
                request.Headers["Authorization"];

            if (!string.IsNullOrEmpty(authorization) &&
                authorization.StartsWith(
                    "Bearer ",
                    StringComparison.OrdinalIgnoreCase))
            {
                provided =
                    authorization["Bearer ".Length..].Trim();
            }
        }

        if (string.IsNullOrEmpty(provided))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided),
            Encoding.UTF8.GetBytes(expected));
    }

    /*
     * Handles OAuth authentication endpoints.
     *
     * /auth/login   - Redirects to Cloudflare Access login
     * /auth/callback - Handles the OAuth callback
     * /auth/logout  - Clears the session
     * /auth/me      - Returns the current user info
     */
    private async Task HandleAuthAsync(
        HttpListenerContext context,
        string path,
        CancellationToken cancellationToken)
    {
        var method = context.Request.HttpMethod;

        switch (path)
        {
            case "/auth/login":
                await HandleLoginAsync(context, cancellationToken);
                return;

            case "/auth/callback":
                await HandleCallbackAsync(context, cancellationToken);
                return;

            case "/auth/logout":
                await HandleLogoutAsync(context);
                return;

            case "/auth/me":
                await HandleMeAsync(context);
                return;

            default:
                await DrainOrCloseAsync(context);
                await WriteJsonAsync(
                    context,
                    404,
                    new ErrorResponse("Unknown auth endpoint."));
                return;
        }
    }

    /*
     * Redirects the user to Cloudflare Access login page.
     *
     * The user authenticates via their configured identity
     * provider (GitHub, Google, One-time PIN, etc.) and
     * Cloudflare redirects back to /auth/callback with a
     * JWT token.
     */
    private async Task HandleLoginAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        if (_sessionManager == null || !_sessionManager.Enabled)
        {
            await WriteJsonAsync(
                context,
                400,
                new ErrorResponse(
                    "OAuth login is not configured."));
            return;
        }

        /*
         * Build the Cloudflare Access authorization URL.
         *
         * The flow is:
         * 1. Redirect to Cloudflare Access
         * 2. User authenticates with their IdP
         * 3. Cloudflare redirects back with a JWT in the
         *    cf_clearance cookie or as a query parameter
         */
        var redirectUri = _oauthConfig.RedirectUri;
        var teamDomain = _oauthConfig.TeamDomain;

        if (string.IsNullOrEmpty(redirectUri))
        {
            redirectUri =
                $"{_config.BaseUrl}/auth/callback";
        }

        var state = Convert
            .ToHexString(
                System.Security.Cryptography
                    .RandomNumberGenerator.GetBytes(16))
            .ToLowerInvariant();

        var loginUrl =
            $"https://{teamDomain}/cdn-cgi/access/callback" +
            $"?redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&state={state}";

        context.Response.StatusCode = 302;
        context.Response.RedirectLocation = loginUrl;
    }

    /*
     * Handles the OAuth callback from Cloudflare Access.
     *
     * After the user authenticates, Cloudflare redirects to
     * this endpoint with a JWT token. The gateway validates
     * the token and creates a session.
     */
    private async Task HandleCallbackAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        if (_sessionManager == null || !_sessionManager.Enabled)
        {
            await WriteJsonAsync(
                context,
                400,
                new ErrorResponse(
                    "OAuth login is not configured."));
            return;
        }

        if (_oauthValidator == null)
        {
            await WriteJsonAsync(
                context,
                500,
                new ErrorResponse(
                    "OAuth validator is not configured."));
            return;
        }

        /*
         * Cloudflare Access sends the JWT as a query parameter
         * named "cf_clearance_jwt" or in the Authorization
         * header.
         */
        var token = context.Request.QueryString["cf_clearance_jwt"]
            ?? context.Request.Headers["Authorization"];

        if (string.IsNullOrEmpty(token))
        {
            await WriteJsonAsync(
                context,
                400,
                new ErrorResponse(
                    "Missing authentication token."));
            return;
        }

        // Strip "Bearer " prefix if present
        if (token.StartsWith(
                "Bearer ",
                StringComparison.OrdinalIgnoreCase))
        {
            token = token["Bearer ".Length..].Trim();
        }

        // Validate the JWT
        var principal =
            await _oauthValidator.ValidateTokenAsync(token);

        if (principal == null)
        {
            await WriteJsonAsync(
                context,
                401,
                new ErrorResponse(
                    "Invalid or expired authentication token."));
            return;
        }

        // Extract user email
        var email =
            CloudflareAccessValidator.GetEmail(principal);

        if (string.IsNullOrEmpty(email))
        {
            await WriteJsonAsync(
                context,
                401,
                new ErrorResponse(
                    "Could not determine user identity."));
            return;
        }

        // Create a session
        var sessionToken =
            _sessionManager.CreateSession(email);

        // Set the session cookie and redirect to root
        var cookie = OAuthSessionManager.FormatCookie(
            sessionToken,
            _sessionManager.Enabled
                ? _oauthConfig.SessionTimeoutMinutes
                : 60);

        context.Response.Headers["Set-Cookie"] = cookie;
        context.Response.StatusCode = 302;
        context.Response.RedirectLocation = "/";
    }

    /*
     * Clears the session cookie (logout).
     */
    private async Task HandleLogoutAsync(
        HttpListenerContext context)
    {
        if (_sessionManager != null)
        {
            var cookieHeader =
                context.Request.Headers["Cookie"];

            var token =
                OAuthSessionManager.ExtractTokenFromCookie(
                    cookieHeader);

            if (token != null)
            {
                _sessionManager.RevokeSession(token);
            }
        }

        var clearCookie = OAuthSessionManager.ClearCookie();
        context.Response.Headers["Set-Cookie"] = clearCookie;

        context.Response.StatusCode = 302;
        context.Response.RedirectLocation = "/";
    }

    /*
     * Returns the current authenticated user's email.
     */
    private async Task HandleMeAsync(
        HttpListenerContext context)
    {
        var email = GetAuthenticatedUser(context.Request);

        if (string.IsNullOrEmpty(email))
        {
            await WriteJsonAsync(
                context,
                401,
                new ErrorResponse(
                    "Not authenticated."));
            return;
        }

        await WriteJsonAsync(
            context,
            200,
            new { Email = email });
    }

    /*
     * Gets the authenticated user's email from either the
     * API key or the session cookie.
     */
    private string? GetAuthenticatedUser(
        HttpListenerRequest request)
    {
        // Check session cookie first
        if (_sessionManager != null &&
            _sessionManager.Enabled)
        {
            var cookieHeader =
                request.Headers["Cookie"];

            var token =
                OAuthSessionManager.ExtractTokenFromCookie(
                    cookieHeader);

            if (token != null)
            {
                var email =
                    _sessionManager.ValidateSession(token);

                if (email != null)
                {
                    return email;
                }
            }
        }

        // Fall back to API key
        var expected = _config.ApiKey;

        if (string.IsNullOrEmpty(expected))
        {
            return null;
        }

        var provided = request.Headers["X-API-Key"];

        if (string.IsNullOrEmpty(provided))
        {
            var authorization =
                request.Headers["Authorization"];

            if (!string.IsNullOrEmpty(authorization) &&
                authorization.StartsWith(
                    "Bearer ",
                    StringComparison.OrdinalIgnoreCase))
            {
                provided =
                    authorization["Bearer ".Length..].Trim();
            }
        }

        if (string.IsNullOrEmpty(provided))
        {
            return null;
        }

        if (CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(provided),
                Encoding.UTF8.GetBytes(expected)))
        {
            return "api-key";
        }

        return null;
    }

    private static string NormalizePath(Uri? url)
    {
        var path = url?.AbsolutePath ?? "/";

        path = path.TrimEnd('/');

        if (string.IsNullOrEmpty(path))
        {
            path = "/";
        }

        return path.ToLowerInvariant();
    }

    private static void ApplyCorsHeaders(
        HttpListenerResponse response)
    {
        /*
         * The API key travels in a custom header, never a cookie,
         * so a wildcard origin cannot make a browser replay
         * someone else's credentials.
         */
        response.Headers["Access-Control-Allow-Origin"] = "*";

        response.Headers["Access-Control-Allow-Methods"] =
            "GET, POST, OPTIONS";

        response.Headers["Access-Control-Allow-Headers"] =
            "Content-Type, X-API-Key, Authorization";

        response.Headers["Access-Control-Max-Age"] = "86400";
    }

    /*
     * Reads the JSON body under a hard size cap.
     *
     * The cap is enforced while reading rather than from
     * Content-Length alone, so a chunked request cannot get
     * around it by not declaring a length.
     */
    private static async Task<BodyResult<T>> ReadRequestAsync<T>(
        HttpListenerContext context)
    {
        var tooLarge =
            new BodyResult<T>(
                default,
                413,
                $"Request body exceeds the {MaxRequestBytes / 1024} KB limit.");

        using var buffer = new MemoryStream();

        var chunk = new byte[8192];

        var total = 0L;

        var exceeded = false;

        try
        {
            while (true)
            {
                var read =
                    await context.Request.InputStream.ReadAsync(chunk);

                if (read == 0)
                {
                    break;
                }

                total += read;

                /*
                 * Past the cap the body is drained but no longer
                 * stored, so memory stays flat while the client is
                 * still allowed to finish sending.
                 *
                 * Closing the response mid-upload instead would reset
                 * the connection, and the caller would see a broken
                 * pipe -- a 502 through a tunnel -- rather than the
                 * 413 that explains what actually went wrong.
                 */
                if (total > MaxRequestBytes)
                {
                    exceeded = true;

                    /*
                     * Past this point the sender is no longer worth
                     * draining; drop the connection instead.
                     */
                    if (total > MaxDrainBytes)
                    {
                        context.Response.KeepAlive = false;

                        return tooLarge;
                    }

                    continue;
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (Exception error)
            when (error is IOException or HttpListenerException)
        {
            /*
             * The caller went away part-way through the body. That is a
             * truncated request, not a fault in the gateway, so it is
             * answered and logged as one instead of falling through to
             * the 500 handler. Windows reports it as an
             * HttpListenerException, other platforms as an IOException.
             */
            context.Response.KeepAlive = false;

            return new BodyResult<T>(
                default,
                400,
                "The request body ended before it was fully received.");
        }

        if (exceeded)
        {
            return tooLarge;
        }

        if (buffer.Length == 0)
        {
            return new BodyResult<T>(
                default,
                400,
                "Expected a JSON request body.");
        }

        buffer.Position = 0;

        try
        {
            var value =
                await JsonSerializer.DeserializeAsync<T>(
                    buffer,
                    JsonOptions);

            if (value == null)
            {
                return new BodyResult<T>(
                    default,
                    400,
                    "Expected a JSON request body.");
            }

            return new BodyResult<T>(value, 0, null);
        }
        catch (JsonException ex)
        {
            return new BodyResult<T>(
                default,
                400,
                $"Invalid JSON request body: {ex.Message}");
        }
    }

    private readonly record struct BodyResult<T>(
        T? Value,
        int ErrorStatus,
        string? Error)
    {
        public bool Failed => Error != null;
    }

    private static async Task WriteMethodNotAllowedAsync(
        HttpListenerContext context,
        string allowed)
    {
        await DrainOrCloseAsync(context);

        context.Response.Headers["Allow"] =
            allowed + ", OPTIONS";

        await WriteJsonAsync(
            context,
            405,
            new ErrorResponse(
                $"This endpoint only accepts {allowed}."));
    }

    private static async Task WriteJsonAsync(
        HttpListenerContext context,
        int statusCode,
        object payload)
    {
        var json =
            JsonSerializer.SerializeToUtf8Bytes(
                payload,
                JsonOptions);

        context.Response.StatusCode = statusCode;

        context.Response.ContentType =
            "application/json; charset=utf-8";

        context.Response.ContentLength64 = json.Length;

        await context.Response.OutputStream.WriteAsync(json);
    }
}
