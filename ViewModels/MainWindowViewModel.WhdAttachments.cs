using System.Globalization;

namespace TechBench.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool TryGetTrackedWhdNoteId(
        int workEntryId,
        out int techNoteId,
        out string errorMessage)
    {
        var trackingLog = _repository.GetLatestVerifiedWhdPostingLog(workEntryId);
        techNoteId = 0;
        errorMessage = string.Empty;

        if (trackingLog is null
            || !TryParseWhdTechNoteId(trackingLog.ExternalReference, out techNoteId))
        {
            errorMessage = "TechBench does not have the exact WHD TechNote ID for this older or manually marked entry. No replacement note was created; attach the images manually in WHD.";
            return false;
        }

        return true;
    }

    private static bool TryParseWhdTechNoteId(string? externalReference, out int techNoteId)
    {
        const string prefix = "WHD-TECHNOTE-";
        var value = externalReference?.Trim() ?? string.Empty;
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[prefix.Length..];
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out techNoteId)
            && techNoteId > 0;
    }
}
