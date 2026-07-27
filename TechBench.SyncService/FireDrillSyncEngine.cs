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
        var workbook = ReadWorkbookContents(snapshot.Bytes, _secretStore.Read());
        var syncedAt = DateTimeOffset.UtcNow;
        var counts = await _repository.ApplyFireDrillSnapshotAsync(
            work, workerId, JsonSerializer.Serialize(workbook.Credentials, JsonOptions),
            snapshot.ModifiedAtUtc, syncedAt, cancellationToken).ConfigureAwait(false);
        CredentialsClientUserSyncCounts? userCounts = null;
        if (workbook.ClientUsers is not null)
        {
            userCounts = await _repository.ApplyCredentialsClientUserSnapshotAsync(
                work, workerId, JsonSerializer.Serialize(workbook.ClientUsers, JsonOptions),
                snapshot.ModifiedAtUtc, syncedAt, cancellationToken).ConfigureAwait(false);
        }

        var userMessage = userCounts is null
            ? " The optional 'Client Users' worksheet was not present."
            : $" Synchronized {userCounts.UserReadCount} client user(s) and {userCounts.AccountReadCount} account row(s); "
              + $"{userCounts.UserSavedCount + userCounts.AccountSavedCount} changed and "
              + $"{userCounts.UserStaleCount + userCounts.AccountStaleCount} became stale.";
        return new FireDrillSyncExecutionResult(
            counts,
            snapshot.ModifiedAtUtc,
            $"Synchronized {counts.ReadCount} Credentials client row(s); {counts.SavedCount} changed and {counts.StaleCount} became stale."
            + userMessage);
    }

    internal static IReadOnlyList<FireDrillCredentialRow> ReadWorkbook(
        byte[] encryptedWorkbook,
        string password) =>
        ReadWorkbookContents(encryptedWorkbook, password).Credentials;

    internal static CredentialsWorkbookContents ReadWorkbookContents(
        byte[] encryptedWorkbook,
        string password)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var stream = new MemoryStream(encryptedWorkbook, writable: false);
        using var reader = ExcelReaderFactory.CreateReader(stream, new ExcelReaderConfiguration
        {
            Password = password,
            LeaveOpen = false
        });

        IReadOnlyList<FireDrillCredentialRow>? credentials = null;
        IReadOnlyList<CredentialsClientUserRow>? clientUsers = null;
        do
        {
            if (!string.Equals(reader.VisibleState, "visible", StringComparison.OrdinalIgnoreCase))
                continue;

            if (NormalizeHeader(reader.Name).Equals("Client Users", StringComparison.OrdinalIgnoreCase))
            {
                clientUsers = ReadClientUsersWorksheet(reader);
                continue;
            }

            credentials ??= ReadCredentialsWorksheet(reader);
        }
        while (reader.NextResult());

        if (credentials is null)
            throw new InvalidDataException("The Credentials workbook contains no visible credential worksheet.");
        return new CredentialsWorkbookContents(credentials, clientUsers);
    }

    private static IReadOnlyList<FireDrillCredentialRow> ReadCredentialsWorksheet(
        IExcelDataReader reader)
    {
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

    private static IReadOnlyList<CredentialsClientUserRow> ReadClientUsersWorksheet(
        IExcelDataReader reader)
    {
        if (!reader.Read())
            throw new InvalidDataException("The 'Client Users' worksheet is empty.");

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
                throw new InvalidDataException(
                    $"'Client Users' column {index + 1} has a header longer than 200 characters.");
            var fieldKey = NormalizeFieldKey(label);
            if (!headerKeys.Add(fieldKey))
                throw new InvalidDataException(
                    $"'Client Users' contains more than one column named '{label}'. No data was changed.");
            columns.Add(new WorkbookColumn(index, fieldKey, label));
        }

        var clientColumn = RequiredColumn(columns, "client");
        var userColumn = RequiredColumn(columns, "user / contact");
        var locationColumn = OptionalColumn(columns, "location / site");
        var roleColumn = OptionalColumn(columns, "role / department");
        var statusColumn = OptionalColumn(columns, "account status");
        var systemColumn = OptionalColumn(columns, "account / system");
        var usernameColumn = OptionalColumn(columns, "username / email");
        string[] identityKeys =
        [
            "client", "location / site", "user / contact", "role / department",
            "account status", "account / system"
        ];
        var accountColumns = columns
            .Where(column => !identityKeys.Contains(column.FieldKey, StringComparer.OrdinalIgnoreCase))
            .OrderBy(column => column.Index)
            .ToArray();

        var people = new Dictionary<string, ClientUserAccumulator>(StringComparer.OrdinalIgnoreCase);
        var accountKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rowNumber = 1;
        while (reader.Read())
        {
            rowNumber++;
            if (blankHeaderColumns.Any(index =>
                    !string.IsNullOrWhiteSpace(CellText(reader.GetValue(index)))))
                throw new InvalidDataException(
                    $"'Client Users' row {rowNumber} contains data beneath a blank column header. No data was changed.");

            var client = CellText(reader.GetValue(clientColumn.Index))?.Trim();
            if (ShouldSkipRow(client)) continue;
            var displayName = CellText(reader.GetValue(userColumn.Index))?.Trim();
            if (string.IsNullOrWhiteSpace(displayName))
                throw new InvalidDataException(
                    $"'Client Users' row {rowNumber} has a Client but no User / Contact. No data was changed.");
            EnsureLength(client, 240, rowNumber, "Client");
            EnsureLength(displayName, 240, rowNumber, "User / Contact");

            var location = Cell(reader, locationColumn);
            var role = Cell(reader, roleColumn);
            var status = Cell(reader, statusColumn);
            var accountSystem = Cell(reader, systemColumn) ?? "General";
            EnsureLength(location, 240, rowNumber, "Location / Site");
            EnsureLength(role, 240, rowNumber, "Role / Department");
            EnsureLength(accountSystem, 240, rowNumber, "Account / System");

            var usernameOrEmail = Cell(reader, usernameColumn);
            var email = usernameOrEmail?.Contains('@') == true
                ? usernameOrEmail
                : null;
            EnsureLength(email, 320, rowNumber, "Username / Email");

            var personSourceKey = "CU-" + HashKey(client, location, displayName);
            if (!people.TryGetValue(personSourceKey, out var person))
            {
                person = new ClientUserAccumulator(
                    personSourceKey, client, displayName, role, email, location,
                    IsActiveStatus(status));
                people.Add(personSourceKey, person);
            }
            else
            {
                person.Merge(role, email, location, IsActiveStatus(status));
            }

            var accountFields = accountColumns
                .Select((column, sortOrder) => new FireDrillCredentialFieldRow(
                    column.FieldKey,
                    column.Label,
                    sortOrder + 1,
                    Secret(CellText(reader.GetValue(column.Index)), rowNumber, column.Label)))
                .ToArray();
            if (!accountFields.Any(field => !string.IsNullOrWhiteSpace(field.Value))
                && string.IsNullOrWhiteSpace(Cell(reader, systemColumn)))
                continue;

            var accountSourceKey = "CA-" + HashKey(
                personSourceKey,
                accountSystem,
                usernameOrEmail);
            if (!accountKeys.Add(accountSourceKey))
                throw new InvalidDataException(
                    $"'Client Users' contains duplicate account rows for '{displayName}' at '{client}' "
                    + $"({accountSystem}). No data was changed.");
            var accountHash = Convert.ToHexString(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(
                    new { accountSystem, accountFields }, JsonOptions)));
            person.Accounts.Add(new CredentialsClientUserAccountRow(
                accountSourceKey,
                accountSystem,
                accountHash,
                accountFields));
        }

        if (people.Count == 0)
            throw new InvalidDataException(
                "The 'Client Users' worksheet contains no client user rows. Existing SQL data was not changed.");

        return people.Values
            .OrderBy(person => person.ClientName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(person => person.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(person => person.ToRow())
            .ToArray();
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

    private static WorkbookColumn RequiredColumn(
        IReadOnlyList<WorkbookColumn> columns,
        string fieldKey) =>
        OptionalColumn(columns, fieldKey)
        ?? throw new InvalidDataException(
            $"The 'Client Users' worksheet must contain a column named '{fieldKey}'. No data was changed.");

    private static WorkbookColumn? OptionalColumn(
        IEnumerable<WorkbookColumn> columns,
        string fieldKey) =>
        columns.FirstOrDefault(column =>
            column.FieldKey.Equals(fieldKey, StringComparison.OrdinalIgnoreCase));

    private static string? Cell(
        IExcelDataReader reader,
        WorkbookColumn? column)
    {
        if (column is null) return null;
        var value = CellText(reader.GetValue(column.Index))?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static void EnsureLength(
        string? value,
        int maximum,
        int rowNumber,
        string field)
    {
        if (value?.Length > maximum)
            throw new InvalidDataException(
                $"'Client Users' row {rowNumber} field '{field}' is longer than {maximum} characters.");
    }

    private static bool IsActiveStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        return value.Trim().ToLowerInvariant() switch
        {
            "inactive" or "disabled" or "terminated" or "former" or "no" or "false" => false,
            _ => true
        };
    }

    private static string HashKey(params string?[] values)
    {
        var normalized = string.Join(
            "\u001f",
            values.Select(value => NormalizeHeader(value).ToLowerInvariant()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

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

    private sealed class ClientUserAccumulator
    {
        public ClientUserAccumulator(
            string sourceKey,
            string clientName,
            string displayName,
            string? roleDepartment,
            string? email,
            string? locationName,
            bool isActive)
        {
            SourceKey = sourceKey;
            ClientName = clientName;
            DisplayName = displayName;
            RoleDepartment = roleDepartment;
            Email = email;
            LocationName = locationName;
            IsActive = isActive;
        }

        public string SourceKey { get; }
        public string ClientName { get; }
        public string DisplayName { get; }
        public string? RoleDepartment { get; private set; }
        public string? Email { get; private set; }
        public string? LocationName { get; private set; }
        public bool IsActive { get; private set; }
        public List<CredentialsClientUserAccountRow> Accounts { get; } = [];

        public void Merge(
            string? roleDepartment,
            string? email,
            string? locationName,
            bool isActive)
        {
            RoleDepartment ??= roleDepartment;
            Email ??= email;
            LocationName ??= locationName;
            IsActive |= isActive;
        }

        public CredentialsClientUserRow ToRow()
        {
            var accounts = Accounts
                .OrderBy(account => account.AccountSystem, StringComparer.OrdinalIgnoreCase)
                .ThenBy(account => account.SourceKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var rowHashHex = Convert.ToHexString(SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        ClientName,
                        DisplayName,
                        RoleDepartment,
                        Email,
                        LocationName,
                        IsActive,
                        accountHashes = accounts.Select(account => account.RowHashHex)
                    },
                    JsonOptions)));
            return new CredentialsClientUserRow(
                SourceKey,
                ClientName,
                DisplayName,
                RoleDepartment,
                Email,
                LocationName,
                IsActive,
                rowHashHex,
                accounts);
        }
    }
}
