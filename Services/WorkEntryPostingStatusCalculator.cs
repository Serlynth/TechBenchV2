using TechBench.Models;

namespace TechBench.Services;

public static class WorkEntryPostingStatusCalculator
{
    public static void Update(WorkEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (!string.IsNullOrWhiteSpace(entry.DisplayLastError))
        {
            entry.PostingStatus = PostingStatus.Failed;
            return;
        }

        entry.PostingStatus = (entry.WhdPosted, entry.SagePosted) switch
        {
            (true, true) => PostingStatus.PostedToBoth,
            (true, false) => PostingStatus.PostedToWhd,
            (false, true) => PostingStatus.PostedToSage,
            _ when entry.DurationMinutes > 0
                && (entry.ClientId is > 0
                    || !string.IsNullOrWhiteSpace(entry.ManualClientName))
                && !string.IsNullOrWhiteSpace(entry.Note) => PostingStatus.Ready,
            _ => PostingStatus.Draft
        };
    }
}
