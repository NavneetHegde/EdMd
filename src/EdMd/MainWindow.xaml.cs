using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace EdMd;

public partial class MainWindow : Window
{
    // The only origin the app UI is ever served from. The WebView2 exposes the
    // file-writing bridge (window.chrome.webview) to ANY page it loads, so we pin
    // the app to this origin: navigation elsewhere is blocked and messages from
    // other origins are ignored. Without this, clicking a link in an untrusted
    // .md file could navigate to a remote page that drives the bridge.
    private const string AppOrigin = "https://EdMd.local";

    // Per-open-file metadata, keyed by full path. The JS side owns the tab/document model
    // (content, dirty state, which tab is active); C# only needs, for each file that lives on
    // disk, the encoding + line ending to round-trip on save and the last-write timestamp to
    // detect an external edit before overwriting. Untitled (never-saved) tabs have no entry
    // here — they use DefaultMeta until their first Save As creates a path.
    private sealed record DocMeta(Encoding Encoding, string Newline, DateTime? WriteTimeUtc);

    // UTF-8 without a BOM, LF line endings — what a fresh/never-saved document saves as.
    private static readonly DocMeta DefaultMeta =
        new(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), "\n", null);

    private readonly Dictionary<string, DocMeta> _docs = new(StringComparer.OrdinalIgnoreCase);

    private DocMeta MetaFor(string? path) =>
        path != null && _docs.TryGetValue(path, out var m) ? m : DefaultMeta;

    // Aggregate dirty flag mirrored from JS (true if ANY tab has unsaved changes); the C# side
    // needs it only to guard the window-close path. _forceClose latches the close once the user
    // has chosen to discard, or JS has reported every dirty tab saved (see the close handshake).
    private bool _isAnyDirty;
    private bool _forceClose;

    // Lazily started when the user first clicks "Open in Browser"; serves wwwroot over
    // http://localhost so the full editor (with File System Access open/save) runs in Chrome.
    private LocalWebServer? _browserServer;

    // Chromium browsers discovered on this machine, sent to the UI to populate the
    // "Open in Browser" dropdown. The UI hands back an Id from this list (never a path),
    // and we launch the matching Path — so JS can only ever start a browser we found.
    private List<(string Id, string Name, string Path)> _browsers = new();

    // Single-instance hand-off (see App.OnStartup): a second launch forwards its file paths to
    // this window. If they arrive before the WebView2 has finished loading (_webReady), we queue
    // them and drain the queue in OnNavigationCompleted once the page is ready to receive them.
    private bool _webReady;
    private readonly List<string> _pendingOpenPaths = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += (_, _) => _browserServer?.Dispose();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await Browser.EnsureCoreWebView2Async(null);
        }
        catch (Exception ex)
        {
            // The Evergreen WebView2 runtime isn't installed — explain instead of crashing.
            Log.Write("WebView2 init failed: " + ex);
            MessageBox.Show(this,
                "EdMd needs the Microsoft Edge WebView2 Runtime, which isn't installed on this PC.\n\n" +
                "Install it (free) from:\n" +
                "https://developer.microsoft.com/microsoft-edge/webview2/\n\n" +
                "then start EdMd again.",
                "EdMd — WebView2 Runtime required", MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown();
            return;
        }

        try
        {
            var settings = Browser.CoreWebView2.Settings;
#if !DEBUG
            settings.AreDevToolsEnabled = false;
            // Default context menus stay ENABLED even in Release: this is a text editor and
            // users expect right-click copy/paste. The origin is pinned to EdMd.local, so the
            // menu isn't an untrusted-content vector.
#endif
            settings.IsStatusBarEnabled = false;

            string wwwroot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
            Browser.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "EdMd.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);

            Browser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            Browser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            // Keep the app pinned to its own origin; send anything else to the OS browser.
            Browser.CoreWebView2.NavigationStarting += OnNavigationStarting;
            Browser.CoreWebView2.NewWindowRequested += OnNewWindowRequested;

            Browser.CoreWebView2.Navigate("https://EdMd.local/index.html");
        }
        catch (Exception ex)
        {
            Log.Write("WebView2 setup failed: " + ex);
        }
    }

    // Warn about unsaved edits before the window closes. The documents live in JS (one per
    // tab), so a "Save" answer round-trips: cancel the close, ask JS to save every dirty tab,
    // and close once JS reports they're all saved (the readyToClose message). "Don't Save"
    // closes; "Cancel" stays. A cancelled Save As mid-flow leaves the window open (JS simply
    // never sends readyToClose).
    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_forceClose || !_isAnyDirty)
            return;

        var result = MessageBox.Show(this,
            "You have unsaved changes. Save before closing?",
            "EdMd", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        if (result == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
        }
        else if (result == MessageBoxResult.No)
        {
            _forceClose = true; // discard and let the close proceed
        }
        else // Yes — save every dirty tab first, then close when JS reports readyToClose
        {
            e.Cancel = true;
            _ = PostToJs(new { type = "requestSaveForClose" });
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Allow only navigation within our own origin (the editor UI). Any other
        // link — e.g. an http(s) URL clicked in a rendered markdown document — is
        // cancelled and opened in the user's default browser instead, so remote
        // pages never load inside the privileged WebView2.
        if (IsAppOrigin(e.Uri))
            return;

        e.Cancel = true;
        OpenInSystemBrowser(e.Uri);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Never let the page spawn its own WebView2 window; route pop-ups to the OS browser.
        e.Handled = true;
        OpenInSystemBrowser(e.Uri);
    }

    private static bool IsAppOrigin(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) &&
        string.Equals(u.GetLeftPart(UriPartial.Authority), AppOrigin, StringComparison.OrdinalIgnoreCase);

    private static void OpenInSystemBrowser(string uri)
    {
        // Only hand well-formed http/https links to the shell — never arbitrary schemes.
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) &&
            (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = u.AbsoluteUri,
                    UseShellExecute = true
                });
            }
            catch { /* ignore — a failed launch must not crash the editor */ }
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        // async void: swallow-and-log so a startup file error can't crash the process.
        try
        {
            // Reopen the tabs from the previous session first, so they form the base the user
            // left off at; any file also passed on the command line de-dupes against them in JS
            // (openInTab focuses an already-open path) and lands after the restored tabs.
            await RestoreSession();

            // Handle files passed on the command line (double-click, or "Open with"). Multiple
            // .md/.markdown args each open into their own tab.
            var args = Environment.GetCommandLineArgs();
            foreach (var arg in args)
            {
                if (MarkdownFile.HasMarkdownExtension(arg) && File.Exists(arg))
                    await OpenFileIntoEditor(arg);
            }

            // The page can now receive documents; open anything a second instance forwarded
            // while we were still loading (see EnqueueOpenFile / App's single-instance server).
            _webReady = true;
            if (_pendingOpenPaths.Count > 0)
            {
                var pending = _pendingOpenPaths.ToArray();
                _pendingOpenPaths.Clear();
                foreach (var p in pending)
                    if (File.Exists(p))
                        await OpenFileIntoEditor(p);
            }

            // Tell the UI which browsers are installed, so its "Open in Browser" dropdown
            // can offer them. Cached so the later launch resolves the Id back to a path.
            _browsers = DiscoverBrowsers();
            await PostToJs(new
            {
                type = "browsers",
                list = _browsers.ConvertAll(b => new { id = b.Id, name = b.Name })
            });
        }
        catch (Exception ex)
        {
            Log.Write("OnNavigationCompleted failed: " + ex);
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Only act on messages from our own UI. Should navigation ever land a foreign
        // page in the WebView2, its messages are dropped here rather than driving disk I/O.
        if (!IsAppOrigin(e.Source))
            return;

        JsonElement msg;
        try
        {
            msg = JsonSerializer.Deserialize<JsonElement>(e.TryGetWebMessageAsString() ?? "");
        }
        catch (JsonException)
        {
            return; // malformed payload — ignore rather than crash the process
        }

        if (msg.ValueKind != JsonValueKind.Object ||
            !msg.TryGetProperty("type", out var typeProp) ||
            typeProp.ValueKind != JsonValueKind.String)
        {
            return;
        }

        // async void: any handler failure logs rather than crashing the process.
        try
        {
            switch (typeProp.GetString())
            {
                case "open":
                    {
                        var dlg = new OpenFileDialog
                        {
                            Filter = "Markdown (*.md;*.markdown)|*.md;*.markdown|All files (*.*)|*.*",
                            Multiselect = true // each selected file opens into its own tab
                        };
                        if (dlg.ShowDialog() == true)
                        {
                            foreach (var file in dlg.FileNames)
                                await OpenFileIntoEditor(file);
                        }
                        break;
                    }

                case "save":
                    {
                        int tabId = GetTabId(msg);
                        string path = GetString(msg, "path");
                        string content = GetContent(msg);
                        if (string.IsNullOrEmpty(path))
                            SaveAs(tabId, path, GetString(msg, "name"), content);
                        else
                            await SaveToPath(tabId, path, content);
                        break;
                    }

                case "saveAs":
                    SaveAs(GetTabId(msg), GetString(msg, "path"), GetString(msg, "name"), GetContent(msg));
                    break;

                case "dirty":
                    _isAnyDirty = msg.TryGetProperty("value", out var dv) && dv.ValueKind == JsonValueKind.True;
                    break;

                case "readyToClose":
                    // JS has saved every dirty tab (the close handshake) — let the window close.
                    _forceClose = true;
                    Close();
                    break;

                case "sessionSnapshot":
                    // JS mirrors its whole tab model (order, active tab, per-tab content + dirty
                    // flag) here whenever it changes; persist it so the next launch can restore
                    // the tabs and recover any unsaved edits after a crash. Fire-and-forget.
                    SaveSession(msg);
                    break;

                case "tabClosed":
                    // A tab was closed in JS; forget its cached encoding/newline/timestamp so
                    // _docs doesn't accumulate stale entries over a long session. Reopening the
                    // file re-detects them in OpenFileIntoEditor.
                    {
                        string closedPath = GetString(msg, "path");
                        if (!string.IsNullOrEmpty(closedPath))
                            _docs.Remove(closedPath);
                    }
                    break;

                case "exportHtml":
                    // JS built the standalone HTML; write it to a user-chosen .html file.
                    ExportHtml(GetString(msg, "name"), GetString(msg, "html"));
                    break;

                case "exportPdf":
                    // Render the JS-built HTML in an offscreen WebView2 and print it to a PDF.
                    await ExportPdf(GetString(msg, "name"), GetString(msg, "html"));
                    break;

                case "theme":
                    // The web UI picked a (light/dark) theme; match the native window title bar so
                    // the OS caption isn't a light strip above a dark editor (and vice versa).
                    SetTitleBarDark(msg.TryGetProperty("dark", out var td) && td.ValueKind == JsonValueKind.True);
                    break;

                case "openInBrowser":
                    {
                        string markdown = msg.TryGetProperty("markdown", out var mdp) && mdp.ValueKind == JsonValueKind.String
                            ? mdp.GetString() ?? ""
                            : "";
                        // The active tab's name/path, so the browser hand-off shows the right file
                        // (C# no longer tracks a single "open" document).
                        string name = GetString(msg, "name");
                        string path = GetString(msg, "path");
                        // Optional: the specific browser the user picked from the dropdown. We map
                        // the Id back to a path from our own discovered list (never trust a path
                        // from JS); an unknown/empty Id falls through to the auto-pick.
                        string browserId = msg.TryGetProperty("browserId", out var bid) && bid.ValueKind == JsonValueKind.String
                            ? bid.GetString() ?? ""
                            : "";
                        string? browserPath = string.IsNullOrEmpty(browserId)
                            ? null
                            : _browsers.Find(b => b.Id == browserId).Path;
                        OpenInBrowserEditor(markdown, name, path, browserPath);
                        break;
                    }
            }
        }
        catch (Exception ex)
        {
            Log.Write("OnWebMessageReceived failed: " + ex);
            await ReportError("Something went wrong. See the log in %LOCALAPPDATA%\\EdMd\\logs.", ex);
        }
    }

    // Open the *full* editor (toolbar, themes, real open/save) in Chrome, pre-loaded with
    // the current document. We serve wwwroot over http://localhost — a secure context, so
    // the browser build can use the File System Access API — and pass the document via the
    // server's /__session route (see the ?session=1 branch in index.html).
    private void OpenInBrowserEditor(string markdown, string name, string path, string? browserPath)
    {
        if (_browserServer == null)
        {
            string wwwroot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
            _browserServer = new LocalWebServer(wwwroot);
            _browserServer.Start();
        }

        _browserServer.SessionName = string.IsNullOrEmpty(name) ? "untitled.md" : name;
        _browserServer.SessionContent = markdown;
        _browserServer.SessionPath = path;
        // Fresh nonce each hand-off; /__session only answers requests carrying it.
        string token = Guid.NewGuid().ToString("N");
        _browserServer.SessionToken = token;

        OpenUrlInBrowser($"http://localhost:{_browserServer.Port}/index.html?session=1&token={token}", browserPath);
    }

    // The editor's open/save needs the File System Access API, which only Chromium browsers
    // provide. So prefer a Chromium browser (Chrome, then Edge — always on Win10/11 — then
    // Brave/Opera/Vivaldi). Only if none is found do we hand the URL to the OS default
    // browser, which may be non-Chromium (editor renders, but open/save are disabled there).
    private static void OpenUrlInBrowser(string url, string? browserPath)
    {
        // Use the browser the user picked, if any; otherwise auto-pick the preferred Chromium.
        string? browser = browserPath ?? FindChromiumBrowser();
        try
        {
            if (browser != null)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = browser,
                    Arguments = "\"" + url + "\"",
                    UseShellExecute = false
                });
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true // default browser via shell association
                });
            }
        }
        catch { /* a failed launch must not crash the editor */ }
    }

    // The Chromium browsers we know how to launch, in preference order (Chrome wins, then
    // Edge — always on Win10/11 — then the rest). Id is the exe stem, used as the stable
    // handle the UI sends back when the user picks one from the dropdown.
    private static readonly (string Exe, string Name)[] KnownBrowsers =
    {
        ("chrome.exe",  "Google Chrome"),
        ("msedge.exe",  "Microsoft Edge"),
        ("brave.exe",   "Brave"),
        ("opera.exe",   "Opera"),
        ("vivaldi.exe", "Vivaldi"),
    };

    // The first installed browser in preference order — the auto-pick when the user doesn't
    // choose one. Null only if no Chromium browser is found (then we fall back to the shell).
    private static string? FindChromiumBrowser()
    {
        var found = DiscoverBrowsers();
        return found.Count > 0 ? found[0].Path : null;
    }

    // Every KnownBrowser actually installed on this machine, in preference order.
    private static List<(string Id, string Name, string Path)> DiscoverBrowsers()
    {
        var found = new List<(string, string, string)>();
        foreach (var (exe, name) in KnownBrowsers)
        {
            string? path = ResolveBrowserPath(exe);
            if (path != null)
                found.Add((Path.GetFileNameWithoutExtension(exe), name, path));
        }
        return found;
    }

    // Locate one browser exe: App Paths (the canonical per-machine/per-user "where is X.exe"
    // lookup) first, then well-known install locations as a backstop.
    private static string? ResolveBrowserPath(string exe)
    {
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            using var key = root.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
            if (key?.GetValue(null) is string p && File.Exists(p))
                return p;
        }

        foreach (var p in WellKnownBrowserPaths(exe))
            if (File.Exists(p)) return p;

        return null;
    }

    private static IEnumerable<string> WellKnownBrowserPaths(string exe)
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        switch (exe)
        {
            case "chrome.exe":
                yield return Path.Combine(pf,   "Google", "Chrome", "Application", "chrome.exe");
                yield return Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe");
                yield return Path.Combine(lad,  "Google", "Chrome", "Application", "chrome.exe");
                break;
            case "msedge.exe":
                yield return Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe");
                yield return Path.Combine(pf,   "Microsoft", "Edge", "Application", "msedge.exe");
                break;
            case "brave.exe":
                yield return Path.Combine(pf,   "BraveSoftware", "Brave-Browser", "Application", "brave.exe");
                yield return Path.Combine(pf86, "BraveSoftware", "Brave-Browser", "Application", "brave.exe");
                break;
            case "opera.exe":
                yield return Path.Combine(lad, "Programs", "Opera", "opera.exe");
                break;
            case "vivaldi.exe":
                yield return Path.Combine(lad, "Vivaldi", "Application", "vivaldi.exe");
                break;
        }
    }

    private static string GetContent(JsonElement msg) => GetString(msg, "content");

    private static string GetString(JsonElement msg, string name) =>
        msg.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    // The JS-owned tab id, echoed back on the save result so JS can route it to the right tab.
    private static int GetTabId(JsonElement msg) =>
        msg.TryGetProperty("tabId", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    // Save a tab's content under a newly-chosen path. `sourcePath` is the tab's current path (empty
    // for a never-saved tab); we copy its encoding/newline from _docs so a Save As round-trips the
    // original file's format. Reports the outcome to the tab via `saved` (success) or `saveResult`
    // (cancel/failure) so the JS close handshake knows whether the save happened.
    private void SaveAs(int tabId, string sourcePath, string suggestedName, string content)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Markdown (*.md;*.markdown)|*.md;*.markdown|All files (*.*)|*.*",
            FileName = string.IsNullOrEmpty(suggestedName) ? "untitled.md" : suggestedName
        };
        if (dlg.ShowDialog() != true)
        {
            _ = PostSaveResult(tabId, false);
            return;
        }

        var meta = MetaFor(string.IsNullOrEmpty(sourcePath) ? null : sourcePath);
        try
        {
            AtomicFile.WriteAllText(dlg.FileName, AtomicFile.NormalizeNewlines(content, meta.Newline), meta.Encoding);
        }
        catch (Exception ex)
        {
            _ = ReportError($"Couldn't save {Path.GetFileName(dlg.FileName)}: {ex.Message}", ex);
            _ = PostSaveResult(tabId, false);
            return;
        }

        // Save As gives the tab a new path; drop the old path's cached meta so _docs doesn't
        // accumulate an orphaned entry per rename (the tab no longer references sourcePath).
        if (!string.IsNullOrEmpty(sourcePath) &&
            !string.Equals(sourcePath, dlg.FileName, StringComparison.OrdinalIgnoreCase))
            _docs.Remove(sourcePath);

        _docs[dlg.FileName] = meta with { WriteTimeUtc = TryGetWriteTimeUtc(dlg.FileName) };
        Title = $"{Path.GetFileName(dlg.FileName)} — EdMd";
        _ = PostToJs(new { type = "saved", tabId, name = Path.GetFileName(dlg.FileName), path = dlg.FileName });
    }

    private async System.Threading.Tasks.Task SaveToPath(int tabId, string path, string content)
    {
        var meta = MetaFor(path);

        // Guard against clobbering an external edit: if the file's on-disk timestamp changed
        // since we last read/wrote it, another app has modified it. Ask before overwriting.
        // (A file that vanished — current == null — has nothing to lose, so we just recreate it.)
        if (meta.WriteTimeUtc is DateTime known &&
            TryGetWriteTimeUtc(path) is DateTime current && current != known)
        {
            var choice = MessageBox.Show(this,
                $"\"{Path.GetFileName(path)}\" has been changed on disk by another program " +
                "since you opened it.\n\n" +
                "Yes — overwrite it with your version\n" +
                "No — save your version under a new name\n" +
                "Cancel — keep editing without saving",
                "EdMd — file changed on disk", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

            if (choice == MessageBoxResult.Cancel)
            {
                await PostSaveResult(tabId, false);
                return;
            }
            if (choice == MessageBoxResult.No)
            {
                SaveAs(tabId, path, Path.GetFileName(path), content);
                return;
            }
            // Yes — fall through and overwrite the external changes.
        }

        try
        {
            AtomicFile.WriteAllText(path, AtomicFile.NormalizeNewlines(content, meta.Newline), meta.Encoding);
        }
        catch (Exception ex)
        {
            await ReportError($"Couldn't save {Path.GetFileName(path)}: {ex.Message}", ex);
            await PostSaveResult(tabId, false);
            return;
        }

        _docs[path] = meta with { WriteTimeUtc = TryGetWriteTimeUtc(path) };
        await PostToJs(new { type = "saved", tabId, name = Path.GetFileName(path), path });
    }

    // Tell JS a save request did NOT write (cancelled dialog or failed write). Success is reported
    // via `saved` instead. The close handshake awaits one of these two per requested save.
    private System.Threading.Tasks.Task PostSaveResult(int tabId, bool ok) =>
        PostToJs(new { type = "saveResult", tabId, ok });

    // The file's current last-write time in UTC, or null if it's missing/unreadable. Used as the
    // baseline for detecting an external edit before an overwrite.
    private static DateTime? TryGetWriteTimeUtc(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null; }
        catch { return null; }
    }

    // Match the native window title bar to the web UI's light/dark theme via the DWM immersive
    // dark-mode attribute. The attribute id changed at Windows 10 20H1 (20 vs. the older 19), so
    // fall back to the legacy id; on older/unsupported builds the call just no-ops.
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void SetTitleBarDark(bool dark)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            int value = dark ? 1 : 0;
            if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref value, sizeof(int));
        }
        catch (Exception ex)
        {
            Log.Write("SetTitleBarDark failed: " + ex);
        }
    }

    // Called (on the UI thread) by the single-instance pipe server when another launch forwards
    // a file. Opens it now if the page is ready, else queues it for OnNavigationCompleted to drain.
    // JS de-dupes by path, so forwarding an already-open file just focuses its existing tab.
    public void EnqueueOpenFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (_webReady)
            _ = OpenFileIntoEditor(path);
        else
            _pendingOpenPaths.Add(path);
    }

    // Surface the existing window when a second instance hands off (or the user relaunches EdMd):
    // un-minimize, activate, and a brief top-most toggle to pull it above the foreground app.
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private async System.Threading.Tasks.Task OpenFileIntoEditor(string path)
    {
        string content;
        try
        {
            content = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex)
        {
            await ReportError($"Couldn't open {Path.GetFileName(path)}: {ex.Message}", ex);
            return;
        }

        // Detect and remember the file's encoding so a later save round-trips it (BOM and all).
        Encoding encoding;
        try { encoding = AtomicFile.DetectEncoding(path); }
        catch { encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false); }
        // Same for its line endings — the editor works in '\n', so remember CRLF vs LF.
        string newline = AtomicFile.DetectNewline(content);

        _docs[path] = new DocMeta(encoding, newline, TryGetWriteTimeUtc(path));
        Title = $"{Path.GetFileName(path)} — EdMd";
        await PostToJs(new { type = "fileOpened", name = Path.GetFileName(path), content, path });
    }

    // A default export file name from the document's name (its extension swapped for .html/.pdf);
    // untitled tabs fall back to "document".
    private static string SuggestExportName(string sourceName, string ext)
    {
        string stem = string.IsNullOrWhiteSpace(sourceName)
            ? "document"
            : Path.GetFileNameWithoutExtension(sourceName);
        if (string.IsNullOrWhiteSpace(stem)) stem = "document";
        return stem + ext;
    }

    // Write the JS-built standalone HTML document to a user-chosen .html file. UTF-8 (no BOM) —
    // the document is self-contained (its CSS is inlined), so it opens/prints anywhere.
    private void ExportHtml(string sourceName, string html)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "HTML (*.html)|*.html|All files (*.*)|*.*",
            FileName = SuggestExportName(sourceName, ".html")
        };
        if (dlg.ShowDialog() != true)
            return;
        try
        {
            AtomicFile.WriteAllText(dlg.FileName, html);
            _ = PostToJs(new { type = "exported", name = Path.GetFileName(dlg.FileName) });
        }
        catch (Exception ex)
        {
            _ = ReportError($"Couldn't export {Path.GetFileName(dlg.FileName)}: {ex.Message}", ex);
        }
    }

    // Export the document to PDF. The main WebView2 hosts the editor UI (toolbar, tabs), so we
    // can't print it directly; instead render the JS-built standalone HTML in a throwaway
    // *offscreen* WebView2 (script disabled — it's a static, possibly-untrusted document) and
    // print that to PDF. The HTML goes via a temp file (deleted afterwards) so the offscreen page
    // has a real file origin to resolve any data:/blob: images against.
    private async System.Threading.Tasks.Task ExportPdf(string sourceName, string html)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "PDF (*.pdf)|*.pdf|All files (*.*)|*.*",
            FileName = SuggestExportName(sourceName, ".pdf")
        };
        if (dlg.ShowDialog() != true)
            return;
        string pdfPath = dlg.FileName;

        string tempHtml = Path.Combine(Path.GetTempPath(), "edmd-export-" + Guid.NewGuid().ToString("N") + ".html");
        CoreWebView2Controller? controller = null;
        try
        {
            await File.WriteAllTextAsync(tempHtml, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
            controller = await Browser.CoreWebView2.Environment.CreateCoreWebView2ControllerAsync(hwnd);
            controller.IsVisible = false;
            // Give it a page-sized viewport so layout/wrapping is sane before we print.
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 816, 1056);

            var wv = controller.CoreWebView2;
            wv.Settings.IsScriptEnabled = false;      // static document — never run script from it
            wv.Settings.AreDefaultContextMenusEnabled = false;

            var loaded = new System.Threading.Tasks.TaskCompletionSource<bool>();
            void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs ev)
            {
                wv.NavigationCompleted -= OnNav;
                loaded.TrySetResult(ev.IsSuccess);
            }
            wv.NavigationCompleted += OnNav;
            wv.Navigate(new Uri(tempHtml).AbsoluteUri);
            if (!await loaded.Task)
            {
                await ReportError("Couldn't export PDF: the document failed to render.",
                    new Exception("offscreen navigation failed"));
                return;
            }

            if (await wv.PrintToPdfAsync(pdfPath, null))
                await PostToJs(new { type = "exported", name = Path.GetFileName(pdfPath) });
            else
                await ReportError("Couldn't export PDF.", new Exception("PrintToPdfAsync returned false"));
        }
        catch (Exception ex)
        {
            await ReportError($"Couldn't export {Path.GetFileName(pdfPath)}: {ex.Message}", ex);
        }
        finally
        {
            controller?.Close();
            try { if (File.Exists(tempHtml)) File.Delete(tempHtml); } catch { /* best-effort temp cleanup */ }
        }
    }

    // Persist the JS-supplied session snapshot to session.json (atomic write). C# stays stateless
    // about tabs: it just serialises what JS mirrored (order, active index, per-tab content/dirty)
    // and never inspects it beyond that. Best-effort — a write failure logs but must not disrupt
    // editing, so this is fire-and-forget.
    private void SaveSession(JsonElement msg)
    {
        try
        {
            int activeIndex = msg.TryGetProperty("activeIndex", out var ai) && ai.ValueKind == JsonValueKind.Number
                ? ai.GetInt32() : 0;
            var list = new List<SessionStore.Tab>();
            if (msg.TryGetProperty("tabs", out var tabsEl) && tabsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tabsEl.EnumerateArray())
                {
                    list.Add(new SessionStore.Tab(
                        GetString(t, "name"),
                        GetString(t, "path"),
                        t.TryGetProperty("dirty", out var d) && d.ValueKind == JsonValueKind.True,
                        GetString(t, "content")));
                }
            }

            string json = SessionStore.Serialize(new SessionStore.Data(activeIndex, list));
            Directory.CreateDirectory(Path.GetDirectoryName(SessionStore.DefaultPath)!);
            AtomicFile.WriteAllText(SessionStore.DefaultPath, json, DefaultMeta.Encoding);
        }
        catch (Exception ex)
        {
            Log.Write("SaveSession failed: " + ex);
        }
    }

    // Rebuild the previous session's tabs on startup and post them to JS as one `restoreSession`
    // batch. Saved files are re-read from disk so their DocMeta (encoding/newline/timestamp) is
    // repopulated — a later save round-trips the file and the external-change guard has a baseline.
    // Rules per tab:
    //   untitled  → restore its buffer (skip a pristine, never-edited blank).
    //   saved+clean→ show fresh disk content; drop the tab if the file is gone/unreadable.
    //   saved+dirty→ keep the recovered buffer (crash recovery), meta read from disk if it exists.
    private async System.Threading.Tasks.Task RestoreSession()
    {
        var outTabs = new List<object>();
        int activeIndex = 0;
        try
        {
            if (File.Exists(SessionStore.DefaultPath))
            {
                var data = SessionStore.Parse(await File.ReadAllTextAsync(SessionStore.DefaultPath));
                if (data != null && data.Tabs.Count > 0)
                {
                    foreach (var t in data.Tabs)
                        await RestoreOneTab(t, outTabs);
                    if (outTabs.Count > 0)
                        activeIndex = Math.Clamp(data.ActiveIndex, 0, outTabs.Count - 1);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("RestoreSession failed: " + ex);
            outTabs.Clear();
            activeIndex = 0;
        }

        // Always tell JS restore is finished — even with an empty list — so it stops holding back
        // the crash-recovery snapshots it suppresses until this message arrives (see app.js).
        await PostToJs(new { type = "restoreSession", activeIndex, tabs = outTabs });
    }

    // Reconstruct one persisted tab into `outTabs` (a `restoreSession` descriptor), repopulating
    // DocMeta from disk for saved files. See RestoreSession for the per-category rules.
    private async System.Threading.Tasks.Task RestoreOneTab(SessionStore.Tab t, List<object> outTabs)
    {
        if (string.IsNullOrEmpty(t.Path))
        {
            // Untitled — only worth restoring if it actually held work.
            if (t.Dirty || !string.IsNullOrEmpty(t.Content))
                outTabs.Add(new { name = t.Name, path = "", content = t.Content, dirty = t.Dirty });
            return;
        }

        if (File.Exists(t.Path))
        {
            try
            {
                string disk = await File.ReadAllTextAsync(t.Path);
                Encoding encoding;
                try { encoding = AtomicFile.DetectEncoding(t.Path); }
                catch { encoding = DefaultMeta.Encoding; }
                _docs[t.Path] = new DocMeta(encoding, AtomicFile.DetectNewline(disk), TryGetWriteTimeUtc(t.Path));
                // Clean tab shows the current file; a dirty tab keeps its recovered edits.
                string content = t.Dirty ? t.Content : disk;
                outTabs.Add(new { name = t.Name, path = t.Path, content, dirty = t.Dirty });
            }
            catch (Exception ex)
            {
                Log.Write("RestoreSession couldn't read " + t.Path + ": " + ex);
                // Preserve unsaved edits even if the file can't be read; drop clean tabs.
                if (t.Dirty)
                    outTabs.Add(new { name = t.Name, path = t.Path, content = t.Content, dirty = true });
            }
        }
        else if (t.Dirty)
        {
            // File is gone but there are unsaved edits — recover them (the path is kept so a save
            // re-targets the original location; if its folder is gone the save reports the error).
            outTabs.Add(new { name = t.Name, path = t.Path, content = t.Content, dirty = true });
        }
        // saved + clean + missing → nothing to lose, drop it.
    }

    // Log the detail, show the user a short message via the footer status line.
    private async System.Threading.Tasks.Task ReportError(string message, Exception ex)
    {
        Log.Write(message + " :: " + ex);
        await PostToJs(new { type = "error", message });
    }

    private async System.Threading.Tasks.Task PostToJs(object payload)
    {
        string json = JsonSerializer.Serialize(payload);
        Browser.CoreWebView2.PostWebMessageAsJson(json);
    }
}
