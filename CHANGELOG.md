# Changelog

Everything worth knowing about each release of Easy FB Soft.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and the version numbers follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html)
as described under [Versioning](README.md#versioning).

Work that is finished but not yet released sits under **Unreleased**.
`scripts/new-release.ps1` promotes it to a version heading, and the
release workflow copies that section into the GitHub Release, so this
file is the single source of truth for what shipped.

## [Unreleased]

### Added

- **The gateway runs as a Windows service.** It starts with the machine
  and serves with nobody signed in, which is what makes an unattended
  Windows Server a supported target. Closing the control panel no
  longer stops it: the window used to *be* the server, so tidying the
  desktop took the gateway down and a tunnel started returning `502`
  with nothing obviously wrong.

  The window is otherwise the one that was there. Databases, the API
  key, the port and the on/off switch are all in the same place; the
  switch now records what the gateway should be doing and the service
  acts on it within a few seconds.

  It also reports two things separately that are easy to confuse:
  whether Windows is running the service, and whether the gateway is
  actually answering, checked by calling `/health` over loopback. A
  running service whose gateway could not bind is exactly the state
  behind a `502`, so it is named rather than shown as healthy.

- **Configuration from a terminal**, for Windows Server Core, which has
  no desktop and so cannot run the control panel at all. The service
  executable doubles as an admin tool: `status`, `on`, `off`, `port`,
  `key show`, `key new`, and `db list/add/enable/disable/remove`. On a
  machine with a desktop you never need it.

- **The settings folder is locked to Administrators and Local System.**
  It holds the Firebird passwords in the clear beside the API key, and
  that key is all that stands between the public internet and those
  databases; inherited from `ProgramData` it would have been readable
  by every account on the machine. The control panel therefore asks for
  administrator rights, which starting and stopping the service needs
  anyway.

- The service reserves its own HTTP prefix when Windows refuses one, so
  changing the port does not leave the gateway unable to bind, and
  restarts itself on failure rather than staying down.

- **The `.exe` installers now fetch what they need.** The `-framework`
  build no longer stops with a link when the .NET 10 Desktop Runtime is
  missing: it asks first, then downloads it from Microsoft and installs
  it silently, and refuses to continue only if that genuinely fails.
  Both `.exe` builds also offer to install `cloudflared`, ticked by
  default and hidden entirely when the machine already has it -- checked
  in both `Program Files` locations and on `PATH`, so winget, scoop and
  a manual copy all count.

  The two are treated differently on purpose. Without .NET the
  `-framework` build cannot start, so a failed download aborts rather
  than leaving a shortcut to an app that dies on launch. `cloudflared`
  is only needed to reach the app from outside, so a failure there warns
  and carries on.

  Installing `cloudflared` gets you the connector, not a working tunnel:
  connecting one still needs your own token, which is the point.

  The `.msi` installers do neither, unchanged and deliberately: an MSI
  cannot download, and deployment tools manage prerequisites themselves.

  The release build now HEADs both URLs before packaging, so a vendor
  URL that moves fails the release instead of failing on a customer's
  machine.

- **A request log.** Every request the gateway answers is appended to
  `C:\ProgramData\EasyFbSoft\logs\gateway-<date>.jsonl`, one JSON
  object per line: time, method, path, status, duration, the calling
  address, whether the key was accepted, and for a query the statement,
  the connection and the row count. Rejected requests are recorded too,
  which is what an attempt on the API key looks like from the outside.
  Bound parameter values are never written -- they are the customer's
  data, and keeping them out of the statement is the point of binding
  them. Files are kept 30 days. Writing happens after the response is
  sent, so a slow disk never delays an answer.
- Documentation for putting **Cloudflare Access** in front of the
  tunnel hostname, so callers are authenticated at Cloudflare's edge
  before a request reaches the machine. See
  [GATEWAY.md](GATEWAY.md#locking-the-tunnel-to-just-you).

### Fixed

- **The `.msi` installers carried no application.** `Package.wxs`
  harvested the published folder with a path relative to the directory
  `wix` was launched from, but WiX resolves `Files/@Include` relative to
  the `.wxs` file itself, so the glob pointed at `installer\publish`,
  which does not exist. WiX only *warns* when a harvest matches nothing
  and still writes a valid installer, so the build passed and 1.0.0
  shipped two 48 KB `.msi` files containing a Start Menu shortcut and
  nothing else -- pointing at an executable that was never copied. Both
  `.exe` installers were unaffected and install correctly. The build now
  fails outright on an empty harvest, and additionally checks every
  installer against the size of the folder it is supposed to package.
- The `.wixpdb` files are no longer attached to releases. They are
  build-time symbol files for WiX itself and were published by accident.

## [1.0.0] - 2026-09-03

### Added

- **HTTP gateway** over the configured Firebird connections, bound to
  `127.0.0.1` only: `GET /health`, `GET /databases`, `POST /query` and
  `POST /execute`, all JSON. `/query` accepts `SELECT` and `WITH` only,
  so a read path cannot write by accident.
- **API key authentication** on every endpoint except `/health`. The
  key is 32 random bytes generated on first run, accepted in
  `X-API-Key` or as `Authorization: Bearer`, and compared in constant
  time. Rotating it takes effect immediately, without restarting the
  gateway.
- **Parameter binding.** Values passed in `parameters` are bound as
  Firebird parameters, so a value cannot become SQL. Blobs come back
  base64, text as text, and non-ASCII is emitted raw rather than
  `\uXXXX` escaped.
- **Gateway API panel** in the main window: whether the listener is
  running and on which URL, the port, start and stop, and copy or
  rotate the key. Windows URL-reservation and port-in-use failures are
  reported with the command that fixes them.
- **Limits** suited to something a tunnel exposes publicly: a row cap
  per query, a command timeout, and a 1 MB request body cap.
- **Installers**, all per-machine into `Program Files`: an `.msi` and
  an `.exe` that bundle .NET, and `-framework` builds of each that
  expect the .NET 10 Desktop Runtime. The `-framework` `.exe` refuses
  to install when the runtime is missing and offers the download link.
- **Release workflow** that builds all four installers on Windows,
  writes `SHA256SUMS.txt`, and attaches them to a GitHub Release when a
  `v*` tag is pushed.
- **Documentation**: [README.md](README.md) for what it does and how to
  get going, [GATEWAY.md](GATEWAY.md) for the API and the tunnel
  configuration.

### Fixed

- A request refused before its body was read -- a wrong API key, an
  unknown path, the wrong HTTP method -- left the unread bytes in the
  connection, so the *next* request on that keep-alive connection was
  parsed from the middle of the previous one and came back as a
  spurious `400`. Since `cloudflared` holds keep-alive connections to
  the origin, this surfaced as an unrelated request failing for no
  visible reason. Small bodies are now drained before the reply and the
  connection is dropped rather than reused when there is too much left.

### Notes

- Settings live in `C:\ProgramData\EasyFbSoft\easyfbsoft.db`. A file
  written by a build from before the rename, under
  `C:\ProgramData\FbGateway`, is carried over automatically on first
  start; the old file is left behind as a backup.

[Unreleased]: https://github.com/kenanwahbeh/FbGateway/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/kenanwahbeh/FbGateway/releases/tag/v1.0.0
