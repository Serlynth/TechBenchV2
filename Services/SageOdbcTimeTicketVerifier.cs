using System.Data;
using System.Data.Odbc;
using System.Globalization;
using System.Text.RegularExpressions;

namespace TechBench.Services;

public sealed record SageTimeTicketVerificationRequest(
    string? TicketNumber,
    DateTime TicketDate,
    int DurationMinutes,
    string Note);

public sealed record SageTimeTicketVerificationResult(
    bool IsSaved,
    bool Found,
    string Message,
    string? TicketNumber = null);

public interface ISageTimeTicketVerifier
{
    SageTimeTicketVerificationResult Verify(
        string dsn,
        string username,
        string password,
        SageTimeTicketVerificationRequest request);
}

public sealed class SageOdbcTimeTicketVerifier : ISageTimeTicketVerifier
{
    private const int OdbcCommandTimeoutSeconds = 10;
    private const int OdbcSchemaTimeoutSeconds = 5;
    private static readonly string[] TicketTableNames = ["Ticket", "Tickets", "TICKET.DAT"];
    private static readonly string[] TicketNumberColumns = ["TicketNumber", "Ticket_Number"];
    private static readonly string[] TicketTypeColumns = ["TicketType", "Ticket_Type"];
    private static readonly string[] TicketDateColumns = ["TicketDate", "Ticket_Date"];
    private static readonly string[] DurationHoursColumns = ["Duration_Hours", "DurationHours"];
    private static readonly string[] DurationMinutesColumns = ["Duration_Minutes", "DurationMinutes"];
    private static readonly string[] DurationUnitColumns = ["DurationUnitQty", "Duration_Unit_Qty", "UnitDuration"];
    private static readonly string[] BillTypeColumns = ["BillType", "Bill_Type"];
    private static readonly string[] MemoColumnCandidates = ["CharMemo", "Char_Memo", "InternalMemo", "Internal_Memo"];
    private string? _cachedDsn;
    private IReadOnlyList<TicketTable>? _cachedTables;

    public SageTimeTicketVerificationResult Verify(
        string dsn,
        string username,
        string password,
        SageTimeTicketVerificationRequest request)
    {
        if (string.IsNullOrWhiteSpace(dsn))
        {
            throw new InvalidOperationException("Enter the Sage ODBC DSN in Settings before verifying saved tickets.");
        }

        using var connection = new OdbcConnection(BuildConnectionString(dsn, username, password));
        connection.Open();
        var candidates = GetTicketTables(connection, dsn);
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                "The Sage ODBC connection did not expose a TICKET.DAT-compatible table with TicketNumber, TicketType, and TicketDate fields.");
        }

        if (IsNativeTicketNumber(request.TicketNumber))
        {
            foreach (var candidate in candidates)
            {
                using var command = connection.CreateCommand();
                command.CommandTimeout = OdbcCommandTimeoutSeconds;
                command.CommandText = $"SELECT {BuildSelectList(candidate)} FROM {Quote(candidate.TableName)} WHERE {Quote(candidate.TicketNumberColumn)} = ?";
                command.Parameters.Add("ticketNumber", OdbcType.VarChar, 20).Value = request.TicketNumber!.Trim();

                using var reader = command.ExecuteReader(CommandBehavior.SingleResult);
                while (reader.Read())
                {
                    var result = ValidateExactRow(reader, candidate, request);
                    if (result.Found)
                    {
                        return result;
                    }
                }
            }

            return new SageTimeTicketVerificationResult(
                false,
                false,
                $"Saved Sage ticket #{request.TicketNumber!.Trim()} is not visible through ODBC yet. The entry remains Sage pending.",
                request.TicketNumber.Trim());
        }

        var recovered = FindUniqueMatchingTicket(connection, candidates, request);
        if (recovered is not null)
        {
            return recovered;
        }

        return new SageTimeTicketVerificationResult(
            false,
            false,
            "A matching saved Sage ticket was not found yet. The entry remains Sage pending.");
    }

    private IReadOnlyList<TicketTable> GetTicketTables(OdbcConnection connection, string dsn)
    {
        if (_cachedTables is not null && string.Equals(_cachedDsn, dsn.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return _cachedTables;
        }

        _cachedTables = DiscoverTicketTables(connection);
        _cachedDsn = dsn.Trim();
        return _cachedTables;
    }

    internal static TicketTable? SelectTicketTable(
        string tableName,
        IReadOnlyCollection<string> columns)
    {
        var ticketNumber = FindColumn(columns, TicketNumberColumns);
        var ticketType = FindColumn(columns, TicketTypeColumns);
        var ticketDate = FindColumn(columns, TicketDateColumns);
        var memoColumns = MemoColumnCandidates
            .Select(candidate => columns.FirstOrDefault(column => column.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
            .Where(column => column is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return ticketNumber is null || ticketType is null || ticketDate is null
            ? null
            : new TicketTable(
                tableName,
                ticketNumber,
                ticketType,
                ticketDate,
                FindColumn(columns, DurationHoursColumns),
                FindColumn(columns, DurationMinutesColumns),
                FindColumn(columns, DurationUnitColumns),
                FindColumn(columns, BillTypeColumns),
                memoColumns);
    }

    private static IReadOnlyList<TicketTable> DiscoverTicketTables(OdbcConnection connection)
    {
        foreach (var tableName in TicketTableNames)
        {
            try
            {
                using var command = connection.CreateCommand();
                command.CommandTimeout = OdbcSchemaTimeoutSeconds;
                command.CommandText = $"SELECT * FROM {Quote(tableName)}";
                using var reader = command.ExecuteReader(CommandBehavior.SchemaOnly);
                var columns = Enumerable.Range(0, reader.FieldCount)
                    .Select(reader.GetName)
                    .Where(IsSafeIdentifier)
                    .ToArray();
                var table = SelectTicketTable(tableName, columns);
                if (table is not null)
                {
                    return [table];
                }
            }
            catch (OdbcException)
            {
                // Sage/Zen versions can expose the same TICKET.DAT file under different logical names.
            }
        }

        return [];
    }

    private static SageTimeTicketVerificationResult ValidateExactRow(
        IDataRecord row,
        TicketTable table,
        SageTimeTicketVerificationRequest request)
    {
        var actualNumber = ReadText(row, table.TicketNumberColumn).Trim();
        if (!actualNumber.Equals(request.TicketNumber!.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return new SageTimeTicketVerificationResult(false, false, string.Empty);
        }

        var mismatches = ReadMismatches(row, table, request, compareNote: false);
        return mismatches.Count == 0
            ? new SageTimeTicketVerificationResult(
                true,
                true,
                $"Verified saved Sage time ticket #{actualNumber} by read-only ODBC.",
                actualNumber)
            : new SageTimeTicketVerificationResult(
                false,
                true,
                $"Sage ticket #{actualNumber} exists, but {string.Join("; ", mismatches)}. The entry remains Sage pending.",
                actualNumber);
    }

    private static SageTimeTicketVerificationResult? FindUniqueMatchingTicket(
        OdbcConnection connection,
        IReadOnlyList<TicketTable> tables,
        SageTimeTicketVerificationRequest request)
    {
        var matches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nearMatches = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in tables)
        {
            if (table.MemoColumns.Count == 0 || string.IsNullOrWhiteSpace(request.Note))
            {
                continue;
            }

            try
            {
                QueryTicketDate(connection, table, request, matches, nearMatches);
            }
            catch (OdbcException)
            {
                // The bounded recent-ticket query below does not depend on Zen date coercion.
            }
            if (matches.Count == 0)
            {
                QueryRecentTickets(connection, table, request, matches, nearMatches);
            }
        }

        if (matches.Count == 1)
        {
            return new SageTimeTicketVerificationResult(
                true,
                true,
                $"Recovered and verified saved Sage time ticket #{matches.Keys.Single()} by read-only ODBC.",
                matches.Keys.Single());
        }

        if (matches.Count > 1)
        {
            return new SageTimeTicketVerificationResult(
                false,
                true,
                $"Sage contains {matches.Count} saved tickets matching this entry, so TechBench could not choose one safely. The entry remains Sage pending.");
        }

        var nearest = nearMatches
            .OrderBy(candidate => candidate.Value.Count)
            .ThenByDescending(candidate => candidate.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(nearest.Key)
            ? null
            : new SageTimeTicketVerificationResult(
                false,
                true,
                $"Sage ticket #{nearest.Key} is visible, but {string.Join("; ", nearest.Value)}. The entry remains Sage pending.",
                nearest.Key);
    }

    private static void QueryTicketDate(
        OdbcConnection connection,
        TicketTable table,
        SageTimeTicketVerificationRequest request,
        IDictionary<string, string> matches,
        IDictionary<string, IReadOnlyList<string>> nearMatches)
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = OdbcCommandTimeoutSeconds;
        command.CommandText = $"SELECT {BuildSelectList(table)} FROM {Quote(table.TableName)} WHERE {Quote(table.TicketTypeColumn)} = 0 AND {Quote(table.TicketDateColumn)} = {{d '{request.TicketDate:yyyy-MM-dd}'}}";

        using var reader = command.ExecuteReader(CommandBehavior.SingleResult);
        CollectMatchingRows(reader, table, request, matches, nearMatches, maxRows: 500);
    }

    private static void QueryRecentTickets(
        OdbcConnection connection,
        TicketTable table,
        SageTimeTicketVerificationRequest request,
        IDictionary<string, string> matches,
        IDictionary<string, IReadOnlyList<string>> nearMatches)
    {
        using var command = connection.CreateCommand();
        command.CommandTimeout = OdbcCommandTimeoutSeconds;
        command.CommandText = $"SELECT TOP 100 {BuildSelectList(table)} FROM {Quote(table.TableName)} WHERE {Quote(table.TicketTypeColumn)} = 0 ORDER BY {Quote(table.TicketNumberColumn)} DESC";
        using var reader = command.ExecuteReader(CommandBehavior.SingleResult);
        CollectMatchingRows(reader, table, request, matches, nearMatches, maxRows: 100);
    }

    private static string BuildSelectList(TicketTable table) => string.Join(", ", new[]
        {
            table.TicketNumberColumn,
            table.TicketTypeColumn,
            table.TicketDateColumn,
            table.DurationHoursColumn,
            table.DurationMinutesColumn,
            table.DurationUnitColumn,
            table.BillTypeColumn
        }
        .Concat(table.MemoColumns)
        .Where(column => !string.IsNullOrWhiteSpace(column))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(column => Quote(column!)));

    private static void CollectMatchingRows(
        IDataReader reader,
        TicketTable table,
        SageTimeTicketVerificationRequest request,
        IDictionary<string, string> matches,
        IDictionary<string, IReadOnlyList<string>> nearMatches,
        int maxRows)
    {
        var rowsRead = 0;
        while (rowsRead < maxRows && reader.Read())
        {
            rowsRead++;
            var mismatches = ReadMismatches(reader, table, request, compareNote: true);
            var number = ReadText(reader, table.TicketNumberColumn).Trim();
            if (!IsNativeTicketNumber(number))
            {
                continue;
            }

            if (mismatches.Count == 0)
            {
                matches[number] = table.TableName;
                continue;
            }

            var matchingSignals = 0;
            if (TryReadDate(reader, table.TicketDateColumn, out var ticketDate)
                && ticketDate.Date == request.TicketDate.Date)
            {
                matchingSignals++;
            }

            if (ReadDurationMinutes(reader, table) == request.DurationMinutes)
            {
                matchingSignals++;
            }

            if (MemoCorresponds(reader, table, request.Note))
            {
                matchingSignals++;
            }

            if (matchingSignals >= 2)
            {
                nearMatches[number] = mismatches;
            }
        }
    }

    private static List<string> ReadMismatches(
        IDataRecord row,
        TicketTable table,
        SageTimeTicketVerificationRequest request,
        bool compareNote)
    {
        var mismatches = new List<string>();
        if (!TryReadInt(row, table.TicketTypeColumn, out var ticketType) || ticketType != 0)
        {
            mismatches.Add("record is not a Time Ticket");
        }

        if (!TryReadDate(row, table.TicketDateColumn, out var ticketDate) || ticketDate.Date != request.TicketDate.Date)
        {
            mismatches.Add($"date does not match {request.TicketDate:d}");
        }

        var actualDuration = ReadDurationMinutes(row, table);
        if (!actualDuration.HasValue || actualDuration.Value != request.DurationMinutes)
        {
            mismatches.Add($"duration does not match {request.DurationMinutes} minutes");
        }

        if (table.BillTypeColumn is not null
            && (!TryReadInt(row, table.BillTypeColumn, out var billType) || billType != 2))
        {
            mismatches.Add("billing type is not Activity Rate");
        }

        if (compareNote
            && table.MemoColumns.Count > 0
            && !MemoCorresponds(row, table, request.Note))
        {
            var actualNote = table.MemoColumns
                .Select(column => NormalizeNote(ReadText(row, column)))
                .FirstOrDefault(note => !string.IsNullOrWhiteSpace(note))
                ?? string.Empty;
            var preview = actualNote.Length <= 80 ? actualNote : $"{actualNote[..77]}...";
            mismatches.Add($"work note does not match (ODBC returned '{preview}')");
        }

        return mismatches;
    }

    private static bool MemoCorresponds(IDataRecord row, TicketTable table, string expectedNote) =>
        MemoValuesCorrespond(table.MemoColumns.Select(column => ReadText(row, column)), expectedNote);

    internal static bool MemoValuesCorrespond(IEnumerable<string> memoValues, string expectedNote) =>
        memoValues.Any(value => NotesCorrespond(value, expectedNote));

    private static int? ReadDurationMinutes(IDataRecord row, TicketTable table)
    {
        if (table.DurationHoursColumn is not null
            && table.DurationMinutesColumn is not null
            && TryReadInt(row, table.DurationHoursColumn, out var hours)
            && TryReadInt(row, table.DurationMinutesColumn, out var minutes))
        {
            return checked(hours * 60 + minutes);
        }

        if (table.DurationUnitColumn is not null
            && TryReadDecimal(row, table.DurationUnitColumn, out var unitDuration))
        {
            return (int)Math.Round(unitDuration * 60m, MidpointRounding.AwayFromZero);
        }

        return null;
    }

    private static bool TryReadInt(IDataRecord row, string column, out int value) =>
        int.TryParse(ReadText(row, column), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static bool TryReadDecimal(IDataRecord row, string column, out decimal value) =>
        decimal.TryParse(ReadText(row, column), NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    private static bool TryReadDate(IDataRecord row, string column, out DateTime value)
    {
        var ordinal = row.GetOrdinal(column);
        if (!row.IsDBNull(ordinal) && row.GetValue(ordinal) is DateTime date)
        {
            value = date;
            return true;
        }

        return DateTime.TryParse(ReadText(row, column), CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out value)
            || DateTime.TryParse(ReadText(row, column), CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out value);
    }

    private static string ReadText(IDataRecord row, string column)
    {
        var ordinal = row.GetOrdinal(column);
        return row.IsDBNull(ordinal)
            ? string.Empty
            : Convert.ToString(row.GetValue(ordinal), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static string? FindColumn(IReadOnlyCollection<string> columns, IEnumerable<string> candidates) =>
        candidates.Select(candidate => columns.FirstOrDefault(column => column.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(match => match is not null);

    private static bool IsSafeIdentifier(string value) =>
        Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_.]*$");

    private static bool IsNativeTicketNumber(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value.Trim(), "^[0-9]+$");

    private static string NormalizeNote(string value)
    {
        var withoutPadding = value
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Replace('\u00A0', ' ')
            .ReplaceLineEndings(" ");
        return Regex.Replace(withoutPadding, @"\s+", " ").Trim();
    }

    internal static bool NotesCorrespond(string sageNote, string expectedNote)
    {
        var actual = NormalizeNote(sageNote);
        var expected = NormalizeNote(expectedNote);
        return actual.Equals(expected, StringComparison.OrdinalIgnoreCase)
            || (actual.Length >= 32 && expected.StartsWith(actual, StringComparison.OrdinalIgnoreCase));
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static string BuildConnectionString(string dsn, string username, string password)
    {
        var builder = new OdbcConnectionStringBuilder
        {
            ["DSN"] = dsn.Trim()
        };
        if (!string.IsNullOrWhiteSpace(username))
        {
            builder["UID"] = username.Trim();
        }

        if (!string.IsNullOrEmpty(password))
        {
            builder["PWD"] = password;
        }

        return builder.ConnectionString;
    }

    internal sealed record TicketTable(
        string TableName,
        string TicketNumberColumn,
        string TicketTypeColumn,
        string TicketDateColumn,
        string? DurationHoursColumn,
        string? DurationMinutesColumn,
        string? DurationUnitColumn,
        string? BillTypeColumn,
        IReadOnlyList<string> MemoColumns);
}
