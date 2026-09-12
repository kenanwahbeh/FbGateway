using System.Net;
using System.Text.Json;
using Xunit;

namespace ByteBridge.Tests;

/*
 * The log as the running server actually fills it in, rather than as a
 * unit in isolation. Each test drives a real request over a real socket
 * and then reads back what was recorded.
 */
public class RequestLoggingTests
{
    /*
     * The entry is written after the response is closed, so that a disk
     * write never sits between the caller and its answer. That means a
     * test can arrive before the line does, and has to wait for it.
     */
    private static async Task<JsonElement> Newest(GatewayHarness gateway, string path)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            foreach (var line in gateway.Log.Tail(200))
            {
                var entry = JsonDocument.Parse(line).RootElement;

                if (entry.GetProperty("path").GetString() == path)
                {
                    return entry.Clone();
                }
            }

            await Task.Delay(50);
        }

        throw new Xunit.Sdk.XunitException($"no log entry for {path} after 2s");
    }

    private static async Task<int> CountAsync(GatewayHarness gateway, string path, int expected)
    {
        var seen = 0;

        for (var attempt = 0; attempt < 40; attempt++)
        {
            seen = gateway.Log.Tail(500)
                .Count(l => JsonDocument.Parse(l).RootElement.GetProperty("path").GetString() == path);

            if (seen >= expected)
            {
                return seen;
            }

            await Task.Delay(50);
        }

        return seen;
    }

    [Fact]
    public async Task A_served_request_is_recorded_with_its_status_and_duration()
    {
        using var gateway = new GatewayHarness();

        await GatewayHarness.Read(gateway.Get("/health", key: ""));

        var entry = await Newest(gateway, "/health");

        Assert.Equal("GET", entry.GetProperty("method").GetString());
        Assert.Equal(200, entry.GetProperty("status").GetInt32());
        Assert.True(entry.GetProperty("elapsedMs").GetInt64() >= 0);
    }

    /*
     * The reason the log exists: if the key leaks, the rejected attempts
     * are the trail that shows it.
     */
    [Fact]
    public async Task A_rejected_key_is_recorded_as_unauthenticated()
    {
        using var gateway = new GatewayHarness();

        await GatewayHarness.Read(gateway.Get("/databases", key: "not-the-key"));

        var entry = await Newest(gateway, "/databases");

        Assert.Equal(401, entry.GetProperty("status").GetInt32());
        Assert.False(entry.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task A_query_records_the_statement_and_the_connection()
    {
        using var gateway = new GatewayHarness();

        await GatewayHarness.Read(gateway.Post("/query", new
        {
            database = "Archive",
            sql = "SELECT ID FROM CUSTOMERS WHERE ID = @id",
            parameters = new Dictionary<string, object> { ["id"] = 42 },
        }));

        var entry = await Newest(gateway, "/query");

        Assert.Equal("Archive", entry.GetProperty("database").GetString());
        Assert.Contains("SELECT ID FROM CUSTOMERS", entry.GetProperty("sql").GetString());
    }

    /*
     * The values bound to a query are the customer's data. They must not
     * be recoverable from the log.
     */
    [Fact]
    public async Task Bound_parameter_values_are_not_written_to_the_log()
    {
        using var gateway = new GatewayHarness();

        const string secret = "national-id-99887766"

;
        await GatewayHarness.Read(gateway.Post("/query", new
        {
            database = "Archive",
            sql = "SELECT ID FROM CUSTOMERS WHERE NATIONAL_ID = @id",
            parameters = new Dictionary<string, object> { ["id"] = secret },
        }));

        await Newest(gateway, "/query");

        var everything = string.Join("\n", gateway.Log.Tail(50));

        Assert.Contains("NATIONAL_ID = @id", everything);
        Assert.DoesNotContain(secret, everything);
    }

    [Fact]
    public async Task The_forwarded_client_address_is_preferred_over_the_socket_peer()
    {
        using var gateway = new GatewayHarness();

        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("CF-Connecting-IP", "203.0.113.55");

        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var entry = await Newest(gateway, "/health");

        Assert.Equal("203.0.113.55", entry.GetProperty("clientIp").GetString());

        // The socket peer is kept too: it proves the request arrived over loopback.
        Assert.Contains("127.0.0.1", entry.GetProperty("localPeer").GetString());
    }

    [Fact]
    public async Task Every_request_lands_exactly_once_under_load()
    {
        using var gateway = new GatewayHarness();

        await Task.WhenAll(Enumerable.Range(0, 25).Select(_ =>
            GatewayHarness.Read(gateway.Get("/health", key: ""))));

        Assert.Equal(25, await CountAsync(gateway, "/health", 25));
    }
}
