using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FirebirdSql.Data.FirebirdClient;
using FbGateway.Configuration;

namespace FbGateway.Gateway;

internal static class FirebirdExecutor
{
    public static string BuildConnectionString(
        DatabaseConfig config)
    {
        var builder =
            new FbConnectionStringBuilder
            {
                DataSource = config.Server,
                Port = config.Port,
                Database = config.Database,
                UserID = config.Username,
                Password = config.Password,
                Charset = "UTF8",
                ConnectionTimeout = 10
            };

        return builder.ToString();
    }

    public static async Task<QueryResponse> QueryAsync(
        DatabaseConfig config,
        string sql,
        IReadOnlyDictionary<string, JsonElement>? parameters,
        int maxRows,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        var response = new QueryResponse();

        await using var connection =
            new FbConnection(
                BuildConnectionString(config));

        await connection.OpenAsync(cancellationToken);

        await using var command =
            connection.CreateCommand();

        command.CommandText = sql;
        command.CommandTimeout = commandTimeoutSeconds;

        AddParameters(command, parameters);

        await using var reader =
            await command.ExecuteReaderAsync(
                cancellationToken);

        for (var i = 0; i < reader.FieldCount; i++)
        {
            response.Columns.Add(reader.GetName(i));
        }

        while (await reader.ReadAsync(cancellationToken))
        {
            /*
             * Stop at the cap rather than materialising a whole
             * table into memory and pushing it through the
             * tunnel. Truncated tells the caller to paginate.
             */
            if (response.Rows.Count >= maxRows)
            {
                response.Truncated = true;
                break;
            }

            var row =
                new List<object?>(reader.FieldCount);

            for (var i = 0; i < reader.FieldCount; i++)
            {
                row.Add(
                    reader.IsDBNull(i)
                        ? null
                        : NormalizeValue(reader.GetValue(i)));
            }

            response.Rows.Add(row);
        }

        response.RowCount = response.Rows.Count;

        stopwatch.Stop();

        response.ElapsedMs =
            stopwatch.ElapsedMilliseconds;

        return response;
    }

    public static async Task<ExecuteResponse> ExecuteAsync(
        DatabaseConfig config,
        string sql,
        IReadOnlyDictionary<string, JsonElement>? parameters,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        await using var connection =
            new FbConnection(
                BuildConnectionString(config));

        await connection.OpenAsync(cancellationToken);

        await using var command =
            connection.CreateCommand();

        command.CommandText = sql;
        command.CommandTimeout = commandTimeoutSeconds;

        AddParameters(command, parameters);

        var rowsAffected =
            await command.ExecuteNonQueryAsync(
                cancellationToken);

        stopwatch.Stop();

        return new ExecuteResponse
        {
            RowsAffected = rowsAffected,

            ElapsedMs =
                stopwatch.ElapsedMilliseconds
        };
    }

    /*
     * Guards /query against accidental writes.
     *
     * This is a convenience split between the two endpoints,
     * not a security boundary: anything holding the API key can
     * still reach /execute.
     */
    public static bool IsReadOnlyStatement(string sql)
    {
        var statement = StripLeadingNoise(sql);

        return StartsWithKeyword(statement, "SELECT") ||
               StartsWithKeyword(statement, "WITH");
    }

    /*
     * Skips whitespace and leading SQL comments, both line and
     * block form, so a statement that opens with a comment is
     * still recognised by its first real keyword.
     */
    private static string StripLeadingNoise(string sql)
    {
        var index = 0;

        while (index < sql.Length)
        {
            if (char.IsWhiteSpace(sql[index]))
            {
                index++;
                continue;
            }

            if (sql[index] == '-' &&
                index + 1 < sql.Length &&
                sql[index + 1] == '-')
            {
                while (index < sql.Length &&
                       sql[index] != '\n')
                {
                    index++;
                }

                continue;
            }

            if (sql[index] == '/' &&
                index + 1 < sql.Length &&
                sql[index + 1] == '*')
            {
                var end =
                    sql.IndexOf(
                        "*/",
                        index + 2,
                        StringComparison.Ordinal);

                if (end < 0)
                {
                    return string.Empty;
                }

                index = end + 2;
                continue;
            }

            break;
        }

        return sql[index..];
    }

    private static bool StartsWithKeyword(
        string text,
        string keyword)
    {
        if (!text.StartsWith(
                keyword,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (text.Length == keyword.Length)
        {
            return true;
        }

        var next = text[keyword.Length];

        return !char.IsLetterOrDigit(next) &&
               next != '_' &&
               next != '$';
    }

    private static void AddParameters(
        FbCommand command,
        IReadOnlyDictionary<string, JsonElement>? parameters)
    {
        if (parameters == null)
        {
            return;
        }

        foreach (var parameter in parameters)
        {
            var name =
                parameter.Key.StartsWith('@')
                    ? parameter.Key
                    : "@" + parameter.Key;

            command.Parameters.AddWithValue(
                name,
                ToParameterValue(parameter.Value));
        }
    }

    private static object ToParameterValue(
        JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return DBNull.Value;

            case JsonValueKind.True:
                return true;

            case JsonValueKind.False:
                return false;

            case JsonValueKind.String:
                return element.GetString()
                    ?? (object)DBNull.Value;

            case JsonValueKind.Number:
                if (element.TryGetInt64(out var integer))
                {
                    return integer;
                }

                if (element.TryGetDecimal(out var value))
                {
                    return value;
                }

                return element.GetDouble();

            default:
                return element.GetRawText();
        }
    }

    /*
     * Keeps the JSON payload predictable.
     *
     * Blobs come back base64 encoded, and any provider specific
     * type the serializer would not handle is rendered as its
     * string form instead of failing the whole request.
     */
    private static object? NormalizeValue(object? value)
    {
        return value switch
        {
            null => null,

            DBNull => null,

            byte[] bytes =>
                Convert.ToBase64String(bytes),

            string or bool or decimal or double or float or
            byte or sbyte or short or ushort or int or uint or
            long or ulong or DateTime or DateTimeOffset or
            TimeSpan or Guid => value,

            _ => value.ToString()
        };
    }
}
