# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A native Windows Markdown editor: a WPF host window that hosts a single WebView2
control filling the whole window. All UI (toolbar, editor, footer) is HTML/CSS/JS
rendered inside the WebView2; the C# side exists only to own the window and to do
the disk I/O the browser sandbox can't. The editor itself is the third-party
[Toast UI Editor](https://ui.toast.com/tui-editor) in WYSIWYG mode.

The core is a handful of small files: `MainWindow.xaml(.cs)`, `App.xaml(.cs)`,
`wwwroot/index.html`, `LocalWebServer.cs` (a loopback HTTP server used only by the
"Open in Browser" action — see the bridge section), and `Log.cs` (a dependency-free file
logger). `App.xaml.cs` wires the global exception handlers.

The same `index.html` also runs as a standalone web app in Chrome/Edge (no WPF host);
`serve.ps1` hosts it on `http://localhost` for that. See "Dual-mode UI" below.

## Commands

Run all from `src/EdMd/`. Requires the .NET 10 SDK and (at runtime) the
WebView2 Runtime.

```
dotnet restore
dotnet run                # build + launch
```

Standalone single-file exe:
```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
# → bin/Release/net10.0-windows/win-x64/publish/EdMd.exe
```

Run the same UI as a web app in Chrome/Edge (no desktop host):
```
pwsh -File src/EdMd/serve.ps1     # serves wwwroot on http://localhost:8080, opens Chrome
```

Tests live in `src/EdMd.Tests/` (xUnit) — run them with:
```
dotnet test        # from src/EdMd.Tests/ or the repo root
```
They cover the pure/security-sensitive C# logic: `LocalWebServer` (static file serving,
MIME, 404/405, the `/__session` token gate, and path-traversal blocking) and `AtomicFile`
(the temp-file+atomic-replace write). GUI/bridge code and the front-end JS are not
covered. There is no linter or CI. `EdMd.slnx` at the repo root is the (XML) solution
file (now lists both projects). Note the project file is `EdMd.csproj` but `AssemblyName` is
`EdMd`, so the built/published binary is **`EdMd.exe`** — installer and
manifest references depend on that name; don't "fix" the mismatch.

## Installer (`src/EdMd.Installer/`)

An **MSIX** package built script-side with the Windows SDK's `MakeAppx`/`SignTool`
(not a `.wapproj`), driven by `build.ps1` (publish self-contained → stage
manifest+assets → pack → sign). Key rules if you touch it:

- `Package.appxmanifest` `Executable="EdMd.exe"` and the `.md`/`.markdown`
  file-type association pass `"%1"`, which `MainWindow.OnNavigationCompleted` reads
  from the command line — keep that contract.
- `Identity/Publisher` **must equal** the signing cert Subject; `build.ps1` injects it
  (and `Version`) into the staged manifest, so the values in the file are placeholders.
- `Find-SdkTool` prefers stable SDKs — Insider/preview `MakeAppx` (build ≥ 27000)
  wrongly rejects the full-trust manifest; there's a `/nv` pack retry as a backstop.
- Production build: `build.ps1 -PfxPath <pfx> -PfxPassword <pwd>`; dev smoke test:
  `build.ps1 -Sign` (self-signed). See `src/EdMd.Installer/README.md`.

## Architecture: the C# ⇄ JS bridge

The only non-trivial thing here is how the WPF host and the web UI talk. They
exchange JSON messages over WebView2's `postMessage` channel — the desktop app uses no
HTTP server or other IPC. (`LocalWebServer` exists only for the browser handoff, below.)

- **Loading the UI:** `MainWindow_Loaded` maps `wwwroot/` to the virtual host
  `https://EdMd.local/` via `SetVirtualHostNameToFolderMapping`, then navigates
  to `index.html`. The editor UI is served from this fake origin, not from a file://
  path.
- **JS → C# (`OnWebMessageReceived`):** the page sends `{type}` messages —
  `open`, `save`, `saveAs` (the last two include `content`, the full markdown
  string), `openInBrowser` (includes `markdown`), and `dirty` (mirrors the editor's
  unsaved flag to C# so the close guard works). C# owns the
  OpenFileDialog/SaveFileDialog and the actual `File.ReadAllText`/`WriteAllText`,
  each wrapped so an I/O failure logs + reports rather than crashing. The
  currently-open path is tracked in `_openedFilePath`; `save` with no path falls
  through to Save As. `SaveAs`/`SaveToPath` return a **bool** (written vs
  cancelled/failed), which drives the save-then-close handshake.
- **C# → JS (`PostToJs`):** C# pushes `{type}` messages back —
  `fileOpened` (name + content + full `path`), `saved` (name + full `path`),
  `error` (a short message shown red in the footer), `requestSaveForClose` (asks
  JS to save so the window can close), and `browsers` (the `{id,name}` list of Chromium
  browsers found on this machine, sent once on load to fill the "Open in Browser"
  dropdown). The JS `message` listener in `index.html`
  reacts by calling `editor.setMarkdown(...)`, updating the filename label + footer
  path, and clearing the dirty flag. (The footer shows the full path on the left and a
  highlighted live `words · chars · lines` counter in the right corner.)
- **Unsaved-changes guard:** `MainWindow_Closing` checks the mirrored `_isDirty`; on
  a dirty close it prompts Save/Don't Save/Cancel. "Save" cancels the close, posts
  `requestSaveForClose`, and `HandleSaveResult` re-closes (`_forceClose`) only after the
  save succeeds. The JS `host.open()` (both hosts) `confirm()`s before discarding edits.
- **Open in Browser (`openInBrowser` → `OpenInBrowserEditor`):** opens the *full*
  editor in Chrome, pre-loaded with the current document — not a static preview. C#
  lazily starts `LocalWebServer` (an in-process loopback `TcpListener` on an ephemeral
  port serving `wwwroot` — needed because the browser build's File System Access API
  requires the `http://localhost` secure context), stashes the current markdown/name/
  path plus a per-launch nonce (`SessionToken`), and launches Chrome at
  `index.html?session=1&token=<nonce>`. The browser build fetches the handoff from the
  server's `/__session?token=…` route, which 403s without the matching token so nothing
  else on loopback can read the document. The dropdown next to the button lets the user
  pick a specific installed browser: the `openInBrowser` message may carry a `browserId`
  from the C#-sent `browsers` list, and C# maps that Id back to a path from its own
  `DiscoverBrowsers()` result (it never launches a path supplied by JS). With no
  `browserId`, `FindChromiumBrowser` auto-picks (Chrome → Edge → Brave/Opera/Vivaldi, via
  App Paths → standard dirs) because open/save needs the File System Access API; only if
  none is found does it fall back to the OS default browser (open/save may be unavailable).
- **File associations / double-click:** `OnNavigationCompleted` scans
  `Environment.GetCommandLineArgs()` for a `.md`/`.markdown` path and opens it once
  the page is ready. This is why open-with works.

If you add a feature that crosses the boundary (e.g. a new toolbar action that
touches disk), you must wire it in **both** places: add a message `type` in the JS
`send(...)` calls and handle it in the C# `switch`, or vice versa.

**Dual-mode UI:** `index.html` runs in two hosts. It checks `IS_DESKTOP`
(`window.chrome.webview` present) and builds a `host` object with `open/save/saveAs`:
inside the WPF app those post bridge messages (above); in a plain browser they use the
**File System Access API** (`showOpenFilePicker`/`showSaveFilePicker`). Both paths funnel
through shared `applyOpenedFile`/`applySaved` UI helpers, so behaviour matches. Any new
file action must be implemented on **both** host objects. Browser mode needs a secure
context (`http://localhost`), reached two ways: `serve.ps1` for the standalone web app,
or `LocalWebServer` when the desktop app's "Open in Browser" hands a document off (adds
the `?session=1` load path). The File System Access API is Chromium-only — in
Firefox/Safari the editor runs but open/save show a "needs Chrome or Edge" notice, and
the browser never exposes a file's full path (so the footer shows the name there).

## Gotchas

- **Crashes & errors:** `App.xaml.cs` traps `DispatcherUnhandledException` (shows a
  dialog, keeps running), `AppDomain.UnhandledException`, and unobserved task
  exceptions. The `async void` WebView2 handlers wrap their bodies in try/catch. All of
  these — plus file I/O failures — write to `%LOCALAPPDATA%\EdMd\logs\edmd-<date>.log`
  via `Log.Write`. If EdMd needs the WebView2 runtime and it's missing,
  `MainWindow_Loaded` shows an install message and exits instead of throwing.
- The editor is the Toast UI Editor (v3.2.2), **vendored locally** under
  `wwwroot/vendor/toastui/` (main CSS, JS bundle, and the separate dark-theme CSS).
  It is deliberately not loaded from a CDN — the editor JS runs in the WebView2 that
  can write files, so a floating CDN version is a code-execution risk. Upgrading
  means re-downloading the three files and bumping the version note in `index.html`.
- **Security model:** the WebView2 is pinned to the `EdMd.local` origin.
  `NavigationStarting`/`NewWindowRequested` cancel any off-origin navigation and open
  such links in the OS browser; `OnWebMessageReceived` checks `e.Source` before
  touching disk. This stops an untrusted `.md` (via a clicked link) from loading a
  remote page that drives the file-writing bridge. Keep these guards if you touch
  `MainWindow.xaml.cs`.
- `icon.ico` (project root) is the app icon, referenced by `EdMd.csproj`
  (`<ApplicationIcon>`) and embedded into the exe. It's a themed multi-size PNG-ICO;
  replace the file in place to rebrand.
- `wwwroot/**` is copied to output via `PreserveNewest`; editing `index.html`
  requires a rebuild/re-run to see changes in the packaged app (`dotnet run` handles
  this).
- The mode switch (raw-markdown tab) is deliberately hidden — Toast is locked to
  WYSIWYG via `hideModeSwitch: true` plus a CSS rule. Zoom and theme are
  browser-side only (persisted in `localStorage`), invisible to C#.
