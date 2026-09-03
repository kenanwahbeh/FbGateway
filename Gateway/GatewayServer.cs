using System;
using System.Collections.Generic;
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
using FbGateway.Configuration;
using FbGateway.Data;

namespace FbGateway.Gateway;

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

    private readonly SqliteDatabase _database;

    private readonly object _sync = new();

    private HttpListener? _listener;

    private CancellationTokenSource? _cancellation;

    private Task? _acceptLoop;

    private volatile GatewayConfig _config;

    public GatewayServer(SqliteDatabase database)
    {
        _database = database;

        _config = database.GetGatewayConfig();
    }

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
                "Either run Easy FB Soft as administrator once, or grant the " +
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
            catch (HttpListenerException)
            {
                // The listener was stopped.
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
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

                await WriteHealthAsync(context);
                return;
            }

            if (!IsAuthorized(context.Request))
            {
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

                    await HandleQueryAsync(context, cancellationToken);
                    return;

                case "/execute":

                    if (method != "POST")
                    {
                        await WriteMethodNotAllowedAsync(context, "POST");
                        return;
                    }

                    await HandleExecuteAsync(context, cancellationToken);
                    return;

                default:

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
                context.Response.Close();
            }
            catch
            {
                // Already closed or the client disconnected.
            }
        }
    }

    private async Task HandleQueryAsync(
        HttpListenerContext context,
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

            await WriteJsonAsync(context, 200, result);
        }
        catch (FbException ex)
        {
            await WriteJsonAsync(
                context,
                400,
                new ErrorResponse(ex.Message));
        }
    }

    private async Task HandleExecuteAsync(
        HttpListenerContext context,
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

            await WriteJsonAsync(context, 200, result);
        }
        catch (FbException ex)
        {
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
            _database.FindConnection(identifier);

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
                    "Turn it online in Easy FB Soft first."));

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
                Service = "EasyFbSoft",
                Connections = connections.Count,
                Online = online,
                TimeUtc = DateTime.UtcNow
            });
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
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
                    return tooLarge;
                }

                continue;
            }

            buffer.Write(chunk, 0, read);
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

    private static Task WriteMethodNotAllowedAsync(
        HttpListenerContext context,
        string allowed)
    {
        context.Response.Headers["Allow"] =
            allowed + ", OPTIONS";

        return WriteJsonAsync(
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
