using System;
using System.IO;

namespace EdMd;

// Minimal dependency-free file logger — deliberately no NuGet, matching the app's
// "few small files" ethos. Writes to %LOCALAPPDATA%\EdMd\logs\edmd-yyyyMMdd.log so
// crashes and I/O failures leave a trail for support without any UI.
internal static class Log
{
    private static readonly object Gate = new();

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdMd", "logs");

    // Daily log files are never overwritten, so without pruning they'd accumulate forever.
    // Keep a rolling window; older files are deleted once per process on the first Write.
    private const int RetentionDays = 14;
    private static bool _pruned;

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            PruneOnce();
            string file = Path.Combine(Dir, $"edmd-{DateTime.Now:yyyyMMdd}.log");
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}";
            lock (Gate) { File.AppendAllText(file, line); }
        }
        catch { /* logging must never throw */ }
    }

    // Delete log files older than the retention window. Runs at most once per process
    // (best-effort — a failure here must never stop the actual log line being written).
    private static void PruneOnce()
    {
        lock (Gate)
        {
            if (_pruned) return;
            _pruned = true;
        }
        try
        {
            DateTime cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (string f in Directory.EnumerateFiles(Dir, "edmd-*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff)
                {
                    try { File.Delete(f); } catch { /* skip a locked/removed file */ }
                }
            }
        }
        catch { /* pruning is best-effort */ }
    }
}
