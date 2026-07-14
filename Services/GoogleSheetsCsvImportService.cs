using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TechBench.Models;
using TechBench.ViewModels;

namespace TechBench.Services;

public static class GoogleSheetsCsvImportService
{
    public static IReadOnlyList<WorklogImportRowViewModel> Parse(
        string csv,
        WorklogImportDurationMode durationMode,
        IReadOnlyList<Client> clients,
        IReadOnlyDictionary<string, int> aliases,
        IReadOnlyList<WorkEntry> existingEntries)
    {
        var parsedRows = ParseCsv(csv);
        var result = new List<WorklogImportRowViewModel>();
        DateTime? currentDate = null;
        for (var index = 0; index < parsedRows.Count; index++)
        {
            var fields = parsedRows[index];
            if (fields.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            var nonEmpty = fields.Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray();
            if (nonEmpty.Length == 1 && TryParseDate(nonEmpty[0], out var dateHeader))
            {
                currentDate = dateHeader.Date;
                continue;
            }

            DateTime workDate;
            string clientText;
            string durationText;
            string note;
            if (fields.Count >= 4 && TryParseDate(fields[0], out var rowDate))
            {
                workDate = rowDate.Date;
                clientText = fields[1];
                durationText = fields[2];
                note = JoinNoteFields(fields, 3);
            }
            else
            {
                if (!currentDate.HasValue || fields.Count < 3)
                {
                    continue;
                }

                workDate = currentDate.Value;
                clientText = fields[0];
                durationText = fields[1];
                note = JoinNoteFields(fields, 2);
            }

            if (IsHeaderRow(clientText, durationText, note)
                || string.IsNullOrWhiteSpace(clientText)
                || string.IsNullOrWhiteSpace(note)
                || !TryParseDuration(durationText, out var rawDuration))
            {
                continue;
            }

            var matchedClient = MatchClient(clientText, clients, aliases);
            var durationMinutes = ConvertDuration(rawDuration, durationMode);
            var row = new WorklogImportRowViewModel
            {
                SourceRowNumber = index + 1,
                WorkDate = workDate,
                SourceClient = clientText.Trim(),
                RawDuration = rawDuration,
                SourceDuration = durationText.Trim(),
                Note = note.Trim(),
                SelectedClient = matchedClient,
                DurationMinutes = durationMinutes
            };
            row.IsDuplicate = IsPotentialDuplicate(row, existingEntries);
            row.IsSelected = row.IsValid && !row.IsDuplicate;
            result.Add(row);
        }

        return result;
    }

    public static int ConvertDuration(decimal rawDuration, WorklogImportDurationMode mode)
    {
        var minutes = mode switch
        {
            WorklogImportDurationMode.AllHours => rawDuration * 60m,
            WorklogImportDurationMode.MixedHoursAndMinutes when rawDuration < 10m => rawDuration * 60m,
            _ => rawDuration
        };
        return Math.Max(0, (int)Math.Round(minutes, MidpointRounding.AwayFromZero));
    }

    internal static IReadOnlyList<IReadOnlyList<string>> ParseCsv(string csv)
    {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < csv.Length; index++)
        {
            var current = csv[index];
            if (quoted)
            {
                if (current == '"')
                {
                    if (index + 1 < csv.Length && csv[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(current);
                }

                continue;
            }

            switch (current)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    if (index + 1 < csv.Length && csv[index + 1] == '\n')
                    {
                        index++;
                    }

                    AddRow(rows, row, field);
                    break;
                case '\n':
                    AddRow(rows, row, field);
                    break;
                default:
                    field.Append(current);
                    break;
            }
        }

        if (field.Length > 0 || row.Count > 0)
        {
            AddRow(rows, row, field);
        }

        return rows;
    }

    private static void AddRow(
        ICollection<IReadOnlyList<string>> rows,
        ICollection<string> row,
        StringBuilder field)
    {
        row.Add(field.ToString());
        rows.Add(row.ToArray());
        row.Clear();
        field.Clear();
    }

    private static Client? MatchClient(
        string sourceClient,
        IReadOnlyList<Client> clients,
        IReadOnlyDictionary<string, int> aliases)
    {
        if (aliases.TryGetValue(sourceClient.Trim(), out var aliasClientId))
        {
            return clients.FirstOrDefault(client => client.Id == aliasClientId);
        }

        var sourceKey = Normalize(sourceClient);
        return clients.FirstOrDefault(client => new[]
        {
            client.Name,
            client.WhdLocationName,
            client.SageCustomerName
        }.Where(static value => !string.IsNullOrWhiteSpace(value))
            .Any(value => Normalize(value!) == sourceKey));
    }

    private static bool IsPotentialDuplicate(
        WorklogImportRowViewModel row,
        IReadOnlyList<WorkEntry> existingEntries)
    {
        var note = Normalize(row.Note);
        return existingEntries.Any(existing =>
            existing.WorkDate.Date == row.WorkDate.Date
            && existing.DurationMinutes == row.DurationMinutes
            && Normalize(existing.Note) == note
            && (row.SelectedClient is { Id: > 0 }
                ? existing.ClientId == row.SelectedClient.Id
                : Normalize(existing.ClientDisplay) == Normalize(row.SourceClient)));
    }

    private static string JoinNoteFields(IReadOnlyList<string> fields, int startIndex)
    {
        return string.Join(" ", fields
            .Skip(startIndex)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim()));
    }

    private static bool TryParseDate(string value, out DateTime date)
    {
        return DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out date)
            || DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date);
    }

    private static bool TryParseDuration(string value, out decimal duration)
    {
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out duration)
            || decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out duration);
    }

    private static bool IsHeaderRow(string client, string duration, string note)
    {
        var combined = $"{client} {duration} {note}";
        return combined.Contains("client", StringComparison.OrdinalIgnoreCase)
            && combined.Contains("duration", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value)
    {
        return Regex.Replace(value.Trim().ToUpperInvariant(), @"[^\p{L}\p{N}]+", string.Empty);
    }
}
