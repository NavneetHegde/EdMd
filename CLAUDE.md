# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A native Windows Markdown editor: a WPF host window that hosts a single WebView2
control filling the whole window. All UI (tab strip, toolbar, editor, footer) is HTML/CSS/JS
rendered inside the WebView2; the C# side exists only to own the window and to do
the disk I/O the browser sandbox can't. The editor itself is the third-party
[Toast UI Editor](https://ui.toast.com/tui-editor) in WYSIWYG mode.

It is **multi-tab / multi-file**: the whole document model (one Toast editor instance
per open file, plus dirty state and which tab is active) lives in JS. C# stays
stateless about tabs — it keeps only per-path save metadata (see the bridge section) and
treats a JS-owned `tabId` as an opaque echo token on save round-trips. It is also
**single-instance**: a second launch forwards its file arguments to the running window
(new tabs) and exits, so every file opens in one app (see "Single instance" below).

The core is a handful of small files: `MainWindow.xaml(.cs)`, `App.xaml(.cs)`,
`wwwroot/index.html` + `wwwroot/app.js` (all the front-end logic), `LocalWebServer.cs`
(a loopback HTTP server used only by the "Open in Browser" action — see the bridge
section), `SessionStore.cs` (the pure serialize/parse for session restore — see the bridge
section), and `Log.cs` (a dependency-free file logger). `App.xaml.cs` wires the global
exception handlers **and** the single-instance mutex + named-pipe server.

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
MIME, 404/405, the `/__session` token gate, and path-traversal blocking), `AtomicFile`
(the temp-file+atomic-replace write, text and binary), `MarkdownFile` (the `.md`/`.markdown`
extension gate for command-line opens and single-instance forwarding), `SessionStore`
(the session-restore serialize/round-trip + tolerant parse of a corrupt session file), and
`ImageStore` (the image-paste MIME→ext allowlist, content-addressed filename + hash dedupe,
and relative-link helpers). GUI/bridge code (tabs,
single-instance plumbing, the WebView2 bridge) and the front-end JS are not covered. There is no linter or CI. `EdMd.slnx` at the repo root is the (XML) solution
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
  `open`, `save`/`saveAs` (both include `tabId`, `path`, `name`, and `content` — the full
  markdown string), `openInBrowser` (includes `markdown` + the active tab's `name`/`path`),
  `dirty` (mirrors an **aggregate** flag — true if *any* tab is unsaved — so the close guard
  works), `tabClosed` (a `path`, so C# drops that file's cached save metadata), `theme`
  (a `dark` bool, to match the native title bar — see Gotchas), `sessionSnapshot` (the whole
  tab model — `activeIndex` + a `tabs` array of `{name,path,dirty,content}` — for session
  restore / crash recovery, see below), `exportHtml`/`exportPdf` (both carry the active tab's
  `name` + a **standalone HTML document string JS built** from the editor's rendered HTML — see
  "Export" below), and `readyToClose` (the
  close handshake). C# owns the OpenFileDialog (now `Multiselect` — each file opens a
  tab)/SaveFileDialog and the actual `File.ReadAllText`/`WriteAllText`, each wrapped so an
  I/O failure logs + reports rather than crashing. **C# holds no "current file" —** instead
  `_docs` maps each on-disk path to a `DocMeta` (encoding + line ending to round-trip, plus
  the last-write timestamp for the external-change guard); untitled tabs use `DefaultMeta`.
  `save` with an empty `path` falls through to Save As. `SaveAs`/`SaveToPath` report their
  outcome to the tab (see `saved`/`saveResult` below) rather than returning a bool.
- **C# → JS (`PostToJs`):** C# pushes `{type}` messages back —
  `fileOpened` (name + content + full `path` + `assetsBase` → opens/reuses a tab), `saved`
  (`tabId` + name + full `path` + `assetsBase` — a successful write), `saveResult` (`tabId` +
  `ok:false` — a cancelled dialog or failed write), `imageSaved` (`reqId` + `ok` + `relPath` +
  `assetsBase`, or `ok:false` — the reply to `saveImage`; see Image paste below),
  `error` (a short message shown red in the footer),
  `requestSaveForClose` (asks JS to save every dirty tab so the window can close),
  `restoreSession` (`activeIndex` + a `tabs` array to rebuild last session's tabs — sent
  once on load, *always*, even empty, so JS stops holding back snapshots; see below),
  `exported` (a `name` — a successful PDF/HTML export, shown in the footer), and
  `browsers` (the `{id,name}` list of Chromium browsers found on this machine, sent once on
  load to fill the "Open in Browser" dropdown). The JS `message` listener in `app.js` routes
  `saved`/`saveResult` back to the originating tab by `tabId` (resolving that tab's pending
  save promise) and updates the footer path + dirty dot. (The footer shows the full path on
  the left and a highlighted live `~tokens · words · chars` counter in the right corner.)
- **Save round-trips are promise-based:** each `save`/`saveAs` registers a resolver in a
  JS `pendingSaves` map keyed by `tabId`; the matching `saved` (→ true) or `saveResult`
  (→ false) resolves it. This lets the close handshake `await` every dirty tab's save.
- **Unsaved-changes guard:** `MainWindow_Closing` checks the mirrored `_isAnyDirty`; on
  a dirty close it prompts Save/Don't Save/Cancel. "Save" cancels the close and posts
  `requestSaveForClose`; JS `saveAllForClose()` saves each dirty tab **in turn** (activating
  it first so a Save As dialog is clearly for that file) and, only if all succeed, posts
  `readyToClose` — which sets `_forceClose` and re-`Close()`s. Any cancelled save aborts the
  close (JS simply never sends `readyToClose`). Per-tab close (chip ×, middle-click, Ctrl+W)
  `confirm()`s in JS before discarding a dirty tab; the app never drops below one tab.
- **Session restore / crash recovery (desktop only):** JS is the source of truth for the tab
  model, so it mirrors a whole-model snapshot (`sessionSnapshot`) to C# — debounced on edits,
  immediate on structural changes (open/close/activate/save) — and C# writes it atomically to
  `%LOCALAPPDATA%\EdMd\session.json` (`SaveSession`). On launch, `RestoreSession` (called at the
  top of `OnNavigationCompleted`, **before** command-line files) re-reads that file and posts one
  `restoreSession` batch: saved+clean tabs show fresh disk content (and their `DocMeta` is
  repopulated so a later save round-trips + the external-change guard has a baseline), saved+dirty
  and untitled tabs keep their **recovered buffer**, and a saved+clean tab whose file is gone is
  dropped. The (de)serialization is the pure, unit-tested `SessionStore` (`Serialize`/`Parse`,
  `Parse` returns null on any malformed input so a corrupt file never crashes the launch). JS
  suppresses snapshots (`restoring` flag) until `restoreSession` arrives — otherwise the empty tab
  it boots with would clobber the file first; a 3s safety timeout un-gates if the message never
  comes. `restoreSession` is therefore always sent, even for an empty/absent session.
- **Image paste / drag-drop (`saveImage` → `SaveImage`):** Toast's single `addImageBlobHook`
  intercepts both clipboard paste and file drop and hands JS the image `Blob`; `insertImage`
  (in `app.js`) resolves it to a URL and calls Toast's `callback(url,'')` so the insert is a
  normal, undoable, dirty-flipping edit. **A saved tab** persists the bytes next to its doc:
  JS posts `saveImage` (base64) and C# `SaveImage` re-validates the ext against `ImageStore`'s
  allowlist (`png/jpg/gif/webp` — **no SVG**), enforces a 25 MB cap *before* the base64 decode,
  writes into a sibling `assets/` folder via `AtomicFile.WriteAllBytes` under a content-addressed
  name (`img-<ts>-<hash8>.<ext>`; a matching hash is **reused**, so a repeat paste dedupes to one
  file), and replies `imageSaved` with the relative `assets/…` link. **An untitled tab** (empty
  `docPath`), the browser build, or any failure gets `ok:false` and JS falls back to an inline
  base64 **data URI** (so the image still renders and travels inside the buffer). The pure,
  security-sensitive bits (MIME→ext, filename, hash, dedupe glob, assets-dir, relative link) live
  in the unit-tested `ImageStore.cs`; only the disk write + bridge glue are in `MainWindow`.
  **Rendering (the non-obvious part):** the editor page is served from `EdMd.local` (mapped to
  `wwwroot`), so a *relative* `assets/…` link would 404 in the live WYSIWYG surface — Toast renders
  images from the node's `imageUrl`, which is *also* what `getMarkdown` serializes, so there's no
  separate "display src." So C# maps each document's folder to a **per-folder virtual host**
  (`AssetsBaseFor` → `a<hash>.edmdassets.local`, allowed by CSP's `img-src https:`) and hands JS
  that absolute base as `assetsBase` on `fileOpened`/`saved`/`restoreSession`/`imageSaved`. JS keeps
  the editor content **absolute** (so images render) but converts to/from **relative** at every
  persistence boundary via `absolutizeAssets`/`relativizeAssets` (anchored on the `](assets/`
  token) — `tabMarkdown(tab)` is the relativised markdown used for save, snapshot, copy, and the
  browser handoff, while `loadContent` absolutises on the way in. So the `.md` on disk stays
  portable (relative links render on GitHub/other editors) while the live editor shows the image.
  Non-goals (v1): image resize/compress, alt-text prompts, re-linking on rename/move (a Save As to
  a *different* folder re-points the in-editor URLs but does **not** copy the image files).
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
  The handoff carries the *active* tab's markdown/name/path (C# no longer tracks one open doc).
- **Export to PDF / HTML (`exportHtml`/`exportPdf`):** **JS** builds the standalone HTML document
  (`buildExportHtml` — the editor's `getHTML()` wrapped in a `.markdown-body` article with an
  inlined, deliberately *light* print stylesheet, so desktop and browser produce the same output)
  and hands it to the host. Desktop: `ExportHtml` writes it to a chosen `.html` (via `AtomicFile`);
  `ExportPdf` can't print the main WebView2 (it hosts the editor chrome), so it renders the HTML in
  a throwaway **offscreen** `CoreWebView2Controller` (created from `Browser.CoreWebView2.Environment`,
  `IsVisible=false`, **script disabled** since the document may be untrusted) loaded from a temp
  file, then `PrintToPdfAsync`. Both report back with `exported`. Browser build (no C#): HTML → a
  Blob download; PDF → a new window it calls `print()` on ("Save as PDF"). Relative-path images
  won't resolve in the export (the doc is self-contained) — data:/absolute URLs do.
- **File associations / double-click:** `OnNavigationCompleted` scans
  `Environment.GetCommandLineArgs()` for `.md`/`.markdown` paths and opens **each** into its
  own tab once the page is ready. This is why open-with works.
- **Single instance:** `App.OnStartup` grabs a named mutex (`Local\EdMd.SingleInstance.Mutex`).
  The first instance owns it and runs a named-pipe server (`EdMd.SingleInstance.Pipe`); any
  later launch finds the mutex held, connects to the pipe, writes its `.md` file args (one
  per line), and exits. The server opens each forwarded path (`MainWindow.EnqueueOpenFile` →
  queued until `_webReady`, then drained in `OnNavigationCompleted`) and calls `BringToFront`.
  `MainWindow` is assigned **before** the pipe server starts so an immediate hand-off is never
  dropped. JS de-dupes by path (`openInTab`), so forwarding an already-open file just focuses
  its tab. Note `App.xaml` has no `StartupUri` — `OnStartup` creates the window itself.

If you add a feature that crosses the boundary (e.g. a new toolbar action that
touches disk), you must wire it in **both** places: add a message `type` in the JS
`send(...)` calls and handle it in the C# `switch`, or vice versa.

**Dual-mode UI:** `index.html` runs in two hosts. It checks `IS_DESKTOP`
(`window.chrome.webview` present) and builds a `host` object with `open/save/saveAs`:
inside the WPF app those post bridge messages (above); in a plain browser they use the
**File System Access API** (`showOpenFilePicker`/`showSaveFilePicker`, both multi-select,
so browser mode is multi-tab too — the handle is stashed on the tab record). Both paths
funnel through the shared `openInTab`/`applySavedToTab` UI helpers, so behaviour matches.
Any new file action must be implemented on **both** host objects. Browser mode needs a secure
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
- Toast's built-in mode switch is deliberately hidden (`hideModeSwitch: true` + a CSS
  rule); the raw-markdown toggle, zoom, theme, and reading width (the centered-column
  control, via the `--read-col` CSS var) are **global preferences** persisted in
  `localStorage` and applied to *every* tab's editor (see `applyModeToEditor`/
  `applyThemeToEditor`, called per-tab on creation and when the preference changes).
- **Theme-aware title bar:** the theme's light/dark is the one preference that also reaches
  C# — JS posts a `theme` message and `SetTitleBarDark` sets the DWM immersive-dark-mode
  attribute so the native caption matches the editor. It uses attribute id 20 with a
  fallback to the pre-20H1 id 19, and no-ops on unsupported builds.
- **Tabs are JS-only.** The tab strip, per-tab Toast instances, active-tab tracking, and
  dirty dots all live in `app.js`; C# only ever sees an aggregate dirty flag and per-path
  `DocMeta`. A Save As that lands on a path another tab already has open is de-duped in JS
  (`dedupeTabsByPath`) so two tabs can't share one path / one `_docs` entry.
