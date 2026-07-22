using System.Globalization;
using System.Text;
using System.Text.Json;
using ExcelDataReader;

namespace TechBench.SyncService;

public sealed class FireDrillSyncEngine
{
    private static readonly string[] ExpectedHeaders =
    [
        "Client", "Firebox IP", "Status", "Admin", "csriadmin",
        "*if enabled-Firebox-DB\\csri", "Authpoint User", "sslvpnpassword",
        "AD Auth User", "AD Password", "RustPW"
    ];
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
            throw new InvalidOperationException("The shared FireDrill workbook path must be configured in TechBench Server Manager.");

        var snapshot = await ReadStableSnapshotAsync(configuration.SourcePath, cancellationToken).ConfigureAwait(false);
        var rows = ReadWorkbook(snapshot.Bytes, _secretStore.Read());
        var syncedAt = DateTimeOffset.UtcNow;
        var counts = await _repository.ApplyFireDrillSnapshotAsync(
            work, workerId, JsonSerializer.Serialize(rows, JsonOptions), snapshot.ModifiedAtUtc, syncedAt, cancellationToken).ConfigureAwait(false);
        return new FireDrillSyncExecutionResult(
            counts,
            snapshot.ModifiedAtUtc,
            $"Synchronized {counts.ReadCount} FireDrill client row(s); {counts.SavedCount} changed and {counts.StaleCount} became stale.");
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
            if (!reader.NextResult()) throw new InvalidDataException("The FireDrill workbook contains no visible worksheet.");
        }
        if (!reader.Read()) throw new InvalidDataException("The first visible FireDrill worksheet is empty.");
        if (reader.FieldCount < ExpectedHeaders.Length)
            throw new InvalidDataException($"The FireDrill header row has {reader.FieldCount} columns; {ExpectedHeaders.Length} are required.");
        for (var index = 0; index < ExpectedHeaders.Length; index++)
        {
            var actual = CellText(reader.GetValue(index))?.Trim() ?? string.Empty;
            if (!actual.Equals(ExpectedHeaders[index], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"FireDrill column {index + 1} must be '{ExpectedHeaders[index]}', but is '{actual}'. No data was changed.");
        }

        var rows = new List<FireDrillCredentialRow>();
        var clients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rowNumber = 1;
        while (reader.Read())
        {
            rowNumber++;
            var client = CellText(reader.GetValue(0))?.Trim();
            if (string.IsNullOrWhiteSpace(client))
            {
                if (Enumerable.Range(1, ExpectedHeaders.Length - 1).Any(index => !string.IsNullOrEmpty(CellText(reader.GetValue(index)))))
                    throw new InvalidDataException($"FireDrill row {rowNumber} contains data but has no Client value. No data was changed.");
                continue;
            }
            if (client.Length > 240) throw new InvalidDataException($"FireDrill row {rowNumber} has a Client value longer than 240 characters.");
            if (!clients.Add(client)) throw new InvalidDataException($"FireDrill contains more than one row for client '{client}'. No data was changed.");

            rows.Add(new FireDrillCredentialRow(
                client,
                Limited(CellText(reader.GetValue(1)), 120, rowNumber, ExpectedHeaders[1]),
                Limited(CellText(reader.GetValue(2)), 120, rowNumber, ExpectedHeaders[2]),
                Secret(CellText(reader.GetValue(3)), rowNumber, ExpectedHeaders[3]),
                Secret(CellText(reader.GetValue(4)), rowNumber, ExpectedHeaders[4]),
                Secret(CellText(reader.GetValue(5)), rowNumber, ExpectedHeaders[5]),
                Secret(CellText(reader.GetValue(6)), rowNumber, ExpectedHeaders[6]),
                Secret(CellText(reader.GetValue(7)), rowNumber, ExpectedHeaders[7]),
                Secret(CellText(reader.GetValue(8)), rowNumber, ExpectedHeaders[8]),
                Secret(CellText(reader.GetValue(9)), rowNumber, ExpectedHeaders[9]),
                Secret(CellText(reader.GetValue(10)), rowNumber, ExpectedHeaders[10])));
        }

        if (rows.Count == 0) throw new InvalidDataException("The FireDrill worksheet contains no client rows. Existing SQL data was not changed.");
        return rows;
    }

    private static string? CellText(object? value) => value switch
    {
        null or DBNull => null,
        string text => text,
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    private static string? Limited(string? value, int maximum, int row, string field)
    {
        value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (value?.Length > maximum) throw new InvalidDataException($"FireDrill row {row} field '{field}' is longer than {maximum} characters.");
        return value;
    }

    private static string? Secret(string? value, int row, string field)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length > 3000) throw new InvalidDataException($"FireDrill row {row} field '{field}' is longer than 3000 characters.");
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
                if (!before.Exists) throw new FileNotFoundException("The FireDrill workbook was not found.", path);
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
                    throw new IOException("The FireDrill workbook changed while it was being read.");
                return new WorkbookSnapshot(memory.ToArray(), new DateTimeOffset(DateTime.SpecifyKind(beforeWrite, DateTimeKind.Utc)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastFailure = ex;
                if (attempt < 4) await Task.Delay(TimeSpan.FromSeconds(attempt * 5), cancellationToken).ConfigureAwait(false);
            }
        }
        throw new IOException("The FireDrill workbook could not be read consistently. It may have been saving; TechBench will retry later.", lastFailure);
    }

    private sealed record WorkbookSnapshot(byte[] Bytes, DateTimeOffset ModifiedAtUtc);
}
