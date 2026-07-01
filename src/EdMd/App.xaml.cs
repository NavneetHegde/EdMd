using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace EdMd;

public partial class App : Application
{
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

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("Unhandled (dispatcher): " + e.Exception);
        MessageBox.Show(
            "EdMd hit an unexpected error. A log was written to %LOCALAPPDATA%\\EdMd\\logs.",
            "EdMd", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true; // keep the editor alive rather than crash to desktop
    }
}
