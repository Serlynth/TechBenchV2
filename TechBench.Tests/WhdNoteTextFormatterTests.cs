using TechBench.Models;
using TechBench.Services;

namespace TechBench.Tests;

public sealed class WhdNoteTextFormatterTests
{
    [Fact]
    public void BuildsWhdNoteFromSageWhdAndIncludedPersonalMarkdown()
    {
        var entry = new WorkEntry
        {
            Note = "Installed updates.",
            InternalNote = "- Rebooted\n- Verified VPN",
            IncludePersonalNoteInWhd = true
        };

        var whdNote = WhdNoteTextFormatter.BuildWhdNoteText(entry);

        Assert.Equal(
            "Installed updates.\r\n\r\nPersonal note (Markdown):\r\n- Rebooted\n- Verified VPN",
            whdNote);
    }

    [Fact]
    public void OmitsPersonalNoteUnlessEntryOptsIn()
    {
        var entry = new WorkEntry
        {
            Note = "Installed updates.",
            InternalNote = "Private reminder."
        };

        Assert.Equal("Installed updates.", WhdNoteTextFormatter.BuildWhdNoteText(entry));
    }

    [Fact]
    public void SplitsWhdNoteBackIntoSageWhdAndPersonalNotes()
    {
        var split = WhdNoteTextFormatter.SplitWhdNoteText(
            "Installed updates.\n\nPersonal note (Markdown):\n- Rebooted\n- Verified VPN");

        Assert.Equal("Installed updates.", split.SageWhdNote);
        Assert.Equal("- Rebooted\n- Verified VPN", split.PersonalNote);
        Assert.True(split.IncludesPersonalNote);
    }

    [Fact]
    public void SplitsLegacyInternalNoteMarker()
    {
        var split = WhdNoteTextFormatter.SplitWhdNoteText(
            "Installed updates.\n\nInternal note (Markdown):\nLegacy note");

        Assert.Equal("Installed updates.", split.SageWhdNote);
        Assert.Equal("Legacy note", split.PersonalNote);
        Assert.True(split.IncludesPersonalNote);
    }
}
