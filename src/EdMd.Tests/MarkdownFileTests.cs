using EdMd;
using Xunit;

namespace EdMd.Tests;

// The extension filter that decides which command-line args EdMd opens, and which a second
// instance forwards to the running window (App.ForwardToRunningInstance /
// MainWindow.OnNavigationCompleted). Anything not matching here is ignored, so it's the gate
// that keeps a stray non-markdown arg from being opened/forwarded.
public class MarkdownFileTests
{
    [Theory]
    [InlineData("note.md")]
    [InlineData("note.markdown")]
    [InlineData(@"C:\docs\My File.md")]
    [InlineData("README.MD")]              // case-insensitive
    [InlineData("readme.MarkDown")]
    public void Accepts_markdown_extensions(string path) =>
        Assert.True(MarkdownFile.HasMarkdownExtension(path));

    [Theory]
    [InlineData("note.txt")]
    [InlineData("note.mdx")]               // .md must be the final extension, not a prefix
    [InlineData("archive.markdown.zip")]
    [InlineData("md")]                     // no dot
    [InlineData("mynote_md")]
    [InlineData(@"C:\Program Files\EdMd\EdMd.exe")] // argv[0]: the exe itself
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_non_markdown(string? path) =>
        Assert.False(MarkdownFile.HasMarkdownExtension(path));
}
