using System;
using System.IO;
using System.Text;

namespace EdMd;

// File writes for the editor. Kept separate from the WPF window so the logic is unit
// testable (see EdMd.Tests) and reusable.
public static class AtomicFile
{
    // UTF-8 without a BOM: the default for a new document and for any file that was read
    // without one. Matches File.ReadAllText's default and what most markdown tooling emits.
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // Inspect a file's leading bytes and return the encoding to round-trip it with — i.e.
    // one that re-emits the same BOM the file already has (or none). Keeping the encoding
    // stable means saving a UTF-8-with-BOM or UTF-16 file doesn't silently rewrite it as
    // UTF-8-no-BOM. Mirrors the BOM sniffing File.ReadAllText/StreamReader do when reading,
    // so the detected encoding matches how the content was decoded in the first place.
    public static Encoding DetectEncoding(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> b = stackalloc byte[4];
        int n = fs.Read(b);

        // UTF-32 LE (FF FE 00 00) must be tested before UTF-16 LE (FF FE), whose BOM is a prefix.
        if (n >= 4 && b[0] == 0xFF && b[1] == 0xFE && b[2] == 0x00 && b[3] == 0x00)
            return new UTF32Encoding(bigEndian: false, byteOrderMark: true);
        if (n >= 4 && b[0] == 0x00 && b[1] == 0x00 && b[2] == 0xFE && b[3] == 0xFF)
            return new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        if (n >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        if (n >= 2 && b[0] == 0xFF && b[1] == 0xFE)
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        if (n >= 2 && b[0] == 0xFE && b[1] == 0xFF)
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: true);

        return Utf8NoBom;
    }

    // Detect the dominant line ending in text so a save can restore it — the editor works
    // in '\n' internally, so without this a CRLF (Windows) file would be silently rewritten
    // with LF endings. Returns "\r\n" when CRLF is present and at least as common as lone LF,
    // otherwise "\n" (also the default for text with no line breaks at all).
    public static string DetectNewline(string text)
    {
        int crlf = 0, lf = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            if (i > 0 && text[i - 1] == '\r') crlf++;
            else lf++;
        }
        return crlf > 0 && crlf >= lf ? "\r\n" : "\n";
    }

    // Rewrite every line ending in text to newline, collapsing any existing mix first.
    public static string NormalizeNewlines(string text, string newline)
    {
        string lf = text.Replace("\r\n", "\n").Replace("\r", "\n");
        return newline == "\r\n" ? lf.Replace("\n", "\r\n") : lf;
    }

    // Write UTF-8 without a BOM (the default for new documents).
    public static void WriteAllText(string path, string content) =>
        WriteAllText(path, content, Utf8NoBom);

    // Write via a temp file + atomic replace so an interrupted save can't truncate
    // or corrupt the user's existing file. On failure the temp file is cleaned up.
    // The encoding (from DetectEncoding on open) is preserved, BOM and all.
    public static void WriteAllText(string path, string content, Encoding encoding) =>
        AtomicWrite(path, tmp => File.WriteAllText(tmp, content, encoding));

    // Binary sibling of WriteAllText, used for pasted/dropped images. Same temp-file +
    // atomic-replace discipline so a half-written image never appears next to the document.
    public static void WriteAllBytes(string path, byte[] bytes) =>
        AtomicWrite(path, tmp => File.WriteAllBytes(tmp, bytes));

    // Write to a sibling temp file, then atomically replace the target (or move into place
    // if it's new). On any failure the temp file is cleaned up and the error rethrown.
    private static void AtomicWrite(string path, Action<string> writeTmp)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        string tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        writeTmp(tmp);
        try
        {
            if (File.Exists(path))
                File.Replace(tmp, path, null);
            else
                File.Move(tmp, path);
        }
        catch
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            throw;
        }
    }
}
