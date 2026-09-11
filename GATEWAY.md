# Easy FB Soft HTTP API

Easy FB Soft exposes the configured Firebird connections over a small
JSON API on loopback, so a Cloudflare Tunnel running on the same
machine has something to forward requests to.

The listener starts with the machine. Its status, port and API key are
shown in the **Gateway API** panel of the control panel window.
The gateway itself runs as the `EasyFbSoft` Windows service, so it is
up whether or not that window is open.

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
the **Copy API Key** button. **New Key** rotates it without restarting
the gateway: the running gateway picks the new key up within a few
seconds, and refuses the old one from then on.

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

| Code | Meaning                                                           |
| ---- | ----------------------------------------------------------------- |
| 400  | Bad request body, a write sent to `/query`, or invalid SQL.       |
| 400  | `database` names more than one connection; send its `id` instead. |
| 401  | Missing or wrong API key.                                         |
| 404  | Unknown endpoint, or no connection matches `database`.            |
| 405  | Wrong HTTP method for the endpoint.                               |
| 409  | The connection exists but is **Offline** in the app.              |
| 413  | Request body over the 1 MB limit.                                 |
| 500  | Unexpected server error.                                          |

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
{ "status": "ok", "service": "EasyFbSoft", "connections": 2, "online": 1, ... }
```

Anyone who reaches the hostname can try the API, so treat the key as
a database credential. Cloudflare Access in front of the hostname is
worth adding if the data is sensitive.

## The request log

Every request the gateway answers is appended to
`C:\ProgramData\EasyFbSoft\logs\gateway-<date>.jsonl`, one JSON object
per line:

```json
{"at":"2026-09-03T21:40:11.7Z","method":"POST","path":"/query","status":200,
 "elapsedMs":12,"localPeer":"127.0.0.1","clientIp":"203.0.113.7",
 "authenticated":true,"database":"Sales",
 "sql":"SELECT NAME FROM CUSTOMERS WHERE ID = @id","rows":1}
```

A tunnel puts this listener on the public internet, so "what ran, and
who asked for it" has to be answerable afterwards -- above all if the
API key leaks. Rejected requests are recorded too, with
`"authenticated": false`; a run of those from one address is what an
attempt on the key looks like.

**Bound parameter values are never written.** The statement is recorded,
the values are not: they are the customer's data, and keeping them out
of the statement is the whole point of binding them. The statement is
truncated past 2000 characters.

`clientIp` comes from Cloudflare's `CF-Connecting-IP` header, since
behind a tunnel the socket peer is always `cloudflared` on loopback.
`localPeer` is kept as well: it is the evidence that a request really
did arrive the expected way.

Files are kept for 30 days and older ones are pruned. Writing happens
after the response is sent, so a slow disk never delays an answer --
which does mean the last entries can be lost if the process is killed
outright.

## Locking the tunnel to just you

The gateway binds to loopback and `cloudflared` reaches it locally, so
nothing is exposed on the LAN. But the tunnel's hostname is on the
public internet: anyone who discovers it reaches the API, and the key is
then the only thing in the way.

[Cloudflare Access](https://developers.cloudflare.com/cloudflare-one/policies/access/)
closes that gap by authenticating at Cloudflare's edge, before a request
ever reaches the machine.

For a program rather than a person, use a **service token**:

1. In Zero Trust, go to **Access → Service auth** and create a service
   token. Keep the Client ID and Client Secret.
2. Go to **Access → Applications**, add a **Self-hosted** application
   for the gateway's hostname.
3. Add a policy with action **Service Auth** and the rule
   *Service Token* → the token you just made.
4. Add a second policy with action **Bypass** for the path `/health`
   only, if you want liveness checks to stay reachable without the
   token.

Callers then send two extra headers:

```bash
curl https://<hostname>/query \
  -H "CF-Access-Client-Id: <client-id>" \
  -H "CF-Access-Client-Secret: <client-secret>" \
  -H "X-API-Key: <key>" \
  -H "Content-Type: application/json" \
  -d '{ "database": "Sales", "sql": "SELECT 1 FROM RDB$DATABASE" }'
```

The API key stays in place. Access decides who may reach the gateway at
all; the key decides what they may do once they have. Losing one still
leaves the other.

## Troubleshooting

**502 from the tunnel, or Cloudflare error 1033** — nothing is
listening on the port `cloudflared` forwards to. Check the Gateway
API panel says **Answering**, and that its port matches the tunnel's
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
reservation. Run Easy FB Soft as administrator once, or grant it from
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
