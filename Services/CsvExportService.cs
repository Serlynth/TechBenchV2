using System.Text;
using TechBench.Models;

namespace TechBench.Services;

public static class CsvExportService
{
    public static string BuildWorkEntryCsv(IEnumerable<WorkEntry> entries)
    {
        var builder = new StringBuilder();
        builder.AppendLine("WorkDate,StartTime,EndTime,DurationMinutes,Client,Ticket,Billable,WHDPosted,SagePosted,Status,Tags,FollowUp,FollowUpDueDate,SageWhdNote,PersonalNote");

        foreach (var entry in entries)
        {
            var fields = new[]
            {
                entry.WorkDate.ToString("yyyy-MM-dd"),
                entry.HasTimeRange ? entry.StartTime.ToString(@"hh\:mm") : string.Empty,
                entry.HasTimeRange ? entry.EndTime.ToString(@"hh\:mm") : string.Empty,
                entry.DurationMinutes.ToString(),
                entry.ClientDisplay,
                entry.TicketDisplay,
                entry.Billable ? "Yes" : "No",
                entry.ShowWhdBadge ? entry.WhdPosted ? "Yes" : "No" : "N/A",
                entry.SagePosted ? "Yes" : "No",
                entry.PostingStatusLabel,
                entry.Tags,
                entry.FollowUpLabel,
                entry.FollowUpDueDate?.ToString("yyyy-MM-dd") ?? string.Empty,
                entry.Note,
                entry.InternalNote ?? string.Empty
            };

            builder.AppendLine(string.Join(",", fields.Select(Escape)));
        }

        return builder.ToString();
    }

    private static string Escape(string value)
    {
        var trimmedStart = value.TrimStart();
        if (trimmedStart.Length > 0 && trimmedStart[0] is '=' or '+' or '-' or '@')
        {
            value = $"'{value}";
        }

        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}
