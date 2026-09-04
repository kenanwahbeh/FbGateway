using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace FbGateway.Gateway;

/*
 * What a request was and how it ended.
 *
 * Filled in as the request is handled, then written once. The SQL
 * statement is recorded but never the parameter values: the values are
 * the customer's data, and the whole point of binding them separately
 * is that they are not part of the statement.
 */
public sealed class RequestRecord
{
    public DateTimeOffset At { get; } = DateTimeOffset.UtcNow;

    public string Method { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public int Status { get; set; }

    public long ElapsedMs { get; set; }

    /*
     * The socket peer, which behind a tunnel is always cloudflared on
     * loopback, and the address Cloudflare says the request came from.
     * Both are kept: the first proves the request arrived the expected
     * way, the second says who sent it.
     */
    public string? LocalPeer { get; set; }

    public string? ClientIp { get; set; }

    public bool Authenticated { get; set; }

    public string? Database { get; set; }

    public string? Sql { get; set; }

    public int? Rows { get; set; }

    public string? Error { get; set; }
}

/*
 * An append-only record of every request the gateway answered.
 *
 * A tunnel puts this listener on the public internet, so the question
 * "what actually ran, and who asked for it" has to be answerable after
 * the fact -- most of all if the API key ever leaks.
 *
 * One JSON object per line, one file per day, older files pruned. It is
 * never allowed to fail a request: a log that breaks the service it
 * observes is worse than no log.
 */
public sealed class RequestLog
{
    private const int MaxSqlLength = 2000;

    private readonly object _sync = new();

    private readonly string _directory;

    private readonly int _retentionDays;

    private DateOnly _lastPruneDay;

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition =
                System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,

            // Arabic and other non-ASCII text stays readable in the file.
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };

    public RequestLog(string directory, int retentionDays = 30)
    {
        _directory = directory;
        _retentionDays = retentionDays < 1 ? 1 : retentionDays;
    }

    public string Directory => _directory;

    /*
     * The file the current day's entries go to. Exposed so the window
     * can point someone at it.
     */
    public string CurrentFile =>
        Path.Combine(
            _directory,
            $"gateway-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");

    public void Write(RequestRecord record)
    {
        try
        {
            var payload = new
            {
                at = record.At.ToString("O", CultureInfo.InvariantCulture),
                method = record.Method,
                path = record.Path,
                status = record.Status,
                elapsedMs = record.ElapsedMs,
                localPeer = record.LocalPeer,
                clientIp = record.ClientIp,
                authenticated = record.Authenticated,
                database = record.Database,
                sql = Truncate(record.Sql),
                rows = record.Rows,
                error = record.Error
            };

            var line = JsonSerializer.Serialize(payload, JsonOptions);

            lock (_sync)
            {
                System.IO.Directory.CreateDirectory(_directory);

                File.AppendAllText(CurrentFile, line + Environment.NewLine);

                PruneIfNewDay();
            }
        }
        catch (Exception ex)
        {
            /*
             * Deliberately swallowed. A full disk or a locked file must
             * not turn a working query into a failed one.
             */
            Console.Error.WriteLine($"request log write failed: {ex.Message}");
        }
    }

    /*
     * Reads back the most recent entries, newest first, for showing in
     * the app. Returns raw lines; the caller decides how to render them.
     */
    public IReadOnlyList<string> Tail(int count = 100)
    {
        try
        {
            if (!System.IO.Directory.Exists(_directory))
            {
                return Array.Empty<string>();
            }

            var files = new List<string>(
                System.IO.Directory.GetFiles(_directory, "gateway-*.jsonl"));

            files.Sort(StringComparer.Ordinal);
            files.Reverse();

            var lines = new List<string>();

            foreach (var file in files)
            {
                var fileLines = ReadAllLinesShared(file);

                for (var i = fileLines.Count - 1; i >= 0 && lines.Count < count; i--)
                {
                    if (!string.IsNullOrWhiteSpace(fileLines[i]))
                    {
                        lines.Add(fileLines[i]);
                    }
                }

                if (lines.Count >= count)
                {
                    break;
                }
            }

            return lines;
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /*
     * The writer holds the file open only for the append, but read it
     * share-friendly anyway so a tail never trips a live write.
     */
    private static List<string> ReadAllLinesShared(string path)
    {
        var lines = new List<string>();

        using var stream =
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    /*
     * Pruning is checked once per day rather than per request; the cost
     * of a directory listing does not belong on the hot path.
     */
    private void PruneIfNewDay()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        if (_lastPruneDay == today)
        {
            return;
        }

        _lastPruneDay = today;

        var cutoff = DateTime.UtcNow.Date.AddDays(-_retentionDays);

        foreach (var file in System.IO.Directory.GetFiles(_directory, "gateway-*.jsonl"))
        {
            var name = Path.GetFileNameWithoutExtension(file);

            if (name.Length < "gateway-yyyy-MM-dd".Length)
            {
                continue;
            }

            var datePart = name["gateway-".Length..];

            if (DateTime.TryParseExact(
                    datePart,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var day)
                && day < cutoff)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static string? Truncate(string? sql)
    {
        if (string.IsNullOrEmpty(sql))
        {
            return sql;
        }

        return sql.Length <= MaxSqlLength
            ? sql
            : sql[..MaxSqlLength] + $"... [{sql.Length - MaxSqlLength} more]";
    }
}
