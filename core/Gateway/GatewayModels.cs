using System.Collections.Generic;
using System.Text.Json;

namespace FbGateway.Gateway;

public class QueryRequest
{
    /*
     * Either the connection Id or its Name.
     *
     * Names are accepted because the Id is a GUID that nobody
     * wants to paste into a request body by hand.
     */
    public string? Database { get; set; }

    public string? Sql { get; set; }

    /*
     * Bound as Firebird named parameters. A key of "id" and a
     * key of "@id" both bind to @id in the statement.
     */
    public Dictionary<string, JsonElement>? Parameters { get; set; }

    public int? MaxRows { get; set; }
}

public class QueryResponse
{
    public List<string> Columns { get; set; } = new();

    public List<List<object?>> Rows { get; set; } = new();

    public int RowCount { get; set; }

    /*
     * True when the result hit the row cap and more rows were
     * left unread on the server.
     */
    public bool Truncated { get; set; }

    public long ElapsedMs { get; set; }
}

public class ExecuteResponse
{
    public int RowsAffected { get; set; }

    public long ElapsedMs { get; set; }
}

/*
 * The public view of a connection.
 *
 * Server, database path and credentials are deliberately left
 * out: a caller only needs to know which connections exist and
 * which one to address.
 */
public class DatabaseSummary
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool Online { get; set; }
}

public class ErrorResponse
{
    public string Error { get; set; } = string.Empty;

    public ErrorResponse()
    {
    }

    public ErrorResponse(string error)
    {
        Error = error;
    }
}
