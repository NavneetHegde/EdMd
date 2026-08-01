using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using EdMd;
using Xunit;

namespace EdMd.Tests;

public class ImageStoreTests
{
    [Theory]
    [InlineData("image/png", "png")]
    [InlineData("image/jpeg", "jpg")]
    [InlineData("image/gif", "gif")]
    [InlineData("image/webp", "webp")]
    [InlineData("IMAGE/PNG", "png")]   // case-insensitive
    [InlineData("image/png ", "png")]  // trimmed
    public void ExtensionForMime_maps_allowed_types(string mime, string expected)
    {
        Assert.Equal(expected, ImageStore.ExtensionForMime(mime));
    }

    [Theory]
    [InlineData("image/svg+xml")] // SVG excluded in v1 — can carry <script>
    [InlineData("image/bmp")]
    [InlineData("text/plain")]
    [InlineData("")]
    public void ExtensionForMime_rejects_unsupported_types(string mime)
    {
        Assert.Null(ImageStore.ExtensionForMime(mime));
    }

    [Fact]
    public void ExtensionForMime_rejects_null()
    {
        Assert.Null(ImageStore.ExtensionForMime(null));
    }

    [Theory]
    [InlineData("png", true)]
    [InlineData("jpg", true)]
    [InlineData("PNG", true)]
    [InlineData("svg", false)]
    [InlineData("exe", false)]
    [InlineData("", false)]
    public void IsAllowedExtension_gates_the_allowlist(string ext, bool allowed)
    {
        Assert.Equal(allowed, ImageStore.IsAllowedExtension(ext));
    }

    [Fact]
    public void IsAllowedExtension_rejects_null()
    {
        Assert.False(ImageStore.IsAllowedExtension(null));
    }

    [Fact]
    public void BuildFileName_has_the_expected_shape()
    {
        var name = ImageStore.BuildFileName(new byte[] { 1, 2, 3 }, "png", new DateTime(2026, 7, 12, 15, 30, 0, DateTimeKind.Utc));
        Assert.Matches(new Regex(@"^img-20260712-153000-[0-9a-f]{8}\.png$"), name);
    }

    [Fact]
    public void BuildFileName_uses_the_supplied_timestamp()
    {
        var name = ImageStore.BuildFileName(new byte[] { 9 }, "webp", new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        Assert.StartsWith("img-20010203-040506-", name);
        Assert.EndsWith(".webp", name);
    }

    [Fact]
    public void ContentHash_is_stable_for_the_same_bytes()
    {
        var a = ImageStore.ContentHash(Encoding.UTF8.GetBytes("hello world"));
        var b = ImageStore.ContentHash(Encoding.UTF8.GetBytes("hello world"));
        Assert.Equal(a, b);
        Assert.Equal(8, a.Length);
        Assert.Matches(new Regex("^[0-9a-f]{8}$"), a);
    }

    [Fact]
    public void ContentHash_differs_for_different_bytes()
    {
        Assert.NotEqual(
            ImageStore.ContentHash(new byte[] { 1, 2, 3 }),
            ImageStore.ContentHash(new byte[] { 3, 2, 1 }));
    }

    [Fact]
    public void Identical_bytes_dedupe_to_one_stored_file_regardless_of_timestamp()
    {
        // The dedupe fix: the same image pasted at two different times still resolves to one file,
        // because a match is found by content hash (DedupeGlob), not by rebuilding the timestamped
        // name. Two BuildFileName calls a second apart differ, but their glob is identical.
        byte[] bytes = { 4, 5, 6, 7 };
        string hash = ImageStore.ContentHash(bytes);
        var first = ImageStore.BuildFileName(bytes, "png", new DateTime(2026, 7, 12, 15, 30, 0, DateTimeKind.Utc));
        var later = ImageStore.BuildFileName(bytes, "png", new DateTime(2026, 7, 12, 15, 30, 5, DateTimeKind.Utc));

        Assert.NotEqual(first, later); // timestamps differ, so the names differ
        var glob = ImageStore.DedupeGlob(hash, "png");
        Assert.Matches(WildcardToRegex(glob), first);
        Assert.Matches(WildcardToRegex(glob), later); // …but both match the one hash-based glob
    }

    [Fact]
    public void AssetsDirFor_is_a_sibling_assets_folder()
    {
        string doc = Path.Combine("C:", "notes", "readme.md");
        Assert.Equal(Path.Combine("C:", "notes", "assets"), ImageStore.AssetsDirFor(doc));
    }

    [Fact]
    public void RelativeLink_is_forward_slashed_and_scoped_to_assets()
    {
        Assert.Equal("assets/img-20260712-153000-1a2b3c4d.png",
            ImageStore.RelativeLink("img-20260712-153000-1a2b3c4d.png"));
    }

    [Fact]
    public void MimeForExtension_maps_the_allowlisted_types()
    {
        Assert.Equal("image/png", ImageStore.MimeForExtension("png"));
        Assert.Equal("image/jpeg", ImageStore.MimeForExtension("JPG"));
        Assert.Equal("image/gif", ImageStore.MimeForExtension("gif"));
        Assert.Equal("image/webp", ImageStore.MimeForExtension("webp"));
        Assert.Equal("application/octet-stream", ImageStore.MimeForExtension("svg"));
    }

    [Fact]
    public void ResolveAssetRequest_resolves_a_link_inside_the_documents_assets_folder()
    {
        string dir = Path.Combine("C:", "notes");
        Assert.Equal(
            Path.Combine("C:", "notes", "assets", "img-20260712-153000-1a2b3c4d.png"),
            ImageStore.ResolveAssetRequest(dir, "assets/img-20260712-153000-1a2b3c4d.png"));
    }

    [Theory]
    // Anything that escapes the document's own assets/ folder, or isn't an allowlisted image,
    // must be refused — this is the gate that stops the asset origin reading arbitrary files.
    [InlineData("assets/../../secret.png")]
    [InlineData(@"assets/..\..\secret.png")]
    [InlineData("../assets/secret.png")]
    [InlineData("secret.png")]              // next to the doc, but outside assets/
    [InlineData("assets/notes.md")]         // inside assets/, but not an image
    [InlineData("assets/evil.svg")]         // deliberately not in the allowlist
    [InlineData("C:/Windows/win.ini")]      // rooted path: Path.Combine would discard the root
    [InlineData("assets/")]
    [InlineData("")]
    [InlineData(null)]
    public void ResolveAssetRequest_refuses_anything_outside_the_allowlist_or_folder(string? urlPath)
    {
        Assert.Null(ImageStore.ResolveAssetRequest(Path.Combine("C:", "notes"), urlPath));
    }

    // Turn a simple "*-hash.ext" glob into an anchored regex so the test can assert both filenames
    // match the one dedupe pattern.
    private static Regex WildcardToRegex(string glob) =>
        new("^" + Regex.Escape(glob).Replace("\\*", ".*") + "$");
}
