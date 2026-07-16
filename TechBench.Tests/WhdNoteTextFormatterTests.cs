using TechBench.Models;
using TechBench.Services;

namespace TechBench.Tests;

public sealed class WhdNoteTextFormatterTests
{
    [Fact]
    public void BuildsWhdNoteFromWorkAndInternalMarkdown()
    {
        var entry = new WorkEntry
        {
            Note = "Installed updates.",
            InternalNote = "- Rebooted\n- Verified VPN"
        };

        var whdNote = WhdNoteTextFormatter.BuildWhdNoteText(entry);

        Assert.Equal(
            "Installed updates.\r\n\r\nInternal note (Markdown):\r\n- Rebooted\n- Verified VPN",
            whdNote);
    }

    [Fact]
    public void SplitsWhdNoteBackIntoWorkAndInternalNotes()
    {
        var split = WhdNoteTextFormatter.SplitWhdNoteText(
            "Installed updates.\n\nInternal note (Markdown):\n- Rebooted\n- Verified VPN");

        Assert.Equal("Installed updates.", split.WorkNote);
        Assert.Equal("- Rebooted\n- Verified VPN", split.InternalNote);
    }
}
