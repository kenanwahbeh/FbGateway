using System.Net;
using Xunit;

namespace FbGateway.Tests;

/*
 * The HTTP surface, over a real listener on a real socket.
 *
 * One gateway is shared by the whole class, so these tests must not
 * change the connection list or the API key; the cases that do live in
 * GatewayLifecycleTests.
 */
public class GatewayServerTests : IClassFixture<GatewayHarness>
{
    private readonly GatewayHarness _gateway;

    public GatewayServerTests(GatewayHarness gateway)
    {
        _gateway = gateway;
    }

    // ---- /health -----------------------------------------------------

    [Fact]
    public async Task Health_needs_no_api_key()
    {
        var (status, body) = await GatewayHarness.Read(_gateway.Get("/health", key: ""));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("\"status\":\"ok\"", body);
    }

    [Fact]
    public async Task Health_names_the_service_and_counts_connections()
    {
        var (_, body) = await GatewayHarness.Read(_gateway.Get("/health", key: ""));

        Assert.Contains("\"service\":\"EasyFbSoft\"", body);
        Assert.Contains("\"connections\":2", body);
        Assert.Contains("\"online\":1", body);
    }

    [Fact]
    public async Task Trailing_slash_resolves_to_the_same_endpoint()
    {
        var (status, _) = await GatewayHarness.Read(_gateway.Get("/health/", key: ""));

        Assert.Equal(HttpStatusCode.OK, status);
    }

    // ---- Authentication ----------------------------------------------

    [Fact]
    public async Task Databases_without_a_key_is_unauthorized()
    {
        var (status, _) = await GatewayHarness.Read(_gateway.Get("/databases", key: ""));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task Databases_with_the_wrong_key_is_unauthorized()
    {
        var (status, _) = await GatewayHarness.Read(
            _gateway.Get("/databases", key: "not-the-key"));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task A_key_of_the_right_length_but_wrong_value_is_still_rejected()
    {
        var wrong = new string('a', _gateway.ApiKey.Length);

        var (status, _) = await GatewayHarness.Read(_gateway.Get("/databases", key: wrong));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task Bearer_authorization_is_accepted()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/databases");

        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _gateway.ApiKey);

        using var response = await _gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- /databases --------------------------------------------------

    [Fact]
    public async Task Databases_lists_every_connection_by_name()
    {
        var (status, body) = await GatewayHarness.Read(_gateway.Get("/databases"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("Sales", body);
        Assert.Contains("Archive", body);
    }

    [Fact]
    public async Task Databases_never_exposes_credentials_or_paths()
    {
        var (_, body) = await GatewayHarness.Read(_gateway.Get("/databases"));

        Assert.DoesNotContain(_gateway.Online.Password, body);
        Assert.DoesNotContain("SYSDBA", body);
        Assert.DoesNotContain(".fdb", body);
    }

    [Fact]
    public async Task Databases_reports_the_online_flag_per_connection()
    {
        var (_, body) = await GatewayHarness.Read(_gateway.Get("/databases"));

        Assert.Contains("\"name\":\"Sales\",\"online\":true", body);
        Assert.Contains("\"name\":\"Archive\",\"online\":false", body);
    }

    // ---- Routing -----------------------------------------------------

    [Theory]
    [InlineData("/query")]
    [InlineData("/execute")]
    public async Task Post_only_endpoints_reject_a_get(string path)
    {
        using var response = await _gateway.Get(path);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);

        // Allow is a content header, not a response header, in .NET.
        Assert.Contains("POST", response.Content.Headers.Allow);
    }

    [Fact]
    public async Task An_unknown_path_is_not_found()
    {
        var (status, body) = await GatewayHarness.Read(_gateway.Get("/nope"));

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Contains("/health", body);
    }

    [Fact]
    public async Task Preflight_is_answered_with_cors_headers()
    {
        using var response = await _gateway.Send(HttpMethod.Options, "/query", key: "");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").First());

        Assert.Contains(
            "X-API-Key",
            response.Headers.GetValues("Access-Control-Allow-Headers").First());
    }

    // ---- Request validation ------------------------------------------

    [Fact]
    public async Task A_missing_body_is_a_bad_request()
    {
        var (status, _) = await GatewayHarness.Read(_gateway.Post("/query", null));

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Malformed_json_says_so()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/query")
        {
            Content = new StringContent(
                "{ not json",
                System.Text.Encoding.UTF8,
                "application/json")
        };

        request.Headers.Add("X-API-Key", _gateway.ApiKey);

        using var response = await _gateway.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Invalid JSON", body);
    }

    [Fact]
    public async Task A_request_without_sql_is_a_bad_request()
    {
        var (status, body) = await GatewayHarness.Read(
            _gateway.Post("/query", new { database = "Sales" }));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("sql", body);
    }

    [Fact]
    public async Task A_request_without_a_database_is_a_bad_request()
    {
        var (status, body) = await GatewayHarness.Read(
            _gateway.Post("/query", new { sql = "SELECT 1 FROM RDB$DATABASE" }));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("database", body);
    }

    // ---- The read-only guard on /query -------------------------------

    [Theory]
    [InlineData("DELETE FROM CUSTOMERS")]
    [InlineData("UPDATE CUSTOMERS SET NAME = 'x'")]
    [InlineData("INSERT INTO CUSTOMERS (ID) VALUES (1)")]
    [InlineData("DROP TABLE CUSTOMERS")]
    [InlineData("EXECUTE PROCEDURE WIPE")]
    public async Task Query_refuses_a_write_and_points_at_execute(string sql)
    {
        var (status, body) = await GatewayHarness.Read(
            _gateway.Post("/query", new { database = "Sales", sql }));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("/execute", body);
    }

    [Theory]
    [InlineData("-- a leading comment\nSELECT 1 FROM RDB$DATABASE")]
    [InlineData("/* a block comment */ SELECT 1 FROM RDB$DATABASE")]
    [InlineData("\n\t  WITH T AS (SELECT 1 X FROM RDB$DATABASE) SELECT X FROM T")]
    public async Task Query_looks_past_comments_and_whitespace(string sql)
    {
        /*
         * A connection name that does not exist, so the guard is the
         * only thing under test: 404 means the statement was accepted
         * and the request went on to resolving the connection.
         */
        var (status, _) = await GatewayHarness.Read(
            _gateway.Post("/query", new { database = "Nope", sql }));

        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    // ---- Resolving the connection ------------------------------------

    [Fact]
    public async Task An_unknown_connection_is_not_found()
    {
        var (status, body) = await GatewayHarness.Read(
            _gateway.Post("/query", new { database = "Ghost", sql = "SELECT 1 FROM RDB$DATABASE" }));

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Contains("Ghost", body);
    }

    [Fact]
    public async Task An_offline_connection_is_a_conflict_that_names_it()
    {
        var (status, body) = await GatewayHarness.Read(
            _gateway.Post("/query", new { database = "Archive", sql = "SELECT 1 FROM RDB$DATABASE" }));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("Archive", body);
        Assert.Contains("offline", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_connection_resolves_by_id_as_well_as_by_name()
    {
        var (status, _) = await GatewayHarness.Read(
            _gateway.Post("/query", new
            {
                database = _gateway.Offline.Id,
                sql = "SELECT 1 FROM RDB$DATABASE"
            }));

        // Resolved, then rejected for being offline rather than unknown.
        Assert.Equal(HttpStatusCode.Conflict, status);
    }

    [Fact]
    public async Task Connection_names_are_matched_case_insensitively()
    {
        var (status, _) = await GatewayHarness.Read(
            _gateway.Post("/query", new { database = "aRcHiVe", sql = "SELECT 1 FROM RDB$DATABASE" }));

        Assert.Equal(HttpStatusCode.Conflict, status);
    }

    // ---- Body size cap -----------------------------------------------

    [Fact]
    public async Task An_oversized_body_is_refused_with_a_readable_status()
    {
        var (status, body) = await GatewayHarness.Read(
            _gateway.Post("/query", new { sql = new string('x', 2 * 1024 * 1024) }));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Contains("limit", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_oversized_body_without_a_key_is_refused_and_never_buffered()
    {
        /*
         * The gateway answers on the key without reading megabytes from
         * an anonymous caller, then drops the connection rather than
         * leaving the remainder in it. Whether the client manages to
         * read the 401 before that or sees the drop is its own race, so
         * both count as refused -- what matters is that the request is
         * not served and the listener carries on.
         */
        try
        {
            var (status, _) = await GatewayHarness.Read(
                _gateway.Post("/query", new { sql = new string('x', 2 * 1024 * 1024) }, key: ""));

            Assert.Equal(HttpStatusCode.Unauthorized, status);
        }
        catch (HttpRequestException)
        {
            // Connection dropped mid-upload: also a refusal.
        }

        // A fresh connection still works.
        using var after = new HttpClient { BaseAddress = _gateway.Client.BaseAddress };

        using var health = await after.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    /*
     * Regression: the gateway used to answer 401 without reading the
     * body, leaving the unsent remainder in the keep-alive connection.
     * The next request on that connection was parsed from the middle of
     * the previous one and came back 400.
     */
    [Fact]
    public async Task An_early_reply_does_not_poison_the_next_request()
    {
        // A statement of a realistic size, sent with no key.
        var (rejected, _) = await GatewayHarness.Read(
            _gateway.Post("/query", new { sql = new string('x', 8 * 1024) }, key: ""));

        Assert.Equal(HttpStatusCode.Unauthorized, rejected);

        // Same client, so the connection is reused if it survived.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var (status, body) = await GatewayHarness.Read(_gateway.Get("/health", key: ""));

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains("\"status\":\"ok\"", body);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "/nope")]
    [InlineData(HttpStatusCode.MethodNotAllowed, "/query")]
    public async Task Other_early_replies_also_leave_the_connection_usable(
        HttpStatusCode expected,
        string path)
    {
        // A GET carrying a body: refused before the body is parsed.
        var request = new HttpRequestMessage(HttpMethod.Get, path)
        {
            Content = new StringContent(
                new string('x', 8 * 1024),
                System.Text.Encoding.UTF8,
                "application/json")
        };

        request.Headers.Add("X-API-Key", _gateway.ApiKey);

        using var response = await _gateway.Client.SendAsync(request);

        Assert.Equal(expected, response.StatusCode);

        var (status, _) = await GatewayHarness.Read(_gateway.Get("/health", key: ""));

        Assert.Equal(HttpStatusCode.OK, status);
    }

    /*
     * The two GETs that succeed used to answer without reading a body
     * they were sent: the one path left that did, after the refusals
     * above were fixed.
     */
    [Theory]
    [InlineData("/health")]
    [InlineData("/databases")]
    public async Task A_get_that_carries_a_body_leaves_the_connection_usable(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path)
        {
            Content = new StringContent(
                new string('x', 8 * 1024),
                System.Text.Encoding.UTF8,
                "application/json")
        };

        request.Headers.Add("X-API-Key", _gateway.ApiKey);

        using var response = await _gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var (status, _) = await GatewayHarness.Read(_gateway.Get("/health", key: ""));

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task A_body_just_under_the_cap_is_accepted()
    {
        var (status, _) = await GatewayHarness.Read(
            _gateway.Post("/query", new
            {
                database = "Ghost",
                sql = "SELECT '" + new string('y', 900 * 1024) + "' FROM RDB$DATABASE"
            }));

        // Parsed and routed; only the connection lookup failed.
        Assert.Equal(HttpStatusCode.NotFound, status);
    }
}
