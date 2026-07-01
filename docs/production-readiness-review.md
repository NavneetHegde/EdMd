# EdMd — Production-Readiness Review

## Context

EdMd works end-to-end (open/save, file associations, dual-mode editor, MSIX installer),
but it was built feature-first and has never been hardened for unattended use by real
users. A review of every source file surfaced **data-loss and crash paths that are
ship-blockers**, plus robustness gaps. This document brings it to a shippable state for
**self-distributed MSIX** at the "Critical + robustness" scope.

**Verdict: not production-ready yet.** The three things that would bite a real user
first: (1) closing the window with unsaved edits loses them silently; (2) any file
I/O error (permission denied, file locked/deleted, full disk) throws in an `async void`
handler and crashes the app; (3) if the WebView2 runtime is absent the app crashes on
launch with no explanation.

---

## Critical fixes (ship-blockers)

### 1. Guard unsaved changes on close and on open
Today only the **New** button checks the dirty flag (`index.html`, `btnNew` handler via
`confirm()`); closing the window and **Open** both discard edits silently.

- **Mirror dirty state to C#.** In `setDirty(v)` (`index.html`) also
  `send({type:'dirty', value:v})` in desktop mode; store `_isDirty` in `MainWindow`.
- **`Window.Closing` handler** (`MainWindow.xaml.cs`, wire in `MainWindow.xaml` or code):
  if `_isDirty` and not `_forceClose`, `MessageBox` Save / Don't Save / Cancel.
  - Cancel → `e.Cancel = true`.
  - Don't Save → set `_forceClose`, allow close.
  - Save → `e.Cancel = true`, post `{type:'requestSaveForClose'}`; JS calls existing
    `host.save()`; reuse the existing `save`/`saveAs` path, then on success set
    `_forceClose` and call `Close()`. If Save As is cancelled, abort the close.
- **Open guard**: in both `host.open()` implementations (`index.html`), if `isDirty`
  `confirm()` before proceeding — mirror the `btnNew` pattern.

### 2. Graceful file I/O error handling
`OpenFileIntoEditor` (`File.ReadAllTextAsync`) and `WriteAllTextAtomic` calls in the
`save`/`saveAs` cases can throw; they run inside `async void OnWebMessageReceived` →
unhandled → crash.

- Wrap reads/writes in `try/catch`; on failure `PostToJs(new {type="error", message})`.
- Add an `error` branch to the JS `message` listener → `setStatus(msg, 0)` styled red
  (or a `MessageBox` on the C# side for hard failures). This is the standard
  "wire both sides of the bridge" change noted in CLAUDE.md.

### 3. WebView2 runtime missing → friendly message
`EnsureCoreWebView2Async` throws `WebView2RuntimeNotFoundException` if the Evergreen
runtime is absent (not guaranteed on all Win10 machines).

- Wrap the init in `MainWindow_Loaded` in `try/catch`; on that exception show a
  `MessageBox` explaining the WebView2 runtime is required with the download URL, then
  `Application.Current.Shutdown()`.

### 4. Global exception handler + crash logging
`App.xaml.cs` is empty; crashes are currently silent.

- In `App` (`App.xaml.cs`) subscribe to `DispatcherUnhandledException`,
  `AppDomain.CurrentDomain.UnhandledException`, and
  `TaskScheduler.UnobservedTaskException`.
- Add a tiny dependency-free `Log` helper writing to
  `%LOCALAPPDATA%\EdMd\logs\edmd-yyyyMMdd.log` (keep the minimalist, no-NuGet ethos).
- Log all three handlers; for dispatcher exceptions show a brief apology dialog.
- Also wrap the bodies of the `async void` handlers (`OnWebMessageReceived`,
  `OnNavigationCompleted`, `MainWindow_Loaded`) in `try/catch` → `Log`, since
  `async void` exceptions bypass the dispatcher handler.

---

## Robustness

### 5. Harden `LocalWebServer` (`LocalWebServer.cs`)
- **Per-session nonce.** Generate a GUID token in `OpenInBrowserEditor`; launch Chrome
  at `index.html?session=<token>`; have `/__session` require a matching `?token=`.
  Browser JS reads its own `location.search` token and sends it. Stops other local
  processes/tabs from reading the in-memory document off the loopback port.
- **Dispose on exit.** Call `_browserServer?.Dispose()` from the `Window.Closing`
  handler so the listener/thread don't linger.
- **Path-check fix.** Compare against `_root + Path.DirectorySeparatorChar` (a sibling
  like `wwwroot-x` currently satisfies `StartsWith(_root)`). Low risk (loopback +
  file-must-exist) but trivial to fix.

### 6. Branding & UX polish
- **Title consistency** (`MainWindow.xaml` `Title="Ed Md"`, and the code-behind
  `"{file} — MD Editor"`): standardize on **EdMd** in both.
- **Right-click in Release**: `AreDefaultContextMenusEnabled = false` removes the
  copy/paste context menu for a *text editor*. Since the origin is pinned to
  `EdMd.local`, re-enable default context menus (keyboard copy/paste already works, but
  users expect the menu).
- **Save As filter** (`SaveAs`): include `*.markdown` alongside `*.md`.

---

## Distribution (self-distributed MSIX)

The current self-signed cert forces every downloader to import a cert as admin before
install — a non-starter for public download. These are recommendations/decision points,
not blocking code:

- **Get a trusted signing identity.** Either an OV/EV code-signing cert from a public CA,
  or **Azure Trusted Signing** (Microsoft's managed, low-cost service). `build.ps1`
  already takes `-PfxPath`; Trusted Signing signs via a signtool **dlib/metadata** call
  instead of a PFX, so it needs a small added branch in the sign step of
  `src/EdMd.Installer/build.ps1`. This is the one procurement decision.
- **Auto-update**: consider shipping a `.appinstaller` + a download page so
  `Add-AppxPackage` upgrades in place (optional).
- **Manifest**: bump `MaxVersionTested` in `Package.appxmanifest` to a current build
  (cosmetic). Keep `MinVersion 10.0.17763`.

---

## Files to modify (summary)

- `src/EdMd/MainWindow.xaml.cs` — dirty mirror + `Window.Closing` save-guard, I/O
  try/catch + `error` message, WebView2-missing handling, async-void guards, Save As
  filter, title string, server dispose + nonce wiring.
- `src/EdMd/MainWindow.xaml` — hook `Closing`, set `Title="EdMd"`.
- `src/EdMd/App.xaml.cs` — global exception handlers + `Log` helper.
- `src/EdMd/LocalWebServer.cs` — nonce/token check, path-check fix.
- `src/EdMd/wwwroot/index.html` — dirty mirror send, open guards, `error` handler,
  `requestSaveForClose` handler, browser-mode session token.
- `src/EdMd.Installer/build.ps1` + `Package.appxmanifest` — signing note / optional
  Trusted Signing branch; `MaxVersionTested` bump.
- Docs: fold the new behaviors into `CLAUDE.md` and `src/EdMd/README.md`.

Reuse existing: `setDirty`, `host.{open,save,saveAs}`, `applyOpenedFile/applySaved`
(`index.html`); `WriteAllTextAtomic`, `OpenFileIntoEditor`, `PostToJs`, the
`OnWebMessageReceived` switch (`MainWindow.xaml.cs`).

---

## Verification

1. **Build**: `dotnet build -c Debug` from `src/EdMd` (0 warnings), then `dotnet run`.
2. **Unsaved-on-close**: type, click the window ✕ → Save/Don't Save/Cancel dialog
   behaves correctly; Cancel keeps the window; Save writes then closes.
3. **Open/New guards**: with unsaved edits, Open and New both prompt before discarding.
4. **I/O errors**: open a file, delete it externally, Save → red error status / dialog,
   app stays alive. Open a file locked by another process → graceful error.
5. **WebView2 missing**: temporarily rename the WebView2 runtime (or test on a clean VM)
   → friendly dialog + clean exit, no crash dump.
6. **Crash logging**: force an exception → entry appears under
   `%LOCALAPPDATA%\EdMd\logs\`.
7. **Open in Browser nonce**: click it; confirm the URL carries `?session=<guid>` and a
   direct `GET /__session` without the token returns 403/empty.
8. **Installer smoke**: `build.ps1 -Sign`, install, double-click a `.md` → opens.
