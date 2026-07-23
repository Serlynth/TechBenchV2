using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExcelDataReader;

namespace TechBench.SyncService;

public sealed class FireDrillSyncEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SyncSqlRepository _repository;
    private readonly FireDrillSecretStore _secretStore;

    public FireDrillSyncEngine(SyncSqlRepository repository, FireDrillSecretStore secretStore)
    {
        _repository = repository;
        _secretStore = secretStore;
    }

    public async Task<FireDrillSyncExecutionResult> ExecuteAsync(
        FireDrillSyncWork work,
        Guid workerId,
        CancellationToken cancellationToken)
    {
        var configuration = await _repository.GetFireDrillConfigurationAsync(cancellationToken).ConfigureAwait(false);
        if (!configuration.IsConfigured)
            throw new InvalidOperationException("The shared Credentials workbook path must be configured in TechBench Server Manager.");

        var snapshot = await ReadStableSnapshotAsync(configuration.SourcePath, cancellationToken).ConfigureAwait(false);
        var rows = ReadWorkbook(snapshot.Bytes, _secretStore.Read());
        var syncedAt = DateTimeOffset.UtcNow;
        var counts = await _repository.ApplyFireDrillSnapshotAsync(
            work, workerId, JsonSerializer.Serialize(rows, JsonOptions), snapshot.ModifiedAtUtc, syncedAt, cancellationToken).ConfigureAwait(false);
        return new FireDrillSyncExecutionResult(
            counts,
            snapshot.ModifiedAtUtc,
            $"Synchronized {counts.ReadCount} Credentials client row(s); {counts.SavedCount} changed and {counts.StaleCount} became stale.");
    }

    internal static IReadOnlyList<FireDrillCredentialRow> ReadWorkbook(byte[] encryptedWorkbook, string password)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var stream = new MemoryStream(encryptedWorkbook, writable: false);
        using var reader = ExcelReaderFactory.CreateReader(stream, new ExcelReaderConfiguration
        {
            Password = password,
            LeaveOpen = false
        });

        while (!string.Equals(reader.VisibleState, "visible", StringComparison.OrdinalIgnoreCase))
        {
            if (!reader.NextResult()) throw new InvalidDataException("The Credentials workbook contains no visible worksheet.");
        }
        if (!reader.Read()) throw new InvalidDataException("The first visible Credentials worksheet is empty.");

        var columns = new List<WorkbookColumn>();
        var blankHeaderColumns = new List<int>();
        var headerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < reader.FieldCount; index++)
        {
            var label = NormalizeHeader(CellText(reader.GetValue(index)));
            if (string.IsNullOrWhiteSpace(label))
            {
                blankHeaderColumns.Add(index);
                continue;
            }
            if (label.Length > 200)
                throw new InvalidDataException($"Credentials column {index + 1} has a header longer than 200 characters.");
            var fieldKey = NormalizeFieldKey(label);
            if (!headerKeys.Add(fieldKey))
                throw new InvalidDataException($"Credentials contains more than one column named '{label}'. No data was changed.");
            columns.Add(new WorkbookColumn(index, fieldKey, label));
        }
        var clientColumn = columns.FirstOrDefault(column =>
            column.FieldKey.Equals("client", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("The Credentials worksheet must contain a column named 'Client'. No data was changed.");
        var credentialColumns = columns
            .Where(column => column.Index != clientColumn.Index)
            .OrderBy(column => column.Index)
            .ToArray();
        if (credentialColumns.Length == 0)
            throw new InvalidDataException("The Credentials worksheet has no credential columns. Existing SQL data was not changed.");

        var rows = new List<FireDrillCredentialRow>();
        var clients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rowNumber = 1;
        while (reader.Read())
        {
            rowNumber++;
            if (blankHeaderColumns.Any(index =>
                    !string.IsNullOrWhiteSpace(CellText(reader.GetValue(index)))))
                throw new InvalidDataException(
                    $"Credentials row {rowNumber} contains data beneath a blank column header. No data was changed.");
            var client = CellText(reader.GetValue(clientColumn.Index))?.Trim();
            // A row without a client cannot be associated with a TechBench record. Ignore it
            // even when the workbook has notes, formulas, or stale values in other columns.
            if (ShouldSkipRow(client)) continue;
            if (client.Length > 240) throw new InvalidDataException($"Credentials row {rowNumber} has a Client value longer than 240 characters.");
            if (!clients.Add(client)) throw new InvalidDataException($"Credentials contains more than one row for client '{client}'. No data was changed.");

            var fields = credentialColumns
                .Select((column, sortOrder) => new FireDrillCredentialFieldRow(
                    column.FieldKey,
                    column.Label,
                    sortOrder + 1,
                    Secret(CellText(reader.GetValue(column.Index)), rowNumber, column.Label)))
                .ToArray();
            var fireboxIp = SummaryField(fields, "firebox ip", 120, rowNumber);
            var status = SummaryField(fields, "status", 120, rowNumber);
            var rowHashHex = Convert.ToHexString(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(new { client, fields }, JsonOptions)));
            rows.Add(new FireDrillCredentialRow(
                client,
                fireboxIp,
                status,
                rowHashHex,
                fields));
        }

        if (rows.Count == 0) throw new InvalidDataException("The Credentials worksheet contains no client rows. Existing SQL data was not changed.");
        return rows;
    }

    internal static bool ShouldSkipRow([NotNullWhen(false)] string? client) => string.IsNullOrWhiteSpace(client);

    internal static bool IsExpectedHeader(int index, string? actual)
    {
        string[] legacyHeaders =
        [
            "Client", "Firebox IP", "Status", "Admin", "csriadmin",
            "*if enabled -Firebox-DB\\csri", "Authpoint User", "sslvpnpassword",
            "AD Auth User", "AD Password", "RustPW"
        ];
        if (index < 0 || index >= legacyHeaders.Length) return false;
        var normalized = NormalizeHeader(actual);
        if (normalized.Equals(NormalizeHeader(legacyHeaders[index]), StringComparison.OrdinalIgnoreCase)) return true;
        return index == 5
            && normalized.Equals(NormalizeHeader("*if enabled-Firebox-DB\\csri"), StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizeHeader(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal static string NormalizeFieldKey(string? value) =>
        NormalizeHeader(value).ToLowerInvariant();

    private static string? CellText(object? value) => value switch
    {
        null or DBNull => null,
        string text => text,
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    private static string? SummaryField(
        IEnumerable<FireDrillCredentialFieldRow> fields,
        string fieldKey,
        int maximum,
        int row)
    {
        var field = fields.FirstOrDefault(candidate =>
            candidate.FieldKey.Equals(fieldKey, StringComparison.OrdinalIgnoreCase));
        var value = string.IsNullOrWhiteSpace(field?.Value) ? null : field.Value.Trim();
        if (value?.Length > maximum)
            throw new InvalidDataException($"Credentials row {row} field '{field?.Label ?? fieldKey}' is longer than {maximum} characters.");
        return value;
    }

    private static string? Secret(string? value, int row, string field)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length > 3000) throw new InvalidDataException($"Credentials row {row} field '{field}' is longer than 3000 characters.");
        return value;
    }

    private static async Task<WorkbookSnapshot> ReadStableSnapshotAsync(string path, CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var before = new FileInfo(path);
                if (!before.Exists) throw new FileNotFoundException("The Credentials workbook was not found.", path);
                var beforeLength = before.Length;
                var beforeWrite = before.LastWriteTimeUtc;
                await using var source = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var memory = new MemoryStream(beforeLength > 0 && beforeLength <= int.MaxValue ? (int)beforeLength : 0);
                await source.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
                source.Close();
                before.Refresh();
                if (before.Length != beforeLength || before.LastWriteTimeUtc != beforeWrite)
                    throw new IOException("The Credentials workbook changed while it was being read.");
                return new WorkbookSnapshot(memory.ToArray(), new DateTimeOffset(DateTime.SpecifyKind(beforeWrite, DateTimeKind.Utc)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastFailure = ex;
                if (attempt < 4) await Task.Delay(TimeSpan.FromSeconds(attempt * 5), cancellationToken).ConfigureAwait(false);
            }
        }
        throw new IOException("The Credentials workbook could not be read consistently. It may have been saving; TechBench will retry later.", lastFailure);
    }

    private sealed record WorkbookSnapshot(byte[] Bytes, DateTimeOffset ModifiedAtUtc);
    private sealed record WorkbookColumn(int Index, string FieldKey, string Label);
}
