# FbGateway HTTP API

FbGateway exposes the configured Firebird connections over a small
JSON API on loopback, so a Cloudflare Tunnel running on the same
machine has something to forward requests to.

The listener starts with the app. Its status, port and API key are
shown in the **Gateway API** panel of the main window.

## Endpoints

| Method | Path         | Auth | Purpose                                  |
| ------ | ------------ | ---- | ---------------------------------------- |
| GET    | `/health`    | no   | Liveness. Use it to test the tunnel.     |
| GET    | `/databases` | yes  | List the configured connections.         |
| POST   | `/query`     | yes  | Run a `SELECT` / `WITH` and get rows.    |
| POST   | `/execute`   | yes  | Run an `INSERT` / `UPDATE` / `DELETE`.   |

`/query` rejects anything that is not a `SELECT` or a `WITH` so a
read path cannot write by accident. Use `/execute` for writes.

## Authentication

Every endpoint except `/health` requires the API key generated on
first run:

```
X-API-Key: <key>
```

`Authorization: Bearer <key>` is accepted as well. Copy the key from
the **Copy API Key** button; **New Key** rotates it and takes effect
immediately, without restarting the gateway.

`/health` is deliberately open so the tunnel can be verified before
any key is involved. It returns no data from any database.

## Examples

List the connections:

```bash
curl -H "X-API-Key: $KEY" https://your-tunnel.example.com/databases
```

Run a query. `database` accepts either the connection name shown in
the app or its id:

```bash
curl -X POST https://your-tunnel.example.com/query \
  -H "X-API-Key: $KEY" \
  -H "Content-Type: application/json" \
  -d '{
        "database": "Sales",
        "sql": "SELECT ID, NAME FROM CUSTOMERS WHERE ID = @id",
        "parameters": { "id": 42 },
        "maxRows": 200
      }'
```

```json
{
  "columns": ["ID", "NAME"],
  "rows": [[42, "Acme Ltd"]],
  "rowCount": 1,
  "truncated": false,
  "elapsedMs": 12
}
```

`truncated` is `true` when the result hit the row cap and more rows
were left unread — narrow the query or page through it.

Always pass values through `parameters` rather than concatenating
them into `sql`; they are bound as Firebird parameters, so a value
cannot turn into SQL.

Write:

```bash
curl -X POST https://your-tunnel.example.com/execute \
  -H "X-API-Key: $KEY" \
  -H "Content-Type: application/json" \
  -d '{
        "database": "Sales",
        "sql": "UPDATE CUSTOMERS SET NAME = @name WHERE ID = @id",
        "parameters": { "id": 42, "name": "Acme Limited" }
      }'
```

```json
{ "rowsAffected": 1, "elapsedMs": 8 }
```

## Types

| Firebird              | JSON                                |
| --------------------- | ----------------------------------- |
| `INTEGER`, `BIGINT`   | number                              |
| `NUMERIC`, `DECIMAL`  | number, scale preserved             |
| `VARCHAR`, `CHAR`     | string                              |
| `DATE`, `TIMESTAMP`   | ISO-8601 string                     |
| `BLOB SUB_TYPE TEXT`  | string                              |
| `BLOB` (binary)       | base64 string                       |
| `NULL`                | `null`                              |

## Status codes

| Code | Meaning                                                      |
| ---- | ------------------------------------------------------------ |
| 400  | Bad request body, a write sent to `/query`, or invalid SQL.  |
| 401  | Missing or wrong API key.                                    |
| 404  | Unknown endpoint, or no connection matches `database`.       |
| 405  | Wrong HTTP method for the endpoint.                          |
| 409  | The connection exists but is **Offline** in the app.         |
| 500  | Unexpected server error.                                     |

Errors are `{ "error": "..." }`.

## Exposing it with Cloudflare Tunnel

The gateway binds to loopback only. `cloudflared` runs on the same
machine and reaches it over `127.0.0.1`, so nothing needs to be
opened on the LAN or the router.

Quick tunnel, for testing:

```bash
cloudflared tunnel --url http://127.0.0.1:8080
```

Named tunnel, in `config.yml`:

```yaml
tunnel: <tunnel-uuid>
credentials-file: C:\Users\<you>\.cloudflared\<tunnel-uuid>.json

ingress:
  - hostname: fbgateway.example.com
    service: http://127.0.0.1:8080
  - service: http_status:404
```

Then confirm the whole path end to end:

```bash
curl https://fbgateway.example.com/health
```

```json
{ "status": "ok", "service": "FbGateway", "connections": 2, "online": 1, ... }
```

Anyone who reaches the hostname can try the API, so treat the key as
a database credential. Cloudflare Access in front of the hostname is
worth adding if the data is sensitive.

## Troubleshooting

**502 from the tunnel, or Cloudflare error 1033** — nothing is
listening on the port `cloudflared` forwards to. Check the Gateway
API panel says **Running**, and that its port matches the tunnel's
`service:` URL. On the machine itself:

```
netstat -ano | findstr :8080
curl http://127.0.0.1:8080/health
```

An empty `netstat` means the gateway is stopped.

**"Port 8080 is already in use"** — something else holds it. Change
the port in the Gateway API panel and update the tunnel config to
match, or free the port:

```
netstat -ano | findstr :8080
tasklist /fi "pid eq <pid>"
```

**"Windows refused to reserve ..."** — HTTP.SYS wants a URL
reservation. Run FbGateway as administrator once, or grant it from
an elevated prompt:

```
netsh http add urlacl url=http://127.0.0.1:8080/ user="%USERNAME%"
```

**409 on every query** — the connection is Offline. Turn it Online in
the app; the gateway re-reads the connection list on every request,
so the change applies immediately.

**Firebird itself is unreachable** — `/health` still answers, because
it does not touch Firebird. `/query` returns the Firebird error, which
is the one to act on.
