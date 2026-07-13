using System.Collections.Generic;
using EdMd;
using Xunit;

namespace EdMd.Tests;

// The pure (de)serialization behind session restore / crash recovery. MainWindow owns the disk
// read/write and the per-file reload; this covers the format round-trip and the tolerance a
// corrupt session file must have so a bad file never crashes the launch (it just means "no
// restore"). The tab/document rebuild itself is GUI/bridge code and isn't unit-tested.
public class SessionStoreTests
{
    [Fact]
    public void Round_trips_tabs_and_active_index()
    {
        var data = new SessionStore.Data(1, new List<SessionStore.Tab>
        {
            new("a.md", @"C:\docs\a.md", false, "# A"),
            new("untitled", "", true, "draft with \"quotes\", \n newlines, and émoji 🎉"),
        });

        var restored = SessionStore.Parse(SessionStore.Serialize(data));

        Assert.NotNull(restored);
        Assert.Equal(1, restored!.ActiveIndex);
        Assert.Equal(2, restored.Tabs.Count);
        Assert.Equal("a.md", restored.Tabs[0].Name);
        Assert.Equal(@"C:\docs\a.md", restored.Tabs[0].Path);
        Assert.False(restored.Tabs[0].Dirty);
        Assert.Equal("# A", restored.Tabs[0].Content);
        Assert.True(restored.Tabs[1].Dirty);
        Assert.Equal("draft with \"quotes\", \n newlines, and émoji 🎉", restored.Tabs[1].Content);
    }

    [Fact]
    public void Serialize_of_empty_session_round_trips_to_no_tabs()
    {
        var restored = SessionStore.Parse(
            SessionStore.Serialize(new SessionStore.Data(0, new List<SessionStore.Tab>())));

        Assert.NotNull(restored);
        Assert.Empty(restored!.Tabs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]        // wrong shape (array, not the object)
    [InlineData("\"just a string\"")]
    public void Parse_returns_null_on_empty_or_malformed(string? json) =>
        Assert.Null(SessionStore.Parse(json));

    [Fact]
    public void Parse_tolerates_a_missing_tabs_array()
    {
        // A payload with no "Tabs" property must not NRE downstream — it normalises to empty.
        var restored = SessionStore.Parse("{\"ActiveIndex\":0}");

        Assert.NotNull(restored);
        Assert.Empty(restored!.Tabs);
    }
}
