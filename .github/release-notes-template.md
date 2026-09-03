### Which file do I download?

Four installers, one application — pick **one**.

| File | .NET | Use it when |
| ---- | ---- | ----------- |
| `EasyFbSoft-{{VERSION}}-x64-setup.exe` | included | **Start here.** Normal desktop install, nothing else to install. |
| `EasyFbSoft-{{VERSION}}-x64.msi` | included | Deploying with Group Policy, Intune or a script. |
| `EasyFbSoft-{{VERSION}}-x64-framework-setup.exe` | required | You already have the .NET 10 Desktop Runtime and want a much smaller download. |
| `EasyFbSoft-{{VERSION}}-x64-framework.msi` | required | Scripted deployment where the runtime is managed separately. |

The `-framework` builds need the
[.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0).
The `-framework` **.exe** checks for it and stops with a link if it is
missing; the **.msi** does not check, because deployment tools handle
prerequisites themselves.

All four install per-machine into `Program Files` and ask for
administrator rights once. Requires 64-bit Windows 8.1 or later.

All four are the same product, so a new version replaces the old one.
Switching between the bundled and `-framework` builds at the *same*
version means uninstalling first.

### After installing

Start Easy FB Soft, add a Firebird connection and turn it Online, then
point your tunnel at the address shown in the Gateway API panel:

```
cloudflared tunnel --url http://127.0.0.1:8080
```

Copy the API key from the same panel — every endpoint except `/health`
requires it in the `X-API-Key` header.

See [GATEWAY.md](https://github.com/kenanwahbeh/FbGateway/blob/{{TAG}}/GATEWAY.md)
for the endpoints and the tunnel configuration.

### Verifying the download

`SHA256SUMS.txt` lists the SHA-256 checksum of each file.
