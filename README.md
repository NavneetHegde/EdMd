# EdMd — a native Windows Markdown editor

[![CI](https://github.com/NavneetHegde/EdMd/actions/workflows/ci.yml/badge.svg)](https://github.com/NavneetHegde/EdMd/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

EdMd is a small, fast **WYSIWYG Markdown editor** for Windows. It's a WPF window hosting a
single WebView2 that renders the whole UI (toolbar, editor, footer) as HTML/CSS/JS, with the
[Toast UI Editor](https://ui.toast.com/tui-editor) doing the editing. The C# side only owns
the window and the disk I/O the browser sandbox can't do. Built for authoring prompts,
skills, `CLAUDE.md` files, PRs, and specs — with a live token/word/char counter.

> _Screenshot placeholder — add `docs/screenshot.png` and reference it here._

## Install

Grab the latest build from the [**Releases**](../../releases) page. Two options:

### MSIX installer (recommended)

Registers `.md` / `.markdown` file associations, adds a Start-menu entry, and supports
in-place upgrades.

```powershell
Add-AppxPackage .\EdMd-<version>.msix
```

> **Interim trust step (self-signed builds only).** Until public signing via SignPath is
> live, releases are signed with a self-signed certificate, so Windows needs to trust it
> once. Download `edmd-codesign.cer` from the same release and run, in an **elevated**
> PowerShell:
>
> ```powershell
> Import-Certificate -FilePath .\edmd-codesign.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
> Import-Certificate -FilePath .\edmd-codesign.cer -CertStoreLocation Cert:\LocalMachine\Root
> ```
>
> This step **goes away** once builds are signed by SignPath (a publicly-trusted cert).

### Portable executable (no install)

Download `EdMd-<version>-win-x64.exe` and run it. It's self-contained (no .NET needed).
No file associations, and SmartScreen may show "Windows protected your PC" → **More info →
Run anyway** until the download builds reputation.

### Requirement: WebView2 Runtime

EdMd needs the **Microsoft Edge WebView2 Evergreen Runtime**, which is already present on
most Windows 10/11 machines. If it's missing, EdMd shows a prompt with the
[download link](https://developer.microsoft.com/microsoft-edge/webview2/).

## Build from source

**Prerequisites:** the [.NET 10 SDK](https://dotnet.microsoft.com/download), the WebView2
Runtime, and (only for the MSIX) the **Windows 10/11 SDK** (`MakeAppx.exe` + `SignTool.exe`).

```powershell
cd src/EdMd
dotnet restore
dotnet run                 # build + launch
```

Run the tests (xUnit — covers the security-sensitive C# logic):

```powershell
dotnet test                # from repo root or src/EdMd.Tests
```

Portable single-file exe:

```powershell
dotnet publish src/EdMd/EdMd.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

MSIX installer — see [`src/EdMd.Installer/README.md`](src/EdMd.Installer/README.md).

### Web / standalone mode

The same UI also runs as a plain web app in Chrome/Edge (File System Access API for
open/save). Serve it locally:

```powershell
pwsh -File src/EdMd/serve.ps1     # http://localhost:8080, opens Chrome
```

## Contributing

Pull requests welcome — branch, run `dotnet test`, open a PR against `main` (CI must pass).
See [`CONTRIBUTING.md`](CONTRIBUTING.md) for the full workflow and the guardrails that keep
the app safe.

## Releasing (maintainers)

Releases are tag-driven — full process and versioning policy in [`RELEASING.md`](RELEASING.md).
In short, push a version tag and CI does the rest:

```powershell
git tag v1.2.3 && git push origin v1.2.3
```

The [`Release`](.github/workflows/release.yml) workflow runs on `windows-latest`: it tests,
publishes the portable exe, builds the MSIX via `src/EdMd.Installer/build.ps1`, signs, and
attaches everything to a GitHub Release. `workflow_dispatch` does a dry build (artifacts on
the run, no Release).

### Signing

A repo variable `SIGNING_MODE` selects the branch:

| Mode | Cert | User trust step | Secrets / variables |
|------|------|-----------------|---------------------|
| `selfsigned` (interim) | committed `CN=EdMd` self-signed | yes (import `.cer` once) | `SELFSIGN_PFX_BASE64`, `SELFSIGN_PFX_PASSWORD` |
| `signpath` (target) | SignPath Foundation OV (publicly trusted) | no | `SIGNPATH_API_TOKEN`; vars `SIGNPATH_ORG_ID`, `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_POLICY_SLUG`, `SIGNPATH_PUBLISHER` |

The private key (`certs/*.pfx`) is **never committed** (see [`.gitignore`](.gitignore)); CI
reads it from a secret. See [`certs/README.md`](certs/README.md).

> Free code signing provided by [SignPath.io](https://signpath.io), certificate by
> [SignPath Foundation](https://signpath.org). _(Applies once `SIGNING_MODE=signpath`.)_

## Security & design

Origin-pinned WebView2, a CSP behind Toast's sanitizer, atomic file writes, a token-gated
loopback handoff for "Open in Browser", and encoding/line-ending preservation. Details in
[`CLAUDE.md`](CLAUDE.md) and [`docs/production-readiness-review.md`](docs/production-readiness-review.md).

## License

[MIT](LICENSE).
