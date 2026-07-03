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

    private string? _openedFilePath;

    // Encoding of the currently-open file, detected on open and reused on save so we don't
    // silently rewrite a UTF-8-with-BOM or UTF-16 file as UTF-8-no-BOM. Defaults to UTF-8
    // without a BOM for a fresh/new document.
    private Encoding _openedEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    // Line ending of the currently-open file. The editor normalizes to '\n' internally, so we
    // remember the original and restore it on save; defaults to '\n' for a fresh document.
    private string _openedNewline = "\n";

    // Dirty state is mirrored from the JS editor (it owns the document); the C# side needs
    // it to guard the window-close path. _forceClose/_closePending drive the save-then-close
    // handshake (see MainWindow_Closing / HandleSaveResult).
    private bool _isDirty;
    private bool _forceClose;
    private bool _closePending;

    // Lazily started when the user first clicks "Open in Browser"; serves wwwroot over
    // http://localhost so the full editor (with File System Access open/save) runs in Chrome.
    private LocalWebServer? _browserServer;

    // Chromium browsers discovered on this machine, sent to the UI to populate the
    // "Open in Browser" dropdown. The UI hands back an Id from this list (never a path),
    // and we launch the matching Path — so JS can only ever start a browser we found.
    private List<(string Id, string Name, string Path)> _browsers = new();

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

    // Warn about unsaved edits before the window closes. The document lives in JS, so a
    // "Save" answer round-trips: cancel the close, ask JS to save, and close once it reports
    // success (HandleSaveResult). "Don't Save" closes; "Cancel" stays.
    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_forceClose || !_isDirty)
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
        else // Yes — save first, then close when JS round-trips the save
        {
            e.Cancel = true;
            _closePending = true;
            _ = PostToJs(new { type = "requestSaveForClose" });
        }
    }

    // Called after every save attempt. If a close is waiting on the save, finish it only
    // when the save actually succeeded (a cancelled Save As dialog leaves the window open).
    private void HandleSaveResult(bool saved)
    {
        if (!_closePending) return;
        _closePending = false;
        if (saved)
        {
            _forceClose = true;
            Close();
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
            // Handle a file passed on the command line (double-click, or "Open with")
            var args = Environment.GetCommandLineArgs();
            foreach (var arg in args)
            {
                if (arg.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
                    arg.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(arg))
                    {
                        await OpenFileIntoEditor(arg);
                    }
                    break;
                }
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
                            Filter = "Markdown (*.md;*.markdown)|*.md;*.markdown|All files (*.*)|*.*"
                        };
                        if (dlg.ShowDialog() == true)
                        {
                            await OpenFileIntoEditor(dlg.FileName);
                        }
                        break;
                    }

                case "save":
                    {
                        string content = GetContent(msg);
                        bool saved = string.IsNullOrEmpty(_openedFilePath)
                            ? SaveAs(content)
                            : await SaveToPath(_openedFilePath, content);
                        HandleSaveResult(saved);
                        break;
                    }

                case "saveAs":
                    {
                        bool saved = SaveAs(GetContent(msg));
                        HandleSaveResult(saved);
                        break;
                    }

                case "dirty":
                    _isDirty = msg.TryGetProperty("value", out var dv) && dv.ValueKind == JsonValueKind.True;
                    break;

                case "reset":
                    // "New file" in the UI: forget the currently-open path so the next Save
                    // prompts for a new file (Save As) instead of overwriting the last file.
                    _openedFilePath = null;
                    break;

                case "openInBrowser":
                    {
                        string markdown = msg.TryGetProperty("markdown", out var mdp) && mdp.ValueKind == JsonValueKind.String
                            ? mdp.GetString() ?? ""
                            : "";
                        // Optional: the specific browser the user picked from the dropdown. We map
                        // the Id back to a path from our own discovered list (never trust a path
                        // from JS); an unknown/empty Id falls through to the auto-pick.
                        string browserId = msg.TryGetProperty("browserId", out var bid) && bid.ValueKind == JsonValueKind.String
                            ? bid.GetString() ?? ""
                            : "";
                        string? browserPath = string.IsNullOrEmpty(browserId)
                            ? null
                            : _browsers.Find(b => b.Id == browserId).Path;
                        OpenInBrowserEditor(markdown, browserPath);
                        break;
                    }
            }
        }
        catch (Exception ex)
        {
            Log.Write("OnWebMessageReceived failed: " + ex);
            _closePending = false; // don't strand a pending close on an unexpected error
            await ReportError("Something went wrong. See the log in %LOCALAPPDATA%\\EdMd\\logs.", ex);
        }
    }

    // Open the *full* editor (toolbar, themes, real open/save) in Chrome, pre-loaded with
    // the current document. We serve wwwroot over http://localhost — a secure context, so
    // the browser build can use the File System Access API — and pass the document via the
    // server's /__session route (see the ?session=1 branch in index.html).
    private void OpenInBrowserEditor(string markdown, string? browserPath)
    {
        if (_browserServer == null)
        {
            string wwwroot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
            _browserServer = new LocalWebServer(wwwroot);
            _browserServer.Start();
        }

        _browserServer.SessionName = string.IsNullOrEmpty(_openedFilePath)
            ? "untitled.md"
            : Path.GetFileName(_openedFilePath);
        _browserServer.SessionContent = markdown;
        _browserServer.SessionPath = _openedFilePath ?? "";
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

    private static string GetContent(JsonElement msg) =>
        msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() ?? ""
            : "";

    // Returns true only if the file was actually written (false if the user cancelled the
    // dialog or the write failed), so the save-then-close handshake knows whether to close.
    private bool SaveAs(string content)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Markdown (*.md;*.markdown)|*.md;*.markdown|All files (*.*)|*.*",
            FileName = string.IsNullOrEmpty(_openedFilePath) ? "untitled.md" : Path.GetFileName(_openedFilePath)
        };
        if (dlg.ShowDialog() != true)
            return false;

        try
        {
            // Preserve the open document's encoding and line endings when saving under a new name.
            AtomicFile.WriteAllText(dlg.FileName, AtomicFile.NormalizeNewlines(content, _openedNewline), _openedEncoding);
        }
        catch (Exception ex)
        {
            _ = ReportError($"Couldn't save {Path.GetFileName(dlg.FileName)}: {ex.Message}", ex);
            return false;
        }

        _openedFilePath = dlg.FileName;
        Title = $"{Path.GetFileName(dlg.FileName)} — EdMd";
        _ = PostToJs(new { type = "saved", name = Path.GetFileName(dlg.FileName), path = dlg.FileName });
        return true;
    }

    private async System.Threading.Tasks.Task<bool> SaveToPath(string path, string content)
    {
        try
        {
            AtomicFile.WriteAllText(path, AtomicFile.NormalizeNewlines(content, _openedNewline), _openedEncoding);
        }
        catch (Exception ex)
        {
            await ReportError($"Couldn't save {Path.GetFileName(path)}: {ex.Message}", ex);
            return false;
        }

        await PostToJs(new { type = "saved", name = Path.GetFileName(path), path });
        return true;
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
        try { _openedEncoding = AtomicFile.DetectEncoding(path); }
        catch { _openedEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false); }
        // Same for its line endings — the editor works in '\n', so remember CRLF vs LF.
        _openedNewline = AtomicFile.DetectNewline(content);

        _openedFilePath = path;
        Title = $"{Path.GetFileName(path)} — EdMd";
        await PostToJs(new { type = "fileOpened", name = Path.GetFileName(path), content, path });
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
