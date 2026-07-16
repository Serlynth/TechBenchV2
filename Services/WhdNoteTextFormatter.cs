using TechBench.Models;

namespace TechBench.Services;

public static class WhdNoteTextFormatter
{
    internal const string InternalNoteHeading = "Internal note (Markdown):";

    public static string BuildWhdNoteText(WorkEntry entry) =>
        BuildWhdNoteText(entry.Note, entry.InternalNote);

    public static string BuildWhdNoteText(string? workNote, string? internalNote)
    {
        var normalizedWorkNote = (workNote ?? string.Empty).Trim();
        var normalizedInternalNote = (internalNote ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedInternalNote))
        {
            return normalizedWorkNote;
        }

        return string.IsNullOrWhiteSpace(normalizedWorkNote)
            ? $"{InternalNoteHeading}{Environment.NewLine}{normalizedInternalNote}"
            : $"{normalizedWorkNote}{Environment.NewLine}{Environment.NewLine}{InternalNoteHeading}{Environment.NewLine}{normalizedInternalNote}";
    }

    public static (string WorkNote, string InternalNote) SplitWhdNoteText(string? noteText)
    {
        var text = (noteText ?? string.Empty).ReplaceLineEndings("\n").Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return (string.Empty, string.Empty);
        }

        var markerIndex = text.IndexOf(InternalNoteHeading, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return (text, string.Empty);
        }

        var workNote = text[..markerIndex].Trim();
        var internalNote = text[(markerIndex + InternalNoteHeading.Length)..].Trim();
        return (workNote, internalNote);
    }
}
