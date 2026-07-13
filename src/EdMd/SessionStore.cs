using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace EdMd;

// Persistence for "reopen my tabs on next launch" + crash recovery of unsaved edits.
// The JS side owns the tab/document model and periodically posts a snapshot of it; C# writes
// that snapshot to session.json (see MainWindow.SaveSession) and reconstructs it on startup
// (MainWindow.RestoreSession). The (de)serialization is kept here as pure static methods so it
// is unit-testable — the disk read/write and the per-file reload that repopulates DocMeta stay
// in MainWindow, which is the only part that needs the WPF/WebView2 host.
public static class SessionStore
{
    // One persisted tab. Content is the full markdown; for a clean saved file it's a fallback
    // (MainWindow prefers a fresh read from disk on restore), for a dirty/untitled tab it *is*
    // the recovered buffer.
    public sealed record Tab(string Name, string Path, bool Dirty, string Content);

    // The whole persisted session: the open tabs (in strip order) and which one was active.
    public sealed record Data(int ActiveIndex, IReadOnlyList<Tab> Tabs);

    // %LOCALAPPDATA%\EdMd\session.json — beside the logs dir the app already writes to.
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EdMd", "session.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string Serialize(Data data) => JsonSerializer.Serialize(data, Options);

    // Tolerant parse: returns null on any empty/malformed input rather than throwing, so a
    // corrupt or truncated session file just means "nothing to restore" — never a launch crash.
    // A payload missing its tabs array normalises to an empty list.
    public static Data? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var data = JsonSerializer.Deserialize<Data>(json, Options);
            if (data == null)
                return null;
            return data.Tabs == null ? data with { Tabs = new List<Tab>() } : data;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
