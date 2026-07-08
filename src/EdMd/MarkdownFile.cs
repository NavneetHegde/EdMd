using System;

namespace EdMd;

// Tiny pure helper: which paths EdMd treats as openable markdown. Shared by the
// file-association command-line scan (MainWindow.OnNavigationCompleted) and the
// single-instance forwarder (App.ForwardToRunningInstance) so both accept exactly the
// same extensions — keep it here (one definition) rather than duplicating the literals.
public static class MarkdownFile
{
    // True if the path ends in a markdown extension we open (.md / .markdown),
    // case-insensitively. Null/empty is not markdown.
    public static bool HasMarkdownExtension(string? path) =>
        !string.IsNullOrEmpty(path) &&
        (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase));
}
