using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Xunit;
using ByteBridge.Configuration;
using ByteBridge.Data;
using ByteBridge.Gateway;

namespace ByteBridge.Tests;

/*
 * A throwaway settings folder.
 *
 * SqliteDatabase stores under CommonApplicationData in a real install,
 * so tests pass a temporary root instead. Without it a test run would
 * read and overwrite the connections of whoever is running it.
 */
public sealed class TempDataRoot : IDisposable
{
    public string Path { get; }

    public TempDataRoot()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "bytebridge-tests",
            Guid.NewGuid().ToString("n"));

        Directory.CreateDirectory(Path);
    }

    public SqliteDatabase OpenDatabase() => new(Path);

    public string CurrentDatabasePath =>
        System.IO.Path.Combine(Path, "ByteBridge", "bytebridge.db");

    public void Dispose()
    {
        /*
         * Pooled connections hold the file open, and on Windows that
         * makes the directory undeletable.
         */
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/*
 * A running gateway on its own port, over its own settings folder,
 * seeded with one Online and one Offline connection.
 */
public sealed class GatewayHarness : IDisposable
{
    private readonly TempDataRoot _root;

    public SqliteDatabase Database { get; }

    public GatewayServer Server { get; }

    public RequestLog Log { get; }

    public HttpClient Client { get; }

    public string ApiKey { get; private set; }

    public DatabaseConfig Online { get; }

    public DatabaseConfig Offline { get; }

    public GatewayHarness()
    {
        _root = new TempDataRoot();

        Database = _root.OpenDatabase();

        Online = new DatabaseConfig
        {
            Name = "Sales",
            Server = "127.0.0.1",
            Port = 3050,
            Username = "SYSDBA",
            Password = "not-a-real-password",
            Database = "/data/sales.fdb",
            Enabled = true,
            LastTestSuccessful = true
        };

        Offline = new DatabaseConfig
        {
            Name = "Archive",
            Server = "127.0.0.1",
            Port = 3050,
            Username = "SYSDBA",
            Password = "not-a-real-password",
            Database = "/data/archive.fdb",
            Enabled = false
        };

        Database.AddConnection(Online);
        Database.AddConnection(Offline);

        var config = Database.GetGatewayConfig();
        config.Port = FreePort();

        ApiKey = config.ApiKey;

        Log = new RequestLog(System.IO.Path.Combine(_root.Path, "logs"));

        Server = new GatewayServer(Database, Log);
        Server.Start(config);

        Client = new HttpClient
        {
            BaseAddress = new Uri(config.BaseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public void RotateApiKey()
    {
        ApiKey = Database.RegenerateApiKey();
        Server.UpdateApiKey(ApiKey);
    }

    public Task<HttpResponseMessage> Get(string path, string? key = null) =>
        Send(HttpMethod.Get, path, key);

    public Task<HttpResponseMessage> Post(string path, object? body, string? key = null) =>
        Send(HttpMethod.Post, path, key, body);

    /*
     * key defaults to the harness's current key. Pass "" to send no
     * key at all, or any other string to send a wrong one.
     */
    public Task<HttpResponseMessage> Send(
        HttpMethod method,
        string path,
        string? key = null,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);

        var effective = key ?? ApiKey;

        if (effective.Length > 0)
        {
            request.Headers.Add("X-API-Key", effective);
        }

        if (body != null)
        {
            request.Content = JsonContent.Create(body);
        }

        return Client.SendAsync(request);
    }

    public static async Task<(HttpStatusCode Status, string Body)> Read(
        Task<HttpResponseMessage> send)
    {
        using var response = await send;

        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);

        probe.Start();

        var port = ((IPEndPoint)probe.LocalEndpoint).Port;

        probe.Stop();

        return port;
    }

    public void Dispose()
    {
        Client.Dispose();
        Server.Dispose();
        _root.Dispose();
    }
}

/*
 * Marks a test that needs a real Firebird server.
 *
 * Set BYTEBRIDGE_TEST_FIREBIRD to a connection spec to run them:
 *
 *   host:port:user:password:/path/or/alias
 *
 * Without it the tests report as skipped rather than failing, so a
 * clone with no Firebird still gets a green run.
 */
public sealed class FirebirdFactAttribute : FactAttribute
{
    public FirebirdFactAttribute()
    {
        if (FirebirdTestServer.Connection == null)
        {
            Skip =
                "Set BYTEBRIDGE_TEST_FIREBIRD=host:port:user:password:database " +
                "to run the live Firebird tests.";
        }
    }
}

/*
 * The Theory counterpart of FirebirdFactAttribute, so a parameterised
 * live test reports as skipped rather than quietly passing without
 * having run.
 */
public sealed class FirebirdTheoryAttribute : TheoryAttribute
{
    public FirebirdTheoryAttribute()
    {
        if (FirebirdTestServer.Connection == null)
        {
            Skip =
                "Set BYTEBRIDGE_TEST_FIREBIRD=host:port:user:password:database " +
                "to run the live Firebird tests.";
        }
    }
}

internal static class FirebirdTestServer
{
    public const string Variable = "BYTEBRIDGE_TEST_FIREBIRD";

    public static DatabaseConfig? Connection { get; } = Parse();

    private static DatabaseConfig? Parse()
    {
        var spec = Environment.GetEnvironmentVariable(Variable);

        if (string.IsNullOrWhiteSpace(spec))
        {
            return null;
        }

        /*
         * Split from the left for the first four fields so the database
         * path, which may itself contain colons on Windows, keeps the
         * remainder intact.
         */
        var parts = spec.Split(':', 5);

        if (parts.Length != 5 || !int.TryParse(parts[1], out var port))
        {
            throw new InvalidOperationException(
                $"{Variable} must look like host:port:user:password:database, got '{spec}'.");
        }

        return new DatabaseConfig
        {
            Name = "Test",
            Server = parts[0],
            Port = port,
            Username = parts[2],
            Password = parts[3],
            Database = parts[4],
            Enabled = true,
            LastTestSuccessful = true
        };
    }
}
