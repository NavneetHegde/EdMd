using System;
using System.IO;
using System.Linq;
using System.Text;
using EdMd;
using Xunit;

namespace EdMd.Tests;

public class AtomicFileTests : IDisposable
{
    private readonly string _dir;

    public AtomicFileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "EdMdAtomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Fact]
    public void Writes_a_new_file()
    {
        string path = Path.Combine(_dir, "note.md");
        AtomicFile.WriteAllText(path, "hello");
        Assert.Equal("hello", File.ReadAllText(path));
    }

    [Fact]
    public void Overwrites_an_existing_file()
    {
        string path = Path.Combine(_dir, "note.md");
        File.WriteAllText(path, "old contents");
        AtomicFile.WriteAllText(path, "new contents");
        Assert.Equal("new contents", File.ReadAllText(path));
    }

    [Fact]
    public void Leaves_no_temp_files_behind()
    {
        string path = Path.Combine(_dir, "note.md");
        AtomicFile.WriteAllText(path, "one");
        AtomicFile.WriteAllText(path, "two");

        Assert.DoesNotContain(Directory.GetFiles(_dir), f => f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        Assert.Single(Directory.GetFiles(_dir)); // just the target file
    }

    [Fact]
    public void Round_trips_unicode_content()
    {
        string path = Path.Combine(_dir, "unicode.md");
        string content = "# héllo · 世界 🌍\n\n- item";
        AtomicFile.WriteAllText(path, content);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void Default_write_emits_no_bom()
    {
        string path = Path.Combine(_dir, "nobom.md");
        AtomicFile.WriteAllText(path, "hello");
        byte[] bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]); // no UTF-8 BOM prefix
    }

    [Fact]
    public void Detects_utf8_without_bom_as_bomless()
    {
        string path = Path.Combine(_dir, "plain.md");
        File.WriteAllText(path, "hello", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Assert.Empty(AtomicFile.DetectEncoding(path).GetPreamble());
    }

    [Fact]
    public void Detects_and_preserves_a_utf8_bom_across_save()
    {
        string path = Path.Combine(_dir, "bom.md");
        File.WriteAllText(path, "hello", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Encoding enc = AtomicFile.DetectEncoding(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, enc.GetPreamble());

        AtomicFile.WriteAllText(path, "changed", enc);
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]); // BOM survives the save
        Assert.Equal("changed", File.ReadAllText(path));
    }

    [Fact]
    public void Detects_and_preserves_utf16_le_across_save()
    {
        string path = Path.Combine(_dir, "utf16.md");
        File.WriteAllText(path, "hello", Encoding.Unicode); // UTF-16 LE with BOM

        Encoding enc = AtomicFile.DetectEncoding(path);
        Assert.Equal(Encoding.Unicode.GetPreamble(), enc.GetPreamble());

        AtomicFile.WriteAllText(path, "world", enc);
        Assert.Equal(Encoding.Unicode.GetPreamble(), File.ReadAllBytes(path)[..2]);
        Assert.Equal("world", File.ReadAllText(path)); // reader auto-detects the BOM
    }

    [Theory]
    [InlineData("a\r\nb\r\nc", "\r\n")]  // pure CRLF
    [InlineData("a\nb\nc", "\n")]        // pure LF
    [InlineData("a\r\nb\nc", "\r\n")]    // mixed, CRLF-dominant
    [InlineData("a\nb\nc\r\n", "\n")]    // mixed, LF-dominant
    [InlineData("no newlines", "\n")]    // none → LF default
    public void Detects_dominant_newline(string text, string expected)
    {
        Assert.Equal(expected, AtomicFile.DetectNewline(text));
    }

    [Fact]
    public void Normalizes_to_crlf_from_a_mix()
    {
        Assert.Equal("a\r\nb\r\nc", AtomicFile.NormalizeNewlines("a\nb\r\nc", "\r\n"));
    }

    [Fact]
    public void Normalizes_to_lf_from_a_mix()
    {
        Assert.Equal("a\nb\nc", AtomicFile.NormalizeNewlines("a\r\nb\rc", "\n"));
    }

    [Fact]
    public void Preserves_crlf_line_endings_across_a_save()
    {
        // The editor hands back LF; with a remembered CRLF the file stays CRLF on disk.
        string path = Path.Combine(_dir, "crlf.md");
        File.WriteAllText(path, "line1\r\nline2\r\n");

        string fromEditor = "line1\nline2\nline3\n"; // '\n' as the editor would emit
        string newline = AtomicFile.DetectNewline(File.ReadAllText(path));
        AtomicFile.WriteAllText(path, AtomicFile.NormalizeNewlines(fromEditor, newline));

        Assert.Equal("line1\r\nline2\r\nline3\r\n", File.ReadAllText(path));
    }
}
