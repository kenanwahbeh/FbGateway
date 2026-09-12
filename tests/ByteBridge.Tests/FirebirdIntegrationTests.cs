using System.Net;
using System.Text.Json;
using Xunit;
using ByteBridge.Gateway;

namespace ByteBridge.Tests;

/*
 * End to end against a real Firebird server: HTTP in, rows out.
 *
 * Skipped unless BYTEBRIDGE_TEST_FIREBIRD names one, so a clone with no
 * Firebird still gets a green run. See FirebirdFactAttribute.
 *
 * The schema is created on first use and left in place; every test
 * works on its own rows so they do not interfere.
 */
[Trait("Category", "Firebird")]
public class FirebirdIntegrationTests
{
    private const string Table = "EFS_TEST_CUSTOMERS";

    private static readonly SemaphoreSlim SchemaGate = new(1, 1);
    private static bool _schemaReady;

    private static async Task<GatewayHarness> LiveGatewayAsync()
    {
        await EnsureSchemaAsync();

        var gateway = new GatewayHarness();

        gateway.Database.AddConnection(FirebirdTestServer.Connection!);

        return gateway;
    }

    private static async Task RunAsync(string sql)
    {
        await FirebirdExecutor.ExecuteAsync(
            FirebirdTestServer.Connection!, sql, null, 30, CancellationToken.None);
    }

    private static async Task EnsureSchemaAsync()
    {
        if (_schemaReady)
        {
            return;
        }

        await SchemaGate.WaitAsync();

        try
        {
            if (_schemaReady)
            {
                return;
            }

            try
            {
                await RunAsync($"DROP TABLE {Table}");
            }
            catch
            {
                // Not there yet on the first run.
            }

            await RunAsync($"""
                CREATE TABLE {Table} (
                  ID INTEGER NOT NULL PRIMARY KEY,
                  NAME VARCHAR(60),
                  BALANCE NUMERIC(12,2),
                  CREATED TIMESTAMP,
                  BIRTHDAY DATE,
                  ACTIVE SMALLINT,
                  NOTES BLOB SUB_TYPE TEXT
                )
                """);

            await RunAsync($"""
                INSERT INTO {Table} VALUES
                (1, 'كنان وهبة', 1500.75, '2024-01-15 10:30:00', '1990-05-20', 1, 'ملاحظة عربية')
                """);

            await RunAsync($"""
                INSERT INTO {Table} VALUES
                (2, 'Acme Ltd', -42.10, '2024-02-01 08:00:00', '1985-11-02', 0, NULL)
                """);

            await RunAsync($"INSERT INTO {Table} VALUES (3, NULL, NULL, NULL, NULL, NULL, NULL)");

            _schemaReady = true;
        }
        finally
        {
            SchemaGate.Release();
        }
    }

    private static JsonElement Rows(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("rows");

    // ---- Reading -----------------------------------------------------

    [FirebirdFact]
    public async Task A_select_returns_columns_and_rows()
    {
        using var gateway = await LiveGatewayAsync();

        var (status, body) = await GatewayHarness.Read(gateway.Post("/query", new
        {
            database = "Test",
            sql = $"SELECT ID, NAME FROM {Table} ORDER BY ID"
        }));

        Assert.Equal(HttpStatusCode.OK, status);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal(2, root.GetProperty("columns").GetArrayLength());
        Assert.Equal(3, root.GetProperty("rowCount").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.True(root.GetProperty("elapsedMs").GetInt64() >= 0);
    }

    [FirebirdFact]
    public async Task Firebird_types_map_onto_json()
    {
        using var gateway = await LiveGatewayAsync();

        var (_, body) = await GatewayHarness.Read(gateway.Post("/query", new
        {
            database = "Test",
            sql = $"SELECT ID, NAME, BALANCE, CREATED, BIRTHDAY, NOTES FROM {Table} WHERE ID = 1"
        }));

        var row = Rows(body)[0];

        Assert.Equal(1, row[0].GetInt32());
        Assert.Equal("كنان وهبة", row[1].GetString());
        Assert.Equal(1500.75m, row[2].GetDecimal());
        Assert.StartsWith("2024-01-15T10:30:00", row[3].GetString());
        Assert.StartsWith("1990-05-20", row[4].GetString());
        Assert.Equal("ملاحظة عربية", row[5].GetString());
    }

    [FirebirdFact]
    public async Task Nulls_come_back_as_json_null()
    {
        using var gateway = await LiveGatewayAsync();

        var (_, body) = await GatewayHarness.Read(gateway.Post("/query", new
        {
            database = "Test",
            sql = $"SELECT NAME, BALANCE, NOTES FROM {Table} WHERE ID = 3"
        }));

        var row = Rows(body)[0];

        Assert.Equal(JsonValueKind.Null, row[0].ValueKind);
        Assert.Equal(JsonValueKind.Null, row[1].ValueKind);
        Assert.Equal(JsonValueKind.Null, row[2].ValueKind);
    }

    [FirebirdFact]
    public async Task Non_ascii_text_is_not_escaped_on_the_wire()
    {
        using var gateway = await LiveGatewayAsync();

        var (_, body) = await GatewayHarness.Read(gateway.Post("/query", new
        {
            database = "Test",
            sql = $"SELECT NAME FROM {Table} WHERE ID = 1"
        }));

        Assert.Contains("كنان وهبة", body);
        Assert.DoesNotContain("\\u06", body);
    }

    [FirebirdFact]
    public async Task Html_significant_characters_stay_escaped()
    {
        using var gateway = await LiveGatewayAsync();

        await RunAsync($"DELETE FROM {Table} WHERE ID = 90");
        await RunAsync($"INSERT INTO {Table} (ID, NAME) VALUES (90, '<script>x</script>')");

        try
        {
            var (_, body) = await GatewayHarness.Read(gateway.Post("/query", new
            {
                database = "Test",
                sql = $"SELECT NAME FROM {Table} WHERE ID = 90"
            }));

            Assert.DoesNotContain("<script>", body);
            Assert.Equal("<script>x</script>", Rows(body)[0][0].GetString());
        }
        finally
        {
            await RunAsync($"DELETE FROM {Table} WHERE ID = 90");
        }
    }

    // ---- Parameters --------------------------------------------------

    [FirebirdTheory]
    [InlineData("id")]
    [InlineData("@id")]
    public async Task A_named_parameter_binds_with_or_without_the_at_sign(string key)
    {
        using var gateway = await LiveGatewayAsync();

        var (status, body) = await GatewayHarness.Read(gateway.Post("/query", new
        {
            database = "Test",
            sql = $"SELECT NAME FROM {Table} WHERE ID = @id",
            parameters = new Dictionary<string, object> { [key] = 2 }
        }));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Acme Ltd", Rows(body)[0][0].GetString());
    }

    [FirebirdFact]
    public async Task A_parameter_value_cannot_become_sql()
    {
        using var gateway = await LiveGatewayAsync();

        var (status, body) = await GatewayHarness.Read(gateway.Post("/query", new
        {
            database = "Test",
            sql = $"SELECT ID FROM {Table} WHERE NAME = @name",
            parameters = new Dictionary<string, object> { ["name"] = "x' OR 1=1 --" }
        }));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("\"rowCount\":0", body);
    }

    [FirebirdFact]
    public async Task A_null_parameter_binds_as_null()
    {
        using var gateway = await LiveGatewayAsync();

        var (status, _) = await GatewayHarness.Read(gateway.Post("/query", new
        {
            database = "Test",
            sql = $"SELECT ID FROM {Table} WHERE (@flag IS NULL OR ACTIVE = @flag) ORDER BY ID",
            parameters = new Dictionary<string, object?> { ["flag"] = null }
        }));

        Assert.Equal(HttpStatusCode.OK, status);
    }

    // ---- Limits and errors -------------------------------------------

    [FirebirdFact]
    public async Task The_row_cap_truncates_and_says_so()
    {
        using var gateway = await LiveGatewayAsync();

        var (_, body) = await GatewayHarness.Read(gateway.Post("/query", new
        {
            database = "Test",
            sql = $"SELECT ID FROM {Table} ORDER BY ID",
            maxRows = 2
        }));

        using var document = JsonDocument.Parse(body);

        Assert.Equal(2, document.RootElement.GetProperty("rowCount").GetInt32());
        Assert.True(document.RootElement.GetProperty("truncated").GetBoolean());
    }

    [FirebirdFact]
    public async Task Invalid_sql_returns_the_firebird_message()
    {
        using var gateway = await LiveGatewayAsync();

        var (status, body) = await GatewayHarness.Read(gateway.Post("/query", new
        {
            database = "Test",
            sql = $"SELECT NOPE FROM {Table}"
        }));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("NOPE", body);
    }

    // ---- Writing -----------------------------------------------------

    [FirebirdFact]
    public async Task Execute_inserts_updates_and_deletes()
    {
        using var gateway = await LiveGatewayAsync();

        await RunAsync($"DELETE FROM {Table} WHERE ID = 99");

        var (inserted, insertBody) = await GatewayHarness.Read(gateway.Post("/execute", new
        {
            database = "Test",
            sql = $"INSERT INTO {Table} (ID, NAME, BALANCE) VALUES (@id, @name, @balance)",
            parameters = new Dictionary<string, object>
            {
                ["id"] = 99, ["name"] = "Inserted", ["balance"] = 10.5
            }
        }));

        Assert.Equal(HttpStatusCode.OK, inserted);
        Assert.Contains("\"rowsAffected\":1", insertBody);

        var (_, readBack) = await GatewayHarness.Read(gateway.Post("/query", new
        {
            database = "Test",
            sql = $"SELECT NAME, BALANCE FROM {Table} WHERE ID = 99"
        }));

        Assert.Equal("Inserted", Rows(readBack)[0][0].GetString());
        Assert.Equal(10.5m, Rows(readBack)[0][1].GetDecimal());

        var (_, updateBody) = await GatewayHarness.Read(gateway.Post("/execute", new
        {
            database = "Test",
            sql = $"UPDATE {Table} SET NAME = 'Renamed' WHERE ID = 99"
        }));

        Assert.Contains("\"rowsAffected\":1", updateBody);

        var (_, deleteBody) = await GatewayHarness.Read(gateway.Post("/execute", new
        {
            database = "Test",
            sql = $"DELETE FROM {Table} WHERE ID = 99"
        }));

        Assert.Contains("\"rowsAffected\":1", deleteBody);

        var (_, gone) = await GatewayHarness.Read(gateway.Post("/query", new
        {
            database = "Test",
            sql = $"SELECT ID FROM {Table} WHERE ID = 99"
        }));

        Assert.Contains("\"rowCount\":0", gone);
    }

    [FirebirdFact]
    public async Task A_cte_is_accepted_by_the_read_only_guard()
    {
        using var gateway = await LiveGatewayAsync();

        var (status, _) = await GatewayHarness.Read(gateway.Post("/query", new
        {
            database = "Test",
            sql = $"WITH T AS (SELECT ID FROM {Table}) SELECT COUNT(*) FROM T"
        }));

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [FirebirdFact]
    public async Task Concurrent_queries_all_succeed()
    {
        using var gateway = await LiveGatewayAsync();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 12).Select(_ => GatewayHarness.Read(
                gateway.Post("/query", new
                {
                    database = "Test",
                    sql = $"SELECT COUNT(*) FROM {Table}"
                }))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.Status));
    }
}
