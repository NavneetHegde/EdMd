using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace EdMd;

public partial class App : Application
{
    // Single-instance plumbing: the first EdMd owns the mutex and runs a named-pipe server;
    // any later launch (double-click a .md, "Open with", or a second icon click) finds the
    // mutex already held, forwards its file arguments over the pipe to the running window —
    // which opens them as new tabs and comes to front — then exits. This is what keeps every
    // file in ONE app instead of spawning a window per file.
    private const string InstanceMutexName = @"Local\EdMd.SingleInstance.Mutex";
    private const string InstancePipeName = "EdMd.SingleInstance.Pipe";

    private Mutex? _instanceMutex;
    private bool _ownsInstance;
    private CancellationTokenSource? _pipeCts;

    public App()
    {
        // Catch-all so an unexpected error logs and shows a message instead of vanishing
        // to the desktop. The generated Main calls InitializeComponent() after this ctor.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Write("Unhandled (AppDomain): " + (e.ExceptionObject as Exception)?.ToString());

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Write("Unobserved task exception: " + e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out _ownsInstance);
        if (!_ownsInstance)
        {
            // Another instance is already running — hand it our file args and quit.
            ForwardToRunningInstance(e.Args);
            Shutdown();
            return;
        }

        // Create and register the window BEFORE the pipe server can accept a connection, so an
        // immediate second launch's hand-off always finds MainWindow set (else it's dropped).
        var window = new MainWindow();
        MainWindow = window; // so the pipe server can route forwarded files to it

        _pipeCts = new CancellationTokenSource();
        _ = RunPipeServerAsync(_pipeCts.Token);

        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _pipeCts?.Cancel(); } catch { /* ignore */ }
        if (_ownsInstance)
        {
            try { _instanceMutex?.ReleaseMutex(); } catch { /* ignore */ }
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    // Connect to the first instance's pipe and write our forwarded markdown paths (one per
    // line). Even with no file args we still connect so the running window comes to front.
    private static void ForwardToRunningInstance(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", InstancePipeName, PipeDirection.Out);
            client.Connect(3000); // wait briefly in case the server is still spinning up
            using var writer = new StreamWriter(client) { AutoFlush = true };
            foreach (var arg in args)
            {
                if (MarkdownFile.HasMarkdownExtension(arg) && File.Exists(arg))
                    writer.WriteLine(Path.GetFullPath(arg));
            }
        }
        catch (Exception ex)
        {
            // A failed hand-off must not crash; log and let this process exit. (The file just
            // won't open — a rare race if two instances start at the very same moment.)
            Log.Write("Single-instance forward failed: " + ex);
        }
    }

    // Accept hand-offs from later launches for the life of the process. Each connection carries
    // zero or more file paths; we open them and bring the window forward on the UI thread.
    private async Task RunPipeServerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    InstancePipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                var paths = new List<string>();
                using (var reader = new StreamReader(server))
                {
                    string? line;
                    while ((line = await reader.ReadLineAsync(ct)) != null)
                    {
                        line = line.Trim();
                        if (line.Length > 0)
                            paths.Add(line);
                    }
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    if (MainWindow is MainWindow mw)
                    {
                        foreach (var p in paths)
                            mw.EnqueueOpenFile(p);
                        mw.BringToFront();
                    }
                });
            }
            catch (OperationCanceledException)
            {
                break; // app is shutting down
            }
            catch (Exception ex)
            {
                Log.Write("Single-instance pipe server error: " + ex);
            }
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("Unhandled (dispatcher): " + e.Exception);
        MessageBox.Show(
            "EdMd hit an unexpected error. A log was written to %LOCALAPPDATA%\\EdMd\\logs.",
            "EdMd", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true; // keep the editor alive rather than crash to desktop
    }
}
