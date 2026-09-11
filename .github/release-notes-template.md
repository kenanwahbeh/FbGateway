### Which file do I download?

Four installers, one application — pick **one**.

| File | .NET | Use it when |
| ---- | ---- | ----------- |
| `ByteBridge-{{VERSION}}-x64-setup.exe` | included | **Start here.** Normal desktop install, nothing else to install. |
| `ByteBridge-{{VERSION}}-x64.msi` | included | Deploying with Group Policy, Intune or a script. |
| `ByteBridge-{{VERSION}}-x64-framework-setup.exe` | fetched | Much smaller download; Setup installs the .NET runtime if the machine lacks it. |
| `ByteBridge-{{VERSION}}-x64-framework.msi` | required | Scripted deployment where the runtime is managed separately. |

The `-framework` builds need the
[.NET 10 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/10.0).
The `-framework` **.exe** checks for it and, when it is missing, offers
to download it from Microsoft and install it for you. The **.msi** does
not check, because deployment tools handle prerequisites themselves.

Both **.exe** installers also offer to install
[`cloudflared`](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/downloads/),
and leave the offer out when the machine already has it. That installs
the connector only — it does not connect a tunnel, which still needs
your own token.

All four install per-machine into `Program Files` and ask for
administrator rights once. Requires 64-bit Windows 8.1 or later.

All four are the same product, so a new version replaces the old one.
Switching between the bundled and `-framework` builds at the *same*
version means uninstalling first.

### It runs as a service

The gateway is installed and started as the `ByteBridge` Windows
service. It starts with the machine and serves with nobody signed in,
so closing the window no longer takes the gateway down.

The window is now a control panel for that service, and asks for
administrator rights: the settings folder holds your Firebird passwords
and the API key, and is restricted to Administrators and the service
account.

On **Windows Server Core**, where there is no desktop, configure it from
a terminal instead: `ByteBridge.Service.exe --help`.

### After installing

Start ByteBridge, add a Firebird connection and turn it Online, then
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
