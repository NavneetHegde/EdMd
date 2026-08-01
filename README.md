# EdMd — a native Windows Markdown editor

[![CI](https://github.com/NavneetHegde/EdMd/actions/workflows/ci.yml/badge.svg)](https://github.com/NavneetHegde/EdMd/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

EdMd is a small, fast **WYSIWYG Markdown editor** for Windows. It's a WPF window hosting a
single WebView2 that renders the whole UI (tab strip, toolbar, editor, footer) as HTML/CSS/JS,
with the [Toast UI Editor](https://ui.toast.com/tui-editor) doing the editing. The C# side only
owns the window and the disk I/O the browser sandbox can't do. Built for authoring prompts,
skills, `CLAUDE.md` files, PRs, and specs — with a live token/word/char counter.

**Multi-file, single instance:** open several documents in tabs (Ctrl+T new, Ctrl+W close,
middle-click to close, or open/select many files at once). Double-clicking a `.md` in Explorer
opens it as a new tab in the running window instead of launching a second copy.

**Works offline, stays offline.** Everything ships with the app — the editor and the diagram
renderer are vendored, not loaded from a CDN — and the page's Content-Security-Policy allows no
remote origin at all, so opening a document never tells anyone you opened it. See
[Security & design](#security--design).

> <img width="1520" height="793" alt="image" src="https://github.com/user-attachments/assets/519be67c-332c-439e-aa3b-2757c6052dbc" />


In Browser

<img width="1920" height="1032" alt="image" src="https://github.com/user-attachments/assets/86bb2144-1458-46c8-8df5-f960ce7ab9f2" />


## Features

**Writing**

- **WYSIWYG by default**, with a **Raw** toggle for the markdown source — and a Preview tab
  next to it when you want to see the rendered result while editing the source.
- **Mermaid diagrams.** A ` ```mermaid ` block is shown as the *diagram* in WYSIWYG (click it to
  edit the source behind it), as markdown in Raw, and rendered in the preview. Flowcharts,
  sequence, gitGraph, pie — whatever Mermaid supports; a broken diagram gets a plain error box
  instead of a crash.
- **Paste or drag in an image.** In a saved document the bytes are written to a sibling
  `assets/` folder and linked relatively (`![](assets/img-….png)`), so the `.md` stays portable
  and renders on GitHub. Identical images are stored once. An unsaved document embeds the image
  inline as a data URI instead, so nothing is written next to a file that doesn't exist yet.
- **Find & replace** with live highlighting, case and regex options.
- **Live counter** in the footer: `~tokens · words · chars` — handy when writing for an LLM.

**Documents**

- **Tabs**, multi-select open, and single-instance file associations (above).
- **Session restore & crash recovery.** Reopening picks up the tabs you had, including unsaved
  buffers.
- **Export to PDF or HTML** — a clean, self-contained, print-friendly document. Diagrams are
  included as pictures.
- **Copy as Markdown / plain text** — the whole document to the clipboard in one click, ready
  to paste into a chat.
- **Open in Browser** — hands the current document to Chrome/Edge running the same editor.
- Encoding and line endings (UTF-8/BOM, CRLF/LF) are preserved on save, writes are atomic, and
  an external change to an open file is detected before it can be overwritten.

**Appearance**

- Twelve themes (light and dark), zoom, and an adjustable reading-column width. The native
  title bar follows the theme.

## Install

Grab the latest build from the [**Releases**](../../releases) page. Two options:

### MSIX installer (recommended)

Registers `.md` / `.markdown` file associations, adds a Start-menu entry, and supports
in-place upgrades.

```powershell
Add-AppxPackage .\EdMd-<version>.msix
```

> **Interim trust step.** Releases are currently signed with a self-signed certificate, so
> Windows needs to trust it once before the MSIX will install. Download `edmd-codesign.cer`
> from the same release and run, in an **elevated** PowerShell:
>
> ```powershell
> Import-Certificate -FilePath .\edmd-codesign.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
> Import-Certificate -FilePath .\edmd-codesign.cer -CertStoreLocation Cert:\LocalMachine\Root
> ```
>
> This step goes away if builds move to a publicly-trusted certificate.

### Portable executable (no install)

Download `EdMd-<version>-win-x64.exe` and run it. It's self-contained (no .NET needed).
No file associations, and SmartScreen may show "Windows protected your PC" → **More info →
Run anyway** until the download builds reputation.

Prefer a zip? `EdMd-<version>-win-x64.zip` has the same portable exe plus `LICENSE.txt` —
extract it anywhere and run `EdMd.exe`. A zipped download also sidesteps some browser/AV
"can't download .exe" friction.

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

Releases are signed with a **self-signed** `CN=EdMd` certificate (CI reads the key from the
secrets `SELFSIGN_PFX_BASE64` / `SELFSIGN_PFX_PASSWORD`), which is why the MSIX needs the
one-time trust step [above](#msix-installer-recommended).

The private key (`certs/*.pfx`) is **never committed** (see [`.gitignore`](.gitignore)); CI
reads it from a secret. See [`certs/README.md`](certs/README.md) and, for the full release
process, [`RELEASING.md`](RELEASING.md).

## Security & design

A markdown file you open is untrusted input, and the WebView2 rendering it can also write to
disk — so the boundary between them is where the design effort went:

- **Origin-pinned WebView2.** The UI is served from a virtual host; navigation anywhere else is
  cancelled and opened in your normal browser instead, and messages from any other origin are
  ignored before they can touch disk.
- **CSP behind Toast's sanitizer.** No inline script, no `unsafe-eval`, and **no remote origin**
  — including images, so a document can't phone home with a tracking pixel.
- **No network.** The app makes no requests of its own; the browser engine it embeds is started
  with background networking, component updates, crash reporting and telemetry switched off.
  One caveat, measured and documented in [`CLAUDE.md`](CLAUDE.md): the WebView2 runtime still
  makes a Windows-account probe of its own that no in-process setting reaches.
- **Careful writes.** Atomic saves, preserved encoding/line endings, an external-change guard,
  and a size- and type-limited path for pasted images that can't escape the document's folder.
- **Token-gated loopback** for the "Open in Browser" handoff, so nothing else on localhost can
  read your document.

Details in [`CLAUDE.md`](CLAUDE.md) and
[`docs/production-readiness-review.md`](docs/production-readiness-review.md).

## License

[MIT](LICENSE).
