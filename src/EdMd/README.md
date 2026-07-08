# MD Editor (.NET / WPF)

A native Windows app (WPF host + WebView2 for the editor UI) that opens and saves
`.md` files directly on disk, with the same split-pane editor/preview as the
browser version.

## Requirements to build

- Windows 10/11
- .NET 10 SDK: https://dotnet.microsoft.com/download
- WebView2 Runtime (already preinstalled on most Windows 10/11 machines;
  if missing, Windows will prompt to install it automatically)

## Build & run

Open a terminal in this folder:

```
dotnet restore
dotnet run
```

## Produce a standalone .exe

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The `.exe` will be in:
```
bin\Release\net10.0-windows\win-x64\publish\EdMd.exe
```

This single file can be copied anywhere — no separate .NET install needed on
the target machine (self-contained).

## Run in Chrome (no desktop app)

The same editor UI also runs as a plain web app in **Chrome or Edge**, with real local
file open/save via the browser's [File System Access API](https://developer.mozilla.org/en-US/docs/Web/API/File_System_API).
`index.html` auto-detects its host: inside the WPF app it uses the C# bridge; in a
browser tab it uses the File System Access API — one codebase, both modes.

```powershell
./serve.ps1              # serves wwwroot on http://localhost:8080 and opens Chrome
./serve.ps1 -Port 9000   # pick a different port
./serve.ps1 -NoLaunch    # serve only, don't open a browser
```

`serve.ps1` is a tiny loopback-only static server (raw `TcpListener` on `127.0.0.1`,
no admin needed). Serving over `http://localhost` is required because the File System
Access API only works in a **secure context** — opening `index.html` as a `file://`
path will load the editor but leave Open/Save disabled.

**From the desktop app:** the toolbar's **Open in Browser** button does the same thing
without `serve.ps1` — it starts an in-process loopback server (`LocalWebServer.cs`) and
opens the *full* editor in Chrome pre-loaded with whatever you're currently editing.

**Caveat:** the File System Access API is **Chromium-only** (Chrome/Edge). In
Firefox/Safari the editor still runs, but Open/Save show a "needs Chrome or Edge"
notice. Ctrl+S works in both hosts. The browser never exposes a file's full path, so
the footer shows the filename there (the desktop app shows the full path).

## Installer / file associations

For distribution (and automatic `.md` / `.markdown` "Open with" + double-click
associations), use the **MSIX installer** in [`../EdMd.Installer`](../EdMd.Installer/).
It packages this app, registers the file types via the package manifest, and is signed
with your code-signing certificate:

```powershell
../EdMd.Installer/build.ps1 -PfxPath <cert.pfx> -PfxPassword <pwd> -Version 1.0.0.0
```

See `EdMd.Installer/README.md` for install/uninstall and signing details.

**Quick manual association (no installer):**
Right-click any `.md` file → Open with → Choose another app → More apps →
find `EdMd.exe` (Browse to the published exe if not listed) → "Always use this app".

## Notes

- **Multi-file tabs / single instance:** each open file gets its own tab (New/Open/templates
  never clobber an existing document — they open or reuse a tab). A second launch (double-click,
  "Open with", or re-running the exe) forwards its files to the already-running window as new
  tabs and brings it to front, rather than opening a duplicate app.
- **Unsaved changes** are guarded: closing the window with pending edits prompts to save every
  dirty tab first; closing a single dirty tab (× / middle-click / Ctrl+W) prompts before
  discarding just that tab. Save/open/write failures show a message instead of
  crashing, and details are logged to `%LOCALAPPDATA%\EdMd\logs\edmd-<date>.log`
  (useful for support). If the WebView2 runtime is missing, EdMd explains how to install
  it rather than crashing on launch.
- `icon.ico` in the project root is the app icon (referenced by the `.csproj`
  `<ApplicationIcon>` line). Replace it with your own `.ico` to rebrand, or remove
  the `<ApplicationIcon>` line to build without one.
- The editor UI is the [Toast UI Editor](https://ui.toast.com/tui-editor)
  (v3.2.2), **vendored locally** under `wwwroot/vendor/toastui/` — the main CSS,
  the JS bundle, and the separate dark-theme CSS (`toastui-editor-dark.min.css`)
  that powers the dark mode toggle. It is intentionally not loaded from a CDN: the
  editor JS runs inside the WebView2 that can write files, so a floating CDN version
  would be code-execution-with-disk-access risk. This also makes the app work fully
  offline. To upgrade, re-download the three files from `uicdn.toast.com` and bump
  the version note in `index.html`.
