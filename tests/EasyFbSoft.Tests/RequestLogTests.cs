using System.Text.Json;
using Xunit;
using FbGateway.Gateway;

namespace FbGateway.Tests;

public class RequestLogTests
{
    private static (RequestLog Log, TempDataRoot Root) NewLog(int retentionDays = 30)
    {
        var root = new TempDataRoot();
        return (new RequestLog(Path.Combine(root.Path, "logs"), retentionDays), root);
    }

    private static RequestRecord Sample() => new()
    {
        Method = "POST",
        Path = "/query",
        Status = 200,
        ElapsedMs = 12,
        LocalPeer = "127.0.0.1",
        ClientIp = "203.0.113.7",
        Authenticated = true,
        Database = "Sales",
        Sql = "SELECT NAME FROM CUSTOMERS WHERE ID = @id",
        Rows = 1,
    };

    [Fact]
    public void An_entry_is_written_as_one_json_line()
    {
        var (log, root) = NewLog();
        using var _ = root;

        log.Write(Sample());

        var lines = File.ReadAllLines(log.CurrentFile);

        var line = Assert.Single(lines);

        using var document = JsonDocument.Parse(line);
        var entry = document.RootElement;

        Assert.Equal("POST", entry.GetProperty("method").GetString());
        Assert.Equal("/query", entry.GetProperty("path").GetString());
        Assert.Equal(200, entry.GetProperty("status").GetInt32());
        Assert.Equal("Sales", entry.GetProperty("database").GetString());
        Assert.Equal(1, entry.GetProperty("rows").GetInt32());
        Assert.True(entry.GetProperty("authenticated").GetBoolean());
    }

    /*
     * The point of binding parameters is that the values are not part of
     * the statement. The log must not put them back.
     */
    [Fact]
    public void Parameter_values_never_reach_the_log()
    {
        var (log, root) = NewLog();
        using var _ = root;

        var record = Sample();
        record.Sql = "SELECT * FROM CUSTOMERS WHERE NATIONAL_ID = @id AND SECRET = @s";
        log.Write(record);

        var written = File.ReadAllText(log.CurrentFile);

        // The statement is there; the placeholders are not filled in.
        Assert.Contains("NATIONAL_ID = @id", written);
        Assert.DoesNotContain("parameters", written, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Both_the_socket_peer_and_the_forwarded_client_are_kept()
    {
        var (log, root) = NewLog();
        using var _ = root;

        log.Write(Sample());

        using var document = JsonDocument.Parse(File.ReadAllLines(log.CurrentFile)[0]);

        Assert.Equal("127.0.0.1", document.RootElement.GetProperty("localPeer").GetString());
        Assert.Equal("203.0.113.7", document.RootElement.GetProperty("clientIp").GetString());
    }

    [Fact]
    public void A_rejected_request_is_recorded_with_its_reason()
    {
        var (log, root) = NewLog();
        using var _ = root;

        log.Write(new RequestRecord
        {
            Method = "POST",
            Path = "/query",
            Status = 401,
            Authenticated = false,
            ClientIp = "198.51.100.9",
        });

        using var document = JsonDocument.Parse(File.ReadAllLines(log.CurrentFile)[0]);

        Assert.Equal(401, document.RootElement.GetProperty("status").GetInt32());
        Assert.False(document.RootElement.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public void Non_ascii_text_stays_readable_in_the_file()
    {
        var (log, root) = NewLog();
        using var _ = root;

        var record = Sample();
        record.Sql = "SELECT * FROM العملاء";
        log.Write(record);

        Assert.Contains("العملاء", File.ReadAllText(log.CurrentFile));
    }

    [Fact]
    public void A_very_long_statement_is_truncated_rather_than_filling_the_disk()
    {
        var (log, root) = NewLog();
        using var _ = root;

        var record = Sample();
        record.Sql = "SELECT " + new string('x', 50_000);
        log.Write(record);

        var line = File.ReadAllLines(log.CurrentFile)[0];

        using var document = JsonDocument.Parse(line);
        var sql = document.RootElement.GetProperty("sql").GetString()!;

        Assert.True(sql.Length < 2500, $"sql was {sql.Length} chars");
        Assert.Contains("more]", sql);
    }

    [Fact]
    public void Concurrent_writes_all_land_and_none_are_torn()
    {
        var (log, root) = NewLog();
        using var _ = root;

        Parallel.For(0, 200, i =>
        {
            var record = Sample();
            record.Rows = i;
            log.Write(record);
        });

        var lines = File.ReadAllLines(log.CurrentFile);

        Assert.Equal(200, lines.Length);

        // Every line must still be valid JSON: a torn write would not parse.
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            Assert.Equal("/query", document.RootElement.GetProperty("path").GetString());
        }
    }

    [Fact]
    public void Tail_returns_the_newest_entries_first()
    {
        var (log, root) = NewLog();
        using var _ = root;

        for (var i = 0; i < 5; i++)
        {
            var record = Sample();
            record.Rows = i;
            log.Write(record);
        }

        var tail = log.Tail(3);

        Assert.Equal(3, tail.Count);

        using var newest = JsonDocument.Parse(tail[0]);
        Assert.Equal(4, newest.RootElement.GetProperty("rows").GetInt32());
    }

    [Fact]
    public void Tail_of_a_directory_that_does_not_exist_is_empty()
    {
        using var root = new TempDataRoot();

        var log = new RequestLog(Path.Combine(root.Path, "never-created"));

        Assert.Empty(log.Tail());
    }

    /*
     * A log that breaks the service it observes is worse than no log.
     */
    [Fact]
    public void A_write_that_cannot_land_does_not_throw()
    {
        // A path that cannot be created as a directory on either platform.
        var log = new RequestLog(Path.Combine(Path.GetTempPath(), "\0invalid"));

        var exception = Record.Exception(() => log.Write(Sample()));

        Assert.Null(exception);
    }

    [Fact]
    public void Files_older_than_the_retention_window_are_pruned()
    {
        var (log, root) = NewLog(retentionDays: 7);
        using var _ = root;

        log.Write(Sample());

        var directory = log.Directory;
        var stale = Path.Combine(directory, $"gateway-{DateTime.UtcNow.AddDays(-30):yyyy-MM-dd}.jsonl");
        var recent = Path.Combine(directory, $"gateway-{DateTime.UtcNow.AddDays(-2):yyyy-MM-dd}.jsonl");

        File.WriteAllText(stale, "{}\n");
        File.WriteAllText(recent, "{}\n");

        // Pruning runs at most once a day; force the next write to do it.
        typeof(RequestLog)
            .GetField("_lastPruneDay", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(log, default(DateOnly));

        log.Write(Sample());

        Assert.False(File.Exists(stale), "a file past the window survived");
        Assert.True(File.Exists(recent), "a file inside the window was deleted");
    }
}
