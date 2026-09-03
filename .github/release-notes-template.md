### Install

Two installers, same application — pick one, not both.

| File | Use it when |
| ---- | ----------- |
| `FbGateway-{{VERSION}}-x64-setup.exe` | Normal desktop install. |
| `FbGateway-{{VERSION}}-x64.msi` | Deploying with Group Policy, Intune or a script. |

Both install per-machine into `Program Files` and ask for administrator
rights once. .NET is bundled, so nothing else has to be installed.

Requires 64-bit Windows 8.1 or later.

### After installing

Start FbGateway, add a Firebird connection and turn it Online, then point
your tunnel at the address shown in the Gateway API panel:

```
cloudflared tunnel --url http://127.0.0.1:8080
```

Copy the API key from the same panel — every endpoint except `/health`
requires it in the `X-API-Key` header.

See [GATEWAY.md](https://github.com/kenanwahbeh/FbGateway/blob/{{TAG}}/GATEWAY.md)
for the endpoints and the tunnel configuration.

### Verifying the download

`SHA256SUMS.txt` lists the SHA-256 checksum of each file.
