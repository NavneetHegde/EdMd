using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace EdMd;

// The pure, security-sensitive logic behind image paste / drag-drop: the MIME→extension
// allowlist, the content-addressed filename, and the assets-dir / relative-link helpers.
// Kept out of MainWindow (like AtomicFile / SessionStore) so it's unit-testable and so the
// disk write stays the only impure part. Nothing here touches the filesystem.
public static class ImageStore
{
    // The only image types we persist. Matched from the blob's MIME type, never from a
    // caller-supplied filename. SVG is deliberately absent: it can carry <script>, so writing
    // attacker-controlled SVG into the user's folder is needless risk (revisit with sanitising).
    private static readonly Dictionary<string, string> MimeToExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = "png",
        ["image/jpeg"] = "jpg",
        ["image/gif"] = "gif",
        ["image/webp"] = "webp",
    };

    // The extensions those MIME types map to — used to re-validate the ext JS derived, so a
    // saveImage can never write an out-of-allowlist extension even if JS is compromised.
    private static readonly HashSet<string> AllowedExt =
        new(MimeToExt.Values, StringComparer.OrdinalIgnoreCase);

    // Map a blob MIME type to the file extension we'll store it under, or null if unsupported.
    public static string? ExtensionForMime(string? mime) =>
        mime != null && MimeToExt.TryGetValue(mime.Trim(), out var ext) ? ext : null;

    // True if ext is one of the allowlisted image extensions (case-insensitive, no dot).
    public static bool IsAllowedExtension(string? ext) =>
        ext != null && AllowedExt.Contains(ext);

    // The stored name: img-<yyyyMMdd-HHmmss>-<8 hex of SHA-256(bytes)>.<ext>. The timestamp
    // keeps the folder human-sortable; the content hash de-duplicates (see DedupeGlob) and
    // makes the name impossible to steer toward clobbering an unrelated file.
    public static string BuildFileName(byte[] bytes, string ext, DateTime nowUtc) =>
        $"img-{nowUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{ContentHash(bytes)}.{ext}";

    // First 8 hex chars of SHA-256 over the bytes — the content-address used in the filename.
    public static string ContentHash(byte[] bytes)
    {
        byte[] hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(8);
        for (int i = 0; i < 4; i++) sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }

    // A search pattern matching any already-stored file with this content hash, regardless of
    // its timestamp prefix — so pasting the same image twice reuses one file. (The name embeds a
    // fresh timestamp each paste, so a match must be found by hash, not by rebuilding the name.)
    public static string DedupeGlob(string contentHash, string ext) => $"*-{contentHash}.{ext}";

    // The sibling assets/ folder the image is written into, next to the document.
    public static string AssetsDirFor(string docPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(docPath)) ?? ".", "assets");

    // The relative Markdown link to embed — always forward-slashed (Markdown, not Windows paths)
    // and always inside the document's own directory (no absolute path, no "..").
    public static string RelativeLink(string fileName) => "assets/" + fileName;
}
