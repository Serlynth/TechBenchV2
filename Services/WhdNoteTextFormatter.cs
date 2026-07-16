using TechBench.Models;

namespace TechBench.Services;

public static class WhdNoteTextFormatter
{
    internal const string PersonalNoteHeading = "Personal note (Markdown):";
    private const string LegacyInternalNoteHeading = "Internal note (Markdown):";

    public static string BuildWhdNoteText(WorkEntry entry) =>
        BuildWhdNoteText(
            entry.Note,
            entry.IncludePersonalNoteInWhd ? entry.InternalNote : null);

    public static string BuildWhdNoteText(string? sageWhdNote, string? personalNote)
    {
        var normalizedSageWhdNote = (sageWhdNote ?? string.Empty).Trim();
        var normalizedPersonalNote = (personalNote ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedPersonalNote))
        {
            return normalizedSageWhdNote;
        }

        return string.IsNullOrWhiteSpace(normalizedSageWhdNote)
            ? $"{PersonalNoteHeading}{Environment.NewLine}{normalizedPersonalNote}"
            : $"{normalizedSageWhdNote}{Environment.NewLine}{Environment.NewLine}{PersonalNoteHeading}{Environment.NewLine}{normalizedPersonalNote}";
    }

    public static (string SageWhdNote, string PersonalNote, bool IncludesPersonalNote) SplitWhdNoteText(string? noteText)
    {
        var text = (noteText ?? string.Empty).ReplaceLineEndings("\n").Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return (string.Empty, string.Empty, false);
        }

        var heading = PersonalNoteHeading;
        var markerIndex = text.IndexOf(heading, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            heading = LegacyInternalNoteHeading;
            markerIndex = text.IndexOf(heading, StringComparison.OrdinalIgnoreCase);
        }

        if (markerIndex < 0)
        {
            return (text, string.Empty, false);
        }

        var sageWhdNote = text[..markerIndex].Trim();
        var personalNote = text[(markerIndex + heading.Length)..].Trim();
        return (sageWhdNote, personalNote, true);
    }
}
