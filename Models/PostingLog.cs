using System.Globalization;

namespace TechBench.Models;

public sealed class PostingLog
{
    public int Id { get; set; }
    public int WorkEntryId { get; set; }
    public string Destination { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? ExternalReference { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public string ResultLabel => Success ? "Success" : "Failed";
    public string EntryLabel => $"Entry #{WorkEntryId}";
    public string CreatedLabel => CreatedAt.ToString("M/d/yyyy h:mm tt", CultureInfo.InvariantCulture);
    public string ExternalReferenceLabel => string.IsNullOrWhiteSpace(ExternalReference) ? "-" : ExternalReference;

    public string MessagePreview => Preview(Message, 150);
    public string PayloadPreview => Preview(Payload, 220);

    private static string Preview(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(empty)";
        }

        var flattened = value.ReplaceLineEndings(" ").Trim();
        return flattened.Length <= maxLength ? flattened : $"{flattened[..maxLength]}...";
    }
}
