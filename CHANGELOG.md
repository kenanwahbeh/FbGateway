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

### Notes

- Settings live in `C:\ProgramData\EasyFbSoft\easyfbsoft.db`. A file
  written by a build from before the rename, under
  `C:\ProgramData\FbGateway`, is carried over automatically on first
  start; the old file is left behind as a backup.

[Unreleased]: https://github.com/kenanwahbeh/FbGateway/commits/main
