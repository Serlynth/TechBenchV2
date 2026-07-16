using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using TechBench.Models;
using TechBench.Services;

namespace TechBench.Data;

public sealed class TechBenchRepository
{
    private const string ClientSelectColumns = """
        Id, Name, Source, ExternalId, IsActive, LastSyncedAt,
        WhdLocationName, WhdContactName, SageCustomerId, SageCustomerName, SageContactName, SageTelephone, MatchStatus
        """;

    private readonly SqliteConnectionFactory _connectionFactory;
    private bool _fullTextSearchAvailable;

    public TechBenchRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public string DatabasePath => _connectionFactory.DatabasePath;

    public void Initialize()
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        command.ExecuteNonQuery();
        EnsureClientSyncColumns(connection);
        EnsureTicketStatusSchema(connection);
        EnsureWorkEntryTimeRangeColumn(connection);
        EnsureWorkEntryClientReferenceSchema(connection);
        EnsureSageVerificationColumns(connection);
        EnsureNoteTakingSchema(connection);
        EnsurePersonalNotePostingColumn(connection);
        EnsureWorkEntryLinkSchema(connection);
        EnsureCommonLinkSchema(connection);
        EnsurePostingAttemptSchema(connection);
        RecoverInterruptedPostingAttempts(connection);
        BackfillSageTicketNumbers(connection);
        BackfillClientSyncMetadata(connection);
        RepairSageOnlyClientMetadata(connection);
        BackfillClientMatchMetadata(connection);
        RemoveSeedDemoData(connection);
        Seed(connection);
        ApplyMockModeRemovalMigration(connection);
        RebuildWorkEntrySearchIndex(connection);
    }

    public bool FullTextSearchAvailable => _fullTextSearchAvailable;

    public IReadOnlyList<Client> GetClients(bool includeInactive = false, string? searchTerm = null)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        var sql = new StringBuilder($"SELECT {ClientSelectColumns} FROM Clients WHERE 1 = 1");

        if (!includeInactive)
        {
            sql.Append(" AND IsActive = 1");
        }

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            sql.Append("""
                 AND (Name LIKE $search
                      OR WhdLocationName LIKE $search
                      OR WhdContactName LIKE $search
                      OR SageCustomerId LIKE $search
                      OR SageCustomerName LIKE $search
                      OR SageContactName LIKE $search)
                """);
            command.Parameters.AddWithValue("$search", $"%{searchTerm.Trim()}%");
        }

        sql.Append(" ORDER BY Name COLLATE NOCASE");
        command.CommandText = sql.ToString();

        using var reader = command.ExecuteReader();
        var clients = new List<Client>();
        while (reader.Read())
        {
            clients.Add(ReadClient(reader));
        }

        return clients;
    }

    public Client? GetClient(int id)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Source, ExternalId, IsActive, LastSyncedAt,
                   WhdLocationName, WhdContactName, SageCustomerId, SageCustomerName, SageContactName, SageTelephone, MatchStatus
            FROM Clients
            WHERE Id = $id
            """;
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadClient(reader) : null;
    }

    public int SaveClient(Client client)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();

        if (client.Id == 0)
        {
            command.CommandText = """
                INSERT INTO Clients
                    (Name, Source, ExternalId, IsActive, LastSyncedAt,
                     WhdLocationName, WhdContactName, SageCustomerId, SageCustomerName, SageContactName, SageTelephone, MatchStatus)
                VALUES
                    ($name, $source, $externalId, $isActive, $lastSyncedAt,
                     $whdLocationName, $whdContactName, $sageCustomerId, $sageCustomerName, $sageContactName, $sageTelephone, $matchStatus);
                SELECT last_insert_rowid();
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE Clients
                SET Name = $name,
                    Source = $source,
                    ExternalId = $externalId,
                    IsActive = $isActive,
                    LastSyncedAt = $lastSyncedAt,
                    WhdLocationName = $whdLocationName,
                    WhdContactName = $whdContactName,
                    SageCustomerId = $sageCustomerId,
                    SageCustomerName = $sageCustomerName,
                    SageContactName = $sageContactName,
                    SageTelephone = $sageTelephone,
                    MatchStatus = $matchStatus
                WHERE Id = $id;
                SELECT $id;
                """;
            command.Parameters.AddWithValue("$id", client.Id);
        }

        client.Name = BuildClientDisplayName(client);
        client.Source = NormalizeClientSource(client.Source);
        client.MatchStatus = ResolveMatchStatus(client);

        command.Parameters.AddWithValue("$name", client.Name.Trim());
        command.Parameters.AddWithValue("$source", NormalizeClientSource(client.Source));
        command.Parameters.AddWithValue("$externalId", (object?)client.ExternalId ?? DBNull.Value);
        command.Parameters.AddWithValue("$isActive", client.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$lastSyncedAt", ToDbDateTime(client.LastSyncedAt));
        command.Parameters.AddWithValue("$whdLocationName", ToDbText(client.WhdLocationName));
        command.Parameters.AddWithValue("$whdContactName", ToDbText(client.WhdContactName));
        command.Parameters.AddWithValue("$sageCustomerId", ToDbText(client.SageCustomerId));
        command.Parameters.AddWithValue("$sageCustomerName", ToDbText(client.SageCustomerName));
        command.Parameters.AddWithValue("$sageContactName", ToDbText(client.SageContactName));
        command.Parameters.AddWithValue("$sageTelephone", ToDbText(client.SageTelephone));
        command.Parameters.AddWithValue("$matchStatus", string.IsNullOrWhiteSpace(client.MatchStatus) ? "Unmatched" : client.MatchStatus.Trim());

        var id = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        client.Id = id;
        return id;
    }

    public void SynchronizeServerClientCache(IReadOnlyList<Client> clients)
    {
        ArgumentNullException.ThrowIfNull(clients);
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        foreach (var client in clients.Where(static client => client.Id > 0))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO Clients
                    (Id, Name, Source, ExternalId, IsActive, LastSyncedAt,
                     WhdLocationName, WhdContactName, SageCustomerId, SageCustomerName,
                     SageContactName, SageTelephone, MatchStatus)
                VALUES
                    ($id, $name, $source, $externalId, $isActive, $lastSyncedAt,
                     $whdLocationName, $whdContactName, $sageCustomerId, $sageCustomerName,
                     $sageContactName, $sageTelephone, $matchStatus)
                ON CONFLICT(Id) DO UPDATE SET
                    Name = excluded.Name,
                    Source = excluded.Source,
                    ExternalId = excluded.ExternalId,
                    IsActive = excluded.IsActive,
                    LastSyncedAt = excluded.LastSyncedAt,
                    WhdLocationName = excluded.WhdLocationName,
                    WhdContactName = excluded.WhdContactName,
                    SageCustomerId = excluded.SageCustomerId,
                    SageCustomerName = excluded.SageCustomerName,
                    SageContactName = excluded.SageContactName,
                    SageTelephone = excluded.SageTelephone,
                    MatchStatus = excluded.MatchStatus
                """;
            command.Parameters.AddWithValue("$id", client.Id);
            command.Parameters.AddWithValue("$name", client.Name.Trim());
            command.Parameters.AddWithValue("$source", string.IsNullOrWhiteSpace(client.Source) ? "Server" : client.Source.Trim());
            command.Parameters.AddWithValue("$externalId", ToDbText(client.ExternalId));
            command.Parameters.AddWithValue("$isActive", client.IsActive ? 1 : 0);
            command.Parameters.AddWithValue("$lastSyncedAt", ToDbDateTime(client.LastSyncedAt));
            command.Parameters.AddWithValue("$whdLocationName", ToDbText(client.WhdLocationName));
            command.Parameters.AddWithValue("$whdContactName", ToDbText(client.WhdContactName));
            command.Parameters.AddWithValue("$sageCustomerId", ToDbText(client.SageCustomerId));
            command.Parameters.AddWithValue("$sageCustomerName", ToDbText(client.SageCustomerName));
            command.Parameters.AddWithValue("$sageContactName", ToDbText(client.SageContactName));
            command.Parameters.AddWithValue("$sageTelephone", ToDbText(client.SageTelephone));
            command.Parameters.AddWithValue("$matchStatus", string.IsNullOrWhiteSpace(client.MatchStatus) ? "Unmatched" : client.MatchStatus.Trim());
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<Ticket> GetTickets(int? clientId = null, string? searchTerm = null, bool includeClosed = false)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        var sql = new StringBuilder("""
            SELECT Id, TicketNumber, ClientId, Subject, Status, Source, ExternalId, WhdStatusTypeId, IsClosed, LastSyncedAt
            FROM Tickets
            WHERE 1 = 1
            """);

        if (clientId.HasValue)
        {
            sql.Append(" AND ClientId = $clientId");
            command.Parameters.AddWithValue("$clientId", clientId.Value);
        }

        if (!includeClosed)
        {
            sql.Append(" AND IsClosed = 0");
        }

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            sql.Append(" AND (TicketNumber LIKE $search OR Subject LIKE $search)");
            command.Parameters.AddWithValue("$search", $"%{searchTerm.Trim()}%");
        }

        sql.Append(" ORDER BY IsClosed, TicketNumber COLLATE NOCASE");
        command.CommandText = sql.ToString();

        using var reader = command.ExecuteReader();
        var tickets = new List<Ticket>();
        while (reader.Read())
        {
            tickets.Add(ReadTicket(reader));
        }

        return tickets;
    }

    public Ticket? GetTicket(int id)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, TicketNumber, ClientId, Subject, Status, Source, ExternalId, WhdStatusTypeId, IsClosed, LastSyncedAt
            FROM Tickets
            WHERE Id = $id
            """;
        command.Parameters.AddWithValue("$id", id);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTicket(reader) : null;
    }

    public int SaveTicket(Ticket ticket)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();

        if (ticket.Id == 0)
        {
            command.CommandText = """
                INSERT INTO Tickets (TicketNumber, ClientId, Subject, Status, Source, ExternalId, WhdStatusTypeId, IsClosed, LastSyncedAt)
                VALUES ($ticketNumber, $clientId, $subject, $status, $source, $externalId, $whdStatusTypeId, $isClosed, $lastSyncedAt);
                SELECT last_insert_rowid();
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE Tickets
                SET TicketNumber = $ticketNumber,
                    ClientId = $clientId,
                    Subject = $subject,
                    Status = $status,
                    Source = $source,
                    ExternalId = $externalId,
                    WhdStatusTypeId = $whdStatusTypeId,
                    IsClosed = $isClosed,
                    LastSyncedAt = $lastSyncedAt
                WHERE Id = $id;
                SELECT $id;
                """;
            command.Parameters.AddWithValue("$id", ticket.Id);
        }

        command.Parameters.AddWithValue("$ticketNumber", ticket.TicketNumber.Trim());
        command.Parameters.AddWithValue("$clientId", ticket.ClientId);
        command.Parameters.AddWithValue("$subject", ticket.Subject.Trim());
        command.Parameters.AddWithValue("$status", string.IsNullOrWhiteSpace(ticket.Status) ? "Open" : ticket.Status.Trim());
        command.Parameters.AddWithValue("$source", string.IsNullOrWhiteSpace(ticket.Source) ? "Manual" : ticket.Source.Trim());
        command.Parameters.AddWithValue("$externalId", (object?)ticket.ExternalId ?? DBNull.Value);
        command.Parameters.AddWithValue("$whdStatusTypeId", (object?)ticket.WhdStatusTypeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$isClosed", ticket.IsClosed ? 1 : 0);
        command.Parameters.AddWithValue("$lastSyncedAt", ToDbDateTime(ticket.LastSyncedAt));

        var id = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        ticket.Id = id;
        return id;
    }

    public IReadOnlyList<TicketStatusOption> GetTicketStatusOptions()
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        var options = new List<TicketStatusOption>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT Id, Name, Source, ExternalId, WhdStatusTypeId, IsClosed, LastSyncedAt
                FROM TicketStatusOptions
                ORDER BY IsClosed, Name COLLATE NOCASE
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                options.Add(ReadTicketStatusOption(reader));
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT DISTINCT Status
                FROM Tickets
                WHERE Status IS NOT NULL AND TRIM(Status) <> ''
                ORDER BY Status COLLATE NOCASE
                """;

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var status = reader.GetString(0);
                if (options.Any(option => option.Name.Equals(status, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                options.Add(new TicketStatusOption
                {
                    Name = status,
                    Source = "Local",
                    ExternalId = $"LOCAL-{NormalizeStatusKey(status)}",
                    IsClosed = IsClosedStatus(status)
                });
            }
        }

        if (options.Count == 0)
        {
            foreach (var status in new[] { "Open", "Pending", "Resolved", "Closed" })
            {
                options.Add(new TicketStatusOption
                {
                    Name = status,
                    Source = "Local",
                    ExternalId = $"LOCAL-{NormalizeStatusKey(status)}",
                    IsClosed = IsClosedStatus(status)
                });
            }
        }

        return options
            .OrderBy(static option => option.IsClosed)
            .ThenBy(static option => option.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public int UpsertTicketStatusOption(TicketStatusOption option)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        var existingId = FindTicketStatusOptionId(connection, option);
        using var command = connection.CreateCommand();
        if (existingId == 0)
        {
            command.CommandText = """
                INSERT INTO TicketStatusOptions (Name, Source, ExternalId, WhdStatusTypeId, IsClosed, LastSyncedAt)
                VALUES ($name, $source, $externalId, $whdStatusTypeId, $isClosed, $lastSyncedAt);
                SELECT last_insert_rowid();
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE TicketStatusOptions
                SET Name = $name,
                    Source = $source,
                    ExternalId = $externalId,
                    WhdStatusTypeId = $whdStatusTypeId,
                    IsClosed = $isClosed,
                    LastSyncedAt = $lastSyncedAt
                WHERE Id = $id;
                SELECT $id;
                """;
            command.Parameters.AddWithValue("$id", existingId);
        }

        var name = string.IsNullOrWhiteSpace(option.Name) ? "Open" : option.Name.Trim();
        var source = string.IsNullOrWhiteSpace(option.Source) ? "WHD" : option.Source.Trim();
        var externalId = string.IsNullOrWhiteSpace(option.ExternalId)
            ? option.WhdStatusTypeId.HasValue ? $"WHD-{option.WhdStatusTypeId.Value}" : $"LOCAL-{NormalizeStatusKey(name)}"
            : option.ExternalId.Trim();

        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$externalId", externalId);
        command.Parameters.AddWithValue("$whdStatusTypeId", (object?)option.WhdStatusTypeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$isClosed", option.IsClosed || IsClosedStatus(name) ? 1 : 0);
        command.Parameters.AddWithValue("$lastSyncedAt", ToDbDateTime(option.LastSyncedAt));

        var id = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        option.Id = id;
        return id;
    }

    public int UpsertSyncedClient(Client client)
    {
        var existing = FindClientForSync(client);
        if (existing is not null)
        {
            client = MergeClient(existing, client);
        }

        return SaveClient(client);
    }

    public int UpsertSageCustomer(SageCustomer customer, DateTime? syncedAt = null)
    {
        var client = new Client
        {
            Name = customer.CustomerName,
            Source = "Sage",
            IsActive = customer.IsActive,
            LastSyncedAt = syncedAt ?? DateTime.Now,
            SageCustomerId = customer.CustomerId,
            SageCustomerName = customer.CustomerName,
            SageContactName = customer.ContactName,
            SageTelephone = customer.Telephone
        };

        return UpsertSyncedClient(client);
    }

    public void SaveClientSageMapping(int clientId, string sageCustomerId, string? sageCustomerName = null)
    {
        var client = GetClient(clientId);
        if (client is null)
        {
            return;
        }

        var normalizedCustomerId = string.IsNullOrWhiteSpace(sageCustomerId) ? null : sageCustomerId.Trim();
        if (normalizedCustomerId is not null)
        {
            var existingSageClient = GetClients(includeInactive: true)
                .FirstOrDefault(candidate => candidate.Id != clientId
                    && string.Equals(
                        candidate.SageCustomerId?.Trim(),
                        normalizedCustomerId,
                        StringComparison.OrdinalIgnoreCase));
            if (existingSageClient is not null && HasWhdIdentity(client))
            {
                MergeClientRecords(clientId, existingSageClient.Id);
                return;
            }
        }

        client.SageCustomerId = normalizedCustomerId;
        if (!string.IsNullOrWhiteSpace(sageCustomerName))
        {
            client.SageCustomerName = sageCustomerName.Trim();
        }
        else if (string.IsNullOrWhiteSpace(client.SageCustomerName))
        {
            client.SageCustomerName = client.WhdLocationName ?? client.Name;
        }

        client.Source = MergeClientSources(client.Source, "Sage");
        client.MatchStatus = string.IsNullOrWhiteSpace(client.SageCustomerId) ? "Unmatched" : "Manual match";
        SaveClient(client);
    }

    public Client MergeClientRecords(int whdClientId, int sageClientId)
    {
        if (whdClientId <= 0 || sageClientId <= 0 || whdClientId == sageClientId)
        {
            throw new ArgumentException("Select separate WHD and Sage client records to match.");
        }

        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var target = GetClient(connection, transaction, whdClientId)
            ?? throw new InvalidOperationException("The selected WHD client no longer exists.");
        var source = GetClient(connection, transaction, sageClientId)
            ?? throw new InvalidOperationException("The selected Sage customer no longer exists.");

        if (!HasWhdIdentity(target) && HasWhdIdentity(source))
        {
            (target, source) = (source, target);
        }

        if (!HasWhdIdentity(target))
        {
            throw new InvalidOperationException("The selected TechBench client is not linked to a WHD location.");
        }

        if (!HasSageIdentity(source))
        {
            throw new InvalidOperationException("The selected match is not linked to a Sage customer.");
        }

        if (!string.IsNullOrWhiteSpace(target.SageCustomerId)
            && !string.Equals(
                target.SageCustomerId.Trim(),
                source.SageCustomerId?.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This WHD location is already linked to a different Sage customer.");
        }

        var affectedWorkEntryIds = new List<int>();
        using (var affectedEntries = connection.CreateCommand())
        {
            affectedEntries.Transaction = transaction;
            affectedEntries.CommandText = """
                SELECT Id
                FROM WorkEntries
                WHERE ClientId = $targetId OR ClientId = $sourceId
                """;
            affectedEntries.Parameters.AddWithValue("$targetId", target.Id);
            affectedEntries.Parameters.AddWithValue("$sourceId", source.Id);
            using var reader = affectedEntries.ExecuteReader();
            while (reader.Read())
            {
                affectedWorkEntryIds.Add(reader.GetInt32(0));
            }
        }

        using (var moveWorkEntries = connection.CreateCommand())
        {
            moveWorkEntries.Transaction = transaction;
            moveWorkEntries.CommandText = "UPDATE WorkEntries SET ClientId = $targetId WHERE ClientId = $sourceId";
            moveWorkEntries.Parameters.AddWithValue("$targetId", target.Id);
            moveWorkEntries.Parameters.AddWithValue("$sourceId", source.Id);
            moveWorkEntries.ExecuteNonQuery();
        }

        using (var moveTickets = connection.CreateCommand())
        {
            moveTickets.Transaction = transaction;
            moveTickets.CommandText = "UPDATE Tickets SET ClientId = $targetId WHERE ClientId = $sourceId";
            moveTickets.Parameters.AddWithValue("$targetId", target.Id);
            moveTickets.Parameters.AddWithValue("$sourceId", source.Id);
            moveTickets.ExecuteNonQuery();
        }

        using (var moveAliases = connection.CreateCommand())
        {
            moveAliases.Transaction = transaction;
            moveAliases.CommandText = "UPDATE ClientAliases SET ClientId = $targetId WHERE ClientId = $sourceId";
            moveAliases.Parameters.AddWithValue("$targetId", target.Id);
            moveAliases.Parameters.AddWithValue("$sourceId", source.Id);
            moveAliases.ExecuteNonQuery();
        }

        target = MergeClient(target, source);
        target.MatchStatus = "Manual match";
        SaveClient(connection, transaction, target);

        using (var deleteSource = connection.CreateCommand())
        {
            deleteSource.Transaction = transaction;
            deleteSource.CommandText = "DELETE FROM Clients WHERE Id = $sourceId";
            deleteSource.Parameters.AddWithValue("$sourceId", source.Id);
            deleteSource.ExecuteNonQuery();
        }

        foreach (var workEntryId in affectedWorkEntryIds)
        {
            UpdateWorkEntrySearchIndex(connection, transaction, workEntryId);
        }

        transaction.Commit();
        return target;
    }

    public int ReconcileExactClientMatches()
    {
        var clients = GetClients(includeInactive: true);
        var whdClients = clients
            .Where(ClientMatchingService.IsWhdLocationCandidate)
            .ToList();
        var sageGroups = clients
            .Where(ClientMatchingService.IsSageMatchCandidate)
            .GroupBy(
                client => NormalizeClientMatchKey(ResolveCompanyNameForMatch(client)),
                StringComparer.Ordinal)
            .Where(static group => !string.IsNullOrWhiteSpace(group.Key))
            .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.Ordinal);
        var matchedCount = 0;

        foreach (var whdClient in whdClients)
        {
            var key = NormalizeClientMatchKey(ResolveCompanyNameForMatch(whdClient));
            if (!sageGroups.TryGetValue(key, out var matches) || matches.Count != 1)
            {
                continue;
            }

            MergeClientRecords(whdClient.Id, matches[0].Id);
            sageGroups.Remove(key);
            matchedCount++;
        }

        return matchedCount;
    }

    public int ReconcileStrongClientMatches()
    {
        var clients = GetClients(includeInactive: true);
        var matches = ClientMatchingService.FindSafeAutomaticMatches(clients, clients);
        var matchedCount = 0;
        foreach (var match in matches)
        {
            MergeClientRecords(match.WhdClient.Id, match.SageClient.Id);
            matchedCount++;
        }

        return matchedCount;
    }

    public int ReconcileSafeClientMatches()
    {
        return ReconcileExactClientMatches() + ReconcileStrongClientMatches();
    }

    public int RemoveStaleSageCustomers(IReadOnlyCollection<string> activeSageCustomerIds, DateTime? syncedAt = null)
    {
        var activeIds = activeSageCustomerIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (activeIds.Count == 0)
        {
            return 0;
        }

        using var connection = _connectionFactory.CreateConnection();
        connection.Open();

        using (var createCommand = connection.CreateCommand())
        {
            createCommand.CommandText = """
                DROP TABLE IF EXISTS ActiveSageCustomerIds;
                CREATE TEMP TABLE ActiveSageCustomerIds (
                    CustomerId TEXT NOT NULL PRIMARY KEY
                );
                """;
            createCommand.ExecuteNonQuery();
        }

        foreach (var customerId in activeIds)
        {
            using var insertCommand = connection.CreateCommand();
            insertCommand.CommandText = "INSERT OR IGNORE INTO ActiveSageCustomerIds (CustomerId) VALUES ($customerId)";
            insertCommand.Parameters.AddWithValue("$customerId", customerId);
            insertCommand.ExecuteNonQuery();
        }

        var staleClients = new List<Client>();
        using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.CommandText = $"""
                SELECT {ClientSelectColumns}
                FROM Clients
                WHERE SageCustomerId IS NOT NULL
                  AND TRIM(SageCustomerId) <> ''
                  AND NOT EXISTS (
                      SELECT 1
                      FROM ActiveSageCustomerIds active
                      WHERE active.CustomerId = TRIM(Clients.SageCustomerId)
                  )
                """;
            using var reader = selectCommand.ExecuteReader();
            while (reader.Read())
            {
                staleClients.Add(ReadClient(reader));
            }
        }

        var changedCount = 0;
        var lastSyncedAt = ToDbDateTime(syncedAt ?? DateTime.Now);
        foreach (var client in staleClients)
        {
            if (client.Source.Equals("Sage", StringComparison.OrdinalIgnoreCase)
                && !ClientHasReferences(connection, client.Id))
            {
                using var deleteCommand = connection.CreateCommand();
                deleteCommand.CommandText = "DELETE FROM Clients WHERE Id = $id";
                deleteCommand.Parameters.AddWithValue("$id", client.Id);
                changedCount += deleteCommand.ExecuteNonQuery();
                continue;
            }

            if (client.Source.Equals("Sage", StringComparison.OrdinalIgnoreCase))
            {
                using var deactivateCommand = connection.CreateCommand();
                deactivateCommand.CommandText = """
                    UPDATE Clients
                    SET IsActive = 0,
                        LastSyncedAt = $lastSyncedAt
                    WHERE Id = $id
                    """;
                deactivateCommand.Parameters.AddWithValue("$id", client.Id);
                deactivateCommand.Parameters.AddWithValue("$lastSyncedAt", lastSyncedAt);
                changedCount += deactivateCommand.ExecuteNonQuery();
                continue;
            }

            client.Source = "WHD";
            client.SageCustomerId = null;
            client.SageCustomerName = null;
            client.SageContactName = null;
            client.SageTelephone = null;
            client.MatchStatus = ResolveMatchStatus(client);
            client.Name = BuildClientDisplayName(client);

            using var updateCommand = connection.CreateCommand();
            updateCommand.CommandText = """
                UPDATE Clients
                SET Name = $name,
                    Source = $source,
                    LastSyncedAt = $lastSyncedAt,
                    SageCustomerId = NULL,
                    SageCustomerName = NULL,
                    SageContactName = NULL,
                    SageTelephone = NULL,
                    MatchStatus = $matchStatus
                WHERE Id = $id
                """;
            updateCommand.Parameters.AddWithValue("$id", client.Id);
            updateCommand.Parameters.AddWithValue("$name", client.Name);
            updateCommand.Parameters.AddWithValue("$source", client.Source);
            updateCommand.Parameters.AddWithValue("$lastSyncedAt", lastSyncedAt);
            updateCommand.Parameters.AddWithValue("$matchStatus", client.MatchStatus);
            changedCount += updateCommand.ExecuteNonQuery();
        }

        return changedCount;
    }

    public Client? TryAutoMatchSageCustomerForClient(int clientId)
    {
        var client = GetClient(clientId);
        if (client is null || !string.IsNullOrWhiteSpace(client.SageCustomerId))
        {
            return null;
        }

        var clientMatchKey = NormalizeClientMatchKey(ResolveCompanyNameForMatch(client));
        if (string.IsNullOrWhiteSpace(clientMatchKey))
        {
            return null;
        }

        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ClientSelectColumns}
            FROM Clients
            WHERE Id <> $clientId
              AND SageCustomerId IS NOT NULL
              AND SageCustomerId <> ''
            """;
        command.Parameters.AddWithValue("$clientId", clientId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var candidate = ReadClient(reader);
            if (NormalizeClientMatchKey(ResolveCompanyNameForMatch(candidate)) != clientMatchKey)
            {
                continue;
            }

            client.SageCustomerId = candidate.SageCustomerId;
            client.SageCustomerName = candidate.SageCustomerName;
            client.SageContactName = candidate.SageContactName;
            client.SageTelephone = candidate.SageTelephone;
            client.Source = MergeClientSources(client.Source, "Sage");
            client.MatchStatus = "Matched";
            SaveClient(client);
            return client;
        }

        return null;
    }

    public int UpsertSyncedTicket(Ticket ticket)
    {
        var existing = FindTicketForSync(ticket.Source, ticket.ExternalId, ticket.TicketNumber);
        if (existing is not null)
        {
            ticket.Id = existing.Id;
        }

        return SaveTicket(ticket);
    }

    public void SynchronizeWhdTickets(
        IReadOnlyList<WhdSyncedTicket> whdTickets,
        DateTime syncedAt,
        bool reconcileMissing)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var clients = ReadAllClients(connection, transaction);
        var tickets = ReadAllTickets(connection, transaction);

        foreach (var whdTicket in whdTickets)
        {
            var incomingClient = new Client
            {
                Name = whdTicket.Client.Name,
                Source = "WHD",
                ExternalId = whdTicket.Client.ExternalId,
                WhdLocationName = whdTicket.Client.LocationName,
                WhdContactName = whdTicket.Client.ContactName,
                IsActive = true,
                LastSyncedAt = syncedAt
            };
            var existingClient = FindClientForSync(clients, incomingClient);
            var client = existingClient is null ? incomingClient : MergeClient(existingClient, incomingClient);
            SaveClient(connection, transaction, client);
            if (existingClient is null)
            {
                clients.Add(client);
            }

            var incomingTicket = new Ticket
            {
                TicketNumber = whdTicket.TicketNumber,
                ClientId = client.Id,
                Subject = whdTicket.Subject,
                Status = whdTicket.Status,
                Source = "WHD",
                ExternalId = whdTicket.ExternalId,
                WhdStatusTypeId = whdTicket.StatusTypeId,
                IsClosed = whdTicket.IsClosed,
                LastSyncedAt = syncedAt
            };
            var existingTicket = tickets.FirstOrDefault(ticket =>
                ticket.Source.Equals("WHD", StringComparison.OrdinalIgnoreCase)
                && ((!string.IsNullOrWhiteSpace(incomingTicket.ExternalId)
                        && string.Equals(ticket.ExternalId, incomingTicket.ExternalId, StringComparison.OrdinalIgnoreCase))
                    || ticket.TicketNumber.Equals(incomingTicket.TicketNumber, StringComparison.OrdinalIgnoreCase)));
            if (existingTicket is not null)
            {
                incomingTicket.Id = existingTicket.Id;
            }

            SaveTicket(connection, transaction, incomingTicket);
            if (existingTicket is null)
            {
                tickets.Add(incomingTicket);
            }
        }

        if (reconcileMissing)
        {
            ReconcileMissingWhdTickets(connection, transaction, whdTickets, syncedAt);
        }

        transaction.Commit();
    }

    public int SynchronizeWhdClients(
        IReadOnlyList<WhdSyncedClient> whdClients,
        DateTime syncedAt,
        bool reconcileMissing = false)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var clients = ReadAllClients(connection, transaction);
        var activeExternalIds = whdClients
            .Where(static client => client.IsActive && !string.IsNullOrWhiteSpace(client.ExternalId))
            .Select(static client => client.ExternalId.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matchedCount = 0;

        foreach (var whdClient in whdClients.Where(static client => client.IsActive))
        {
            var incoming = new Client
            {
                Name = whdClient.Name,
                Source = "WHD",
                ExternalId = whdClient.ExternalId,
                WhdLocationName = whdClient.LocationName,
                WhdContactName = whdClient.ContactName,
                IsActive = true,
                LastSyncedAt = syncedAt
            };
            var existing = FindClientForSync(clients, incoming);
            var merged = existing is null ? incoming : MergeClient(existing, incoming);
            SaveClient(connection, transaction, merged);
            if (existing is null)
            {
                clients.Add(merged);
            }

            if (!string.IsNullOrWhiteSpace(merged.SageCustomerId))
            {
                matchedCount++;
            }
        }

        if (reconcileMissing && activeExternalIds.Count > 0)
        {
            RemoveStaleWhdOnlyClients(connection, transaction, clients, activeExternalIds, syncedAt);
        }

        transaction.Commit();
        return matchedCount;
    }

    public (int SavedCount, int StaleCount) SynchronizeSageCustomers(
        IReadOnlyList<SageCustomer> customers,
        DateTime syncedAt)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var clients = ReadAllClients(connection, transaction);
        var activeSageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var savedCount = 0;

        foreach (var customer in customers)
        {
            if (string.IsNullOrWhiteSpace(customer.CustomerId)
                || string.IsNullOrWhiteSpace(customer.CustomerName))
            {
                continue;
            }

            activeSageIds.Add(customer.CustomerId.Trim());
            var incoming = new Client
            {
                Name = customer.CustomerName,
                Source = "Sage",
                IsActive = customer.IsActive,
                LastSyncedAt = syncedAt,
                SageCustomerId = customer.CustomerId,
                SageCustomerName = customer.CustomerName,
                SageContactName = customer.ContactName,
                SageTelephone = customer.Telephone
            };
            var existing = FindClientForSync(clients, incoming);
            var merged = existing is null ? incoming : MergeClient(existing, incoming);
            SaveClient(connection, transaction, merged);
            if (existing is null)
            {
                clients.Add(merged);
            }

            savedCount++;
        }

        var staleCount = activeSageIds.Count == 0
            ? 0
            : RemoveStaleSageCustomers(connection, transaction, clients, activeSageIds, syncedAt);
        transaction.Commit();
        return (savedCount, staleCount);
    }

    public IReadOnlyList<WorkEntry> GetWorkEntries(WorkEntryQuery query)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        var fullTextQuery = BuildFullTextQuery(query.Keyword);
        var useFullTextSearch = _fullTextSearchAvailable && !string.IsNullOrWhiteSpace(fullTextQuery);
        var snippetExpression = useFullTextSearch
            ? "snippet(WorkEntrySearch, -1, '[', ']', ' ... ', 24)"
            : "NULL";
        var searchJoin = useFullTextSearch
            ? "INNER JOIN WorkEntrySearch ON WorkEntrySearch.WorkEntryId = w.Id"
            : string.Empty;
        var sql = new StringBuilder($$"""
            SELECT w.Id, w.WorkDate, w.ClientId, w.ManualClientName, w.TicketId, w.TicketNumberText, w.HasTimeRange, w.StartTime, w.EndTime,
                   w.DurationMinutes, w.Billable, w.Note, w.InternalNote, w.IncludePersonalNoteInWhd, w.Tags, w.FollowUpState, w.FollowUpDueDate,
                   w.WhdPosted, w.WhdPostedAt, w.SagePosted, w.SagePostedAt, w.SageTicketNumber,
                   w.PostingStatus, w.LastError, w.CreatedAt, w.UpdatedAt,
                   COALESCE(NULLIF(w.ManualClientName, ''), c.Name, '') AS ClientName,
                   t.TicketNumber, t.Subject AS TicketSubject,
                   {{snippetExpression}} AS SearchSnippet
            FROM WorkEntries w
            LEFT JOIN Clients c ON c.Id = w.ClientId
            LEFT JOIN Tickets t ON t.Id = w.TicketId
            {{searchJoin}}
            WHERE 1 = 1
            """);

        if (query.StartDate.HasValue)
        {
            sql.Append(" AND w.WorkDate >= $startDate");
            command.Parameters.AddWithValue("$startDate", ToDbDate(query.StartDate.Value));
        }

        if (query.EndDate.HasValue)
        {
            sql.Append(" AND w.WorkDate <= $endDate");
            command.Parameters.AddWithValue("$endDate", ToDbDate(query.EndDate.Value));
        }

        if (query.ClientId.HasValue)
        {
            sql.Append(" AND w.ClientId = $clientId");
            command.Parameters.AddWithValue("$clientId", query.ClientId.Value);
        }

        if (query.TicketId.HasValue)
        {
            sql.Append(" AND w.TicketId = $ticketId");
            command.Parameters.AddWithValue("$ticketId", query.TicketId.Value);
        }

        if (query.ExcludeId.HasValue)
        {
            sql.Append(" AND w.Id <> $excludeId");
            command.Parameters.AddWithValue("$excludeId", query.ExcludeId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.TicketText))
        {
            sql.Append(" AND (w.TicketNumberText LIKE $ticket OR t.TicketNumber LIKE $ticket)");
            command.Parameters.AddWithValue("$ticket", $"%{query.TicketText.Trim()}%");
        }

        if (query.PostingStatus.HasValue)
        {
            sql.Append(" AND w.PostingStatus = $postingStatus");
            command.Parameters.AddWithValue("$postingStatus", query.PostingStatus.Value.ToString());
        }

        if (useFullTextSearch)
        {
            sql.Append(" AND WorkEntrySearch MATCH $fullTextQuery");
            command.Parameters.AddWithValue("$fullTextQuery", fullTextQuery!);
        }
        else if (!string.IsNullOrWhiteSpace(query.Keyword))
        {
            sql.Append("""
                 AND (w.Note LIKE $keyword
                      OR w.InternalNote LIKE $keyword
                      OR w.Tags LIKE $keyword
                      OR w.ManualClientName LIKE $keyword
                      OR c.Name LIKE $keyword
                      OR t.Subject LIKE $keyword
                      OR t.TicketNumber LIKE $keyword
                      OR w.TicketNumberText LIKE $keyword)
                """);
            command.Parameters.AddWithValue("$keyword", $"%{query.Keyword.Trim()}%");
        }

        var requestedTags = WorkEntryTags.Parse(query.Tags);
        for (var index = 0; index < requestedTags.Count; index++)
        {
            var parameterName = $"$tag{index}";
            sql.Append($" AND instr(',' || REPLACE(REPLACE(LOWER(TRIM(w.Tags)), ', ', ','), ' ,', ',') || ',', {parameterName}) > 0");
            command.Parameters.AddWithValue(parameterName, $",{requestedTags[index].ToLowerInvariant()},");
        }

        if (query.FollowUpState.HasValue)
        {
            sql.Append(" AND w.FollowUpState = $followUpState");
            command.Parameters.AddWithValue("$followUpState", query.FollowUpState.Value.ToString());
        }

        if (query.OpenFollowUpsOnly)
        {
            sql.Append(" AND w.FollowUpState IN ('FollowUp', 'Waiting')");
        }

        if (query.PendingWhdOnly)
        {
            sql.Append("""
                 AND w.SagePosted = 0
                 AND (w.TicketId IS NOT NULL OR NULLIF(TRIM(w.TicketNumberText), '') IS NOT NULL)
                 AND (w.WhdPosted = 0
                      OR w.WhdPostedAt IS NULL
                      OR julianday(w.UpdatedAt) > julianday(w.WhdPostedAt) + (1.0 / 86400.0))
                """);
        }

        if (query.PendingSageOnly)
        {
            sql.Append(" AND w.SagePosted = 0 AND w.Billable = 1");
        }

        if (query.PendingAnyOnly)
        {
            sql.Append("""
                 AND ((w.SagePosted = 0
                       AND (w.TicketId IS NOT NULL OR NULLIF(TRIM(w.TicketNumberText), '') IS NOT NULL)
                       AND (w.WhdPosted = 0
                            OR w.WhdPostedAt IS NULL
                            OR julianday(w.UpdatedAt) > julianday(w.WhdPostedAt) + (1.0 / 86400.0)))
                      OR (w.SagePosted = 0 AND w.Billable = 1))
                """);
        }

        sql.Append(" ORDER BY w.WorkDate DESC, w.StartTime DESC, w.Id DESC");
        if (query.MaxResults is > 0)
        {
            sql.Append(" LIMIT $maxResults");
            command.Parameters.AddWithValue("$maxResults", query.MaxResults.Value);
        }

        command.CommandText = sql.ToString();

        using var reader = command.ExecuteReader();
        var entries = new List<WorkEntry>();
        while (reader.Read())
        {
            entries.Add(ReadWorkEntry(reader));
        }

        return entries;
    }

    public IReadOnlyList<string> GetDistinctTags()
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Tags FROM WorkEntries WHERE NULLIF(TRIM(Tags), '') IS NOT NULL ORDER BY Id";

        var tags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            foreach (var tag in WorkEntryTags.Parse(reader.GetString(0)))
            {
                tags.Add(tag);
            }
        }

        return tags.ToArray();
    }

    public WorkEntry? GetWorkEntry(int id)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT w.Id, w.WorkDate, w.ClientId, w.ManualClientName, w.TicketId, w.TicketNumberText, w.HasTimeRange, w.StartTime, w.EndTime,
                   w.DurationMinutes, w.Billable, w.Note, w.InternalNote, w.IncludePersonalNoteInWhd, w.Tags, w.FollowUpState, w.FollowUpDueDate,
                   w.WhdPosted, w.WhdPostedAt, w.SagePosted, w.SagePostedAt, w.SageTicketNumber,
                   w.PostingStatus, w.LastError, w.CreatedAt, w.UpdatedAt,
                   COALESCE(NULLIF(w.ManualClientName, ''), c.Name, '') AS ClientName,
                   t.TicketNumber, t.Subject AS TicketSubject,
                   NULL AS SearchSnippet
            FROM WorkEntries w
            LEFT JOIN Clients c ON c.Id = w.ClientId
            LEFT JOIN Tickets t ON t.Id = w.TicketId
            WHERE w.Id = $id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadWorkEntry(reader) : null;
    }

    public int SaveWorkEntry(WorkEntry entry)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var id = SaveWorkEntry(connection, transaction, entry);
        transaction.Commit();
        return id;
    }

    public int ImportWorkEntries(
        IEnumerable<WorkEntry> entries,
        IReadOnlyDictionary<string, int>? clientAliases = null)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var count = 0;
        foreach (var entry in entries)
        {
            SaveWorkEntry(connection, transaction, entry);
            count++;
        }

        if (clientAliases is not null)
        {
            foreach (var (alias, clientId) in clientAliases)
            {
                if (string.IsNullOrWhiteSpace(alias) || clientId <= 0)
                {
                    continue;
                }

                using var aliasCommand = connection.CreateCommand();
                aliasCommand.Transaction = transaction;
                aliasCommand.CommandText = """
                    INSERT INTO ClientAliases (Alias, ClientId)
                    VALUES ($alias, $clientId)
                    ON CONFLICT(Alias) DO UPDATE SET ClientId = excluded.ClientId
                    """;
                aliasCommand.Parameters.AddWithValue("$alias", alias.Trim());
                aliasCommand.Parameters.AddWithValue("$clientId", clientId);
                aliasCommand.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        return count;
    }

    public void DeleteWorkEntry(int id)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        if (IsSagePosted(connection, transaction, id))
        {
            throw new InvalidOperationException("Entries posted to Sage are permanently locked and cannot be deleted.");
        }

        if (IsWhdPosted(connection, transaction, id))
        {
            throw new InvalidOperationException("Entries synchronized to WHD cannot be deleted because their exact TechNote tracking must be preserved.");
        }

        if (_fullTextSearchAvailable)
        {
            using var searchCommand = connection.CreateCommand();
            searchCommand.Transaction = transaction;
            searchCommand.CommandText = "DELETE FROM WorkEntrySearch WHERE WorkEntryId = $id";
            searchCommand.Parameters.AddWithValue("$id", id);
            searchCommand.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM WorkEntries WHERE Id = $id";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<WorkEntryLink> GetWorkEntryLinks(int workEntryId)
    {
        if (workEntryId <= 0)
        {
            return [];
        }

        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT w.Id, w.WorkDate, w.ClientId, w.ManualClientName, w.TicketId, w.TicketNumberText, w.HasTimeRange, w.StartTime, w.EndTime,
                   w.DurationMinutes, w.Billable, w.Note, w.InternalNote, w.IncludePersonalNoteInWhd, w.Tags, w.FollowUpState, w.FollowUpDueDate,
                   w.WhdPosted, w.WhdPostedAt, w.SagePosted, w.SagePostedAt, w.SageTicketNumber,
                   w.PostingStatus, w.LastError, w.CreatedAt, w.UpdatedAt,
                   COALESCE(NULLIF(w.ManualClientName, ''), c.Name, '') AS ClientName,
                   t.TicketNumber, t.Subject AS TicketSubject, NULL AS SearchSnippet,
                   l.Id, l.SourceWorkEntryId, l.TargetWorkEntryId, l.LinkType, l.CreatedAt
            FROM WorkEntryLinks l
            INNER JOIN WorkEntries w
                ON w.Id = CASE
                    WHEN l.SourceWorkEntryId = $workEntryId THEN l.TargetWorkEntryId
                    ELSE l.SourceWorkEntryId
                END
            LEFT JOIN Clients c ON c.Id = w.ClientId
            LEFT JOIN Tickets t ON t.Id = w.TicketId
            WHERE l.SourceWorkEntryId = $workEntryId OR l.TargetWorkEntryId = $workEntryId
            ORDER BY w.WorkDate DESC, w.Id DESC
            """;
        command.Parameters.AddWithValue("$workEntryId", workEntryId);

        using var reader = command.ExecuteReader();
        var links = new List<WorkEntryLink>();
        while (reader.Read())
        {
            links.Add(new WorkEntryLink
            {
                Id = reader.GetInt32(30),
                SourceWorkEntryId = reader.GetInt32(31),
                TargetWorkEntryId = reader.GetInt32(32),
                CurrentWorkEntryId = workEntryId,
                LinkType = Enum.TryParse<WorkEntryLinkType>(reader.GetString(33), out var type)
                        ? type
                        : WorkEntryLinkType.Related,
                CreatedAt = FromDbDateTime(reader, 34) ?? DateTime.MinValue,
                RelatedEntry = ReadWorkEntry(reader)
            });
        }

        return links;
    }

    public int SaveWorkEntryLink(
        int sourceWorkEntryId,
        int targetWorkEntryId,
        WorkEntryLinkType linkType)
    {
        if (sourceWorkEntryId <= 0 || targetWorkEntryId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceWorkEntryId), "Both linked notes must already be saved.");
        }

        if (sourceWorkEntryId == targetWorkEntryId)
        {
            throw new InvalidOperationException("A note cannot be linked to itself.");
        }

        if (!Enum.IsDefined(linkType))
        {
            throw new ArgumentOutOfRangeException(nameof(linkType));
        }

        if (linkType == WorkEntryLinkType.Related && sourceWorkEntryId > targetWorkEntryId)
        {
            (sourceWorkEntryId, targetWorkEntryId) = (targetWorkEntryId, sourceWorkEntryId);
        }

        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = """
                DELETE FROM WorkEntryLinks
                WHERE (SourceWorkEntryId = $sourceId AND TargetWorkEntryId = $targetId)
                   OR (SourceWorkEntryId = $targetId AND TargetWorkEntryId = $sourceId)
                """;
            deleteCommand.Parameters.AddWithValue("$sourceId", sourceWorkEntryId);
            deleteCommand.Parameters.AddWithValue("$targetId", targetWorkEntryId);
            deleteCommand.ExecuteNonQuery();
        }

        using var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = transaction;
        insertCommand.CommandText = """
            INSERT INTO WorkEntryLinks
                (SourceWorkEntryId, TargetWorkEntryId, LinkType, CreatedAt)
            VALUES
                ($sourceId, $targetId, $linkType, $createdAt);
            SELECT last_insert_rowid();
            """;
        insertCommand.Parameters.AddWithValue("$sourceId", sourceWorkEntryId);
        insertCommand.Parameters.AddWithValue("$targetId", targetWorkEntryId);
        insertCommand.Parameters.AddWithValue("$linkType", linkType.ToString());
        insertCommand.Parameters.AddWithValue("$createdAt", ToDbDateTime(DateTime.Now));
        var id = Convert.ToInt32(insertCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        transaction.Commit();
        return id;
    }

    public void DeleteWorkEntryLink(int linkId)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM WorkEntryLinks WHERE Id = $id";
        command.Parameters.AddWithValue("$id", linkId);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<NoteTemplate> GetTemplates()
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Category, TemplateText
            FROM Templates
            ORDER BY Category COLLATE NOCASE, Name COLLATE NOCASE
            """;

        using var reader = command.ExecuteReader();
        var templates = new List<NoteTemplate>();
        while (reader.Read())
        {
            templates.Add(new NoteTemplate
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Category = reader.GetString(2),
                TemplateText = reader.GetString(3)
            });
        }

        return templates;
    }

    public int SaveTemplate(NoteTemplate template)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        if (template.Id <= 0)
        {
            command.CommandText = """
                INSERT INTO Templates (Name, Category, TemplateText)
                VALUES ($name, $category, $templateText);
                SELECT last_insert_rowid();
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE Templates
                SET Name = $name,
                    Category = $category,
                    TemplateText = $templateText
                WHERE Id = $id;
                SELECT $id;
                """;
            command.Parameters.AddWithValue("$id", template.Id);
        }

        command.Parameters.AddWithValue("$name", template.Name.Trim());
        command.Parameters.AddWithValue("$category", template.Category.Trim());
        command.Parameters.AddWithValue("$templateText", template.TemplateText.Trim());
        var id = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        template.Id = id;
        return id;
    }

    public void DeleteTemplate(int id)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Templates WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public EditorDraft? GetEditorDraft()
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Payload FROM EditorDrafts WHERE Id = 1 LIMIT 1";
        var payload = command.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<EditorDraft>(payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void SaveEditorDraft(EditorDraft draft)
    {
        draft.UpdatedAt = DateTime.Now;
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO EditorDrafts (Id, Payload, UpdatedAt)
            VALUES (1, $payload, $updatedAt)
            ON CONFLICT(Id) DO UPDATE SET
                Payload = excluded.Payload,
                UpdatedAt = excluded.UpdatedAt
            """;
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(draft));
        command.Parameters.AddWithValue("$updatedAt", ToDbDateTime(draft.UpdatedAt));
        command.ExecuteNonQuery();
    }

    public void ClearEditorDraft()
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM EditorDrafts WHERE Id = 1";
        command.ExecuteNonQuery();
    }

    public IReadOnlyDictionary<string, int> GetClientAliases()
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Alias, ClientId FROM ClientAliases ORDER BY Alias COLLATE NOCASE";
        using var reader = command.ExecuteReader();
        var aliases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            aliases[reader.GetString(0)] = reader.GetInt32(1);
        }

        return aliases;
    }

    public void SaveClientAlias(string alias, int clientId)
    {
        var normalizedAlias = alias.Trim();
        if (normalizedAlias.Length == 0 || clientId <= 0)
        {
            return;
        }

        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ClientAliases (Alias, ClientId)
            VALUES ($alias, $clientId)
            ON CONFLICT(Alias) DO UPDATE SET ClientId = excluded.ClientId
            """;
        command.Parameters.AddWithValue("$alias", normalizedAlias);
        command.Parameters.AddWithValue("$clientId", clientId);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<CommonLink> GetCommonLinks()
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Url, SortOrder, BuiltInKey, CreatedAt, UpdatedAt
            FROM CommonLinks
            ORDER BY SortOrder, Name COLLATE NOCASE, Id
            """;

        using var reader = command.ExecuteReader();
        var links = new List<CommonLink>();
        while (reader.Read())
        {
            links.Add(new CommonLink
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                Url = reader.GetString(2),
                SortOrder = reader.GetInt32(3),
                BuiltInKey = reader.IsDBNull(4) ? null : reader.GetString(4),
                CreatedAt = FromDbDateTime(reader, 5) ?? DateTime.MinValue,
                UpdatedAt = FromDbDateTime(reader, 6) ?? DateTime.MinValue
            });
        }

        return links;
    }

    public int SaveCommonLink(CommonLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (string.IsNullOrWhiteSpace(link.Name))
        {
            throw new ArgumentException("A link name is required.", nameof(link));
        }

        if (string.IsNullOrWhiteSpace(link.Url))
        {
            throw new ArgumentException("A link address is required.", nameof(link));
        }

        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        var now = DateTime.Now;

        if (link.Id > 0)
        {
            using (var builtInCommand = connection.CreateCommand())
            {
                builtInCommand.CommandText = "SELECT BuiltInKey FROM CommonLinks WHERE Id = $id";
                builtInCommand.Parameters.AddWithValue("$id", link.Id);
                if (builtInCommand.ExecuteScalar() is string builtInKey
                    && !string.IsNullOrWhiteSpace(builtInKey))
                {
                    throw new InvalidOperationException("Built-in links cannot be changed.");
                }
            }

            using var updateCommand = connection.CreateCommand();
            updateCommand.CommandText = """
                UPDATE CommonLinks
                SET Name = $name,
                    Url = $url,
                    UpdatedAt = $updatedAt
                WHERE Id = $id
                """;
            updateCommand.Parameters.AddWithValue("$id", link.Id);
            updateCommand.Parameters.AddWithValue("$name", link.Name.Trim());
            updateCommand.Parameters.AddWithValue("$url", link.Url.Trim());
            updateCommand.Parameters.AddWithValue("$updatedAt", ToDbDateTime(now));
            if (updateCommand.ExecuteNonQuery() == 0)
            {
                throw new InvalidOperationException("The link no longer exists.");
            }

            link.Name = link.Name.Trim();
            link.Url = link.Url.Trim();
            link.BuiltInKey = null;
            link.UpdatedAt = now;
            return link.Id;
        }

        using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText = """
            INSERT INTO CommonLinks (Name, Url, SortOrder, CreatedAt, UpdatedAt)
            VALUES (
                $name,
                $url,
                COALESCE((SELECT MAX(SortOrder) + 1 FROM CommonLinks), 0),
                $createdAt,
                $updatedAt);
            SELECT last_insert_rowid();
            """;
        insertCommand.Parameters.AddWithValue("$name", link.Name.Trim());
        insertCommand.Parameters.AddWithValue("$url", link.Url.Trim());
        insertCommand.Parameters.AddWithValue("$createdAt", ToDbDateTime(now));
        insertCommand.Parameters.AddWithValue("$updatedAt", ToDbDateTime(now));
        link.Id = Convert.ToInt32(insertCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        link.Name = link.Name.Trim();
        link.Url = link.Url.Trim();
        link.BuiltInKey = null;
        link.CreatedAt = now;
        link.UpdatedAt = now;
        return link.Id;
    }

    public void DeleteCommonLink(int id)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using (var builtInCommand = connection.CreateCommand())
        {
            builtInCommand.CommandText = "SELECT BuiltInKey FROM CommonLinks WHERE Id = $id";
            builtInCommand.Parameters.AddWithValue("$id", id);
            if (builtInCommand.ExecuteScalar() is string builtInKey
                && !string.IsNullOrWhiteSpace(builtInKey))
            {
                throw new InvalidOperationException("Built-in links cannot be removed.");
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM CommonLinks WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public IReadOnlyDictionary<string, string> GetSettings()
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Key, Value FROM Settings";

        using var reader = command.ExecuteReader();
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            settings[reader.GetString(0)] = reader.GetString(1);
        }

        return settings;
    }

    public string GetSetting(string key, string fallback = "")
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Settings WHERE Key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string ?? fallback;
    }

    public void SaveSetting(string key, string value)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Settings (Key, Value)
            VALUES ($key, $value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    public void DeleteSetting(string key)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Settings WHERE Key = $key";
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    public void AddPostingLog(PostingLog log)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO PostingLogs (WorkEntryId, Destination, Payload, Success, Message, ExternalReference, CreatedAt)
            VALUES ($workEntryId, $destination, $payload, $success, $message, $externalReference, $createdAt)
            """;
        command.Parameters.AddWithValue("$workEntryId", log.WorkEntryId);
        command.Parameters.AddWithValue("$destination", log.Destination);
        command.Parameters.AddWithValue("$payload", log.Payload);
        command.Parameters.AddWithValue("$success", log.Success ? 1 : 0);
        command.Parameters.AddWithValue("$message", log.Message);
        command.Parameters.AddWithValue("$externalReference", ToDbText(log.ExternalReference));
        command.Parameters.AddWithValue("$createdAt", ToDbDateTime(log.CreatedAt));
        command.ExecuteNonQuery();
    }

    public PostingLog? GetLatestVerifiedWhdPostingLog(int workEntryId)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, WorkEntryId, Destination, Payload, Success, Message, ExternalReference, CreatedAt
            FROM PostingLogs
            WHERE WorkEntryId = $workEntryId
              AND Destination = 'WHD'
              AND Success = 1
              AND ExternalReference LIKE 'WHD-TECHNOTE-%'
            ORDER BY Id DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$workEntryId", workEntryId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadPostingLog(reader) : null;
    }

    public PostingAttemptStartResult TryBeginPostingAttempt(
        int workEntryId,
        string destination,
        string attemptKey,
        string payloadHash)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var outstanding = GetOutstandingPostingAttempt(connection, transaction, workEntryId, destination);
        if (outstanding is not null)
        {
            transaction.Commit();
            return new PostingAttemptStartResult(false, null, outstanding);
        }

        var startedAt = DateTime.Now;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO PostingAttempts
                (WorkEntryId, Destination, AttemptKey, PayloadHash, Status, Message, StartedAt)
            VALUES
                ($workEntryId, $destination, $attemptKey, $payloadHash, 'Started', 'External posting started.', $startedAt);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$workEntryId", workEntryId);
        command.Parameters.AddWithValue("$destination", destination.Trim());
        command.Parameters.AddWithValue("$attemptKey", attemptKey);
        command.Parameters.AddWithValue("$payloadHash", payloadHash);
        command.Parameters.AddWithValue("$startedAt", ToDbDateTime(startedAt));

        try
        {
            var id = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            transaction.Commit();
            return new PostingAttemptStartResult(true, new PostingAttempt
            {
                Id = id,
                WorkEntryId = workEntryId,
                Destination = destination.Trim(),
                AttemptKey = attemptKey,
                PayloadHash = payloadHash,
                Status = PostingAttemptStatus.Started,
                Message = "External posting started.",
                StartedAt = startedAt
            }, null);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            transaction.Rollback();
            return new PostingAttemptStartResult(
                false,
                null,
                GetOutstandingPostingAttempt(workEntryId, destination));
        }
    }

    public PostingAttempt? GetOutstandingPostingAttempt(int workEntryId, string destination)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        return GetOutstandingPostingAttempt(connection, null, workEntryId, destination);
    }

    public void CompletePostingAttempt(
        int attemptId,
        PostingAttemptStatus status,
        string message,
        string? externalReference = null)
    {
        if (status == PostingAttemptStatus.Started)
        {
            throw new ArgumentException("A completed posting attempt cannot remain Started.", nameof(status));
        }

        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE PostingAttempts
            SET Status = $status,
                Message = $message,
                ExternalReference = $externalReference,
                CompletedAt = $completedAt
            WHERE Id = $id
            """;
        command.Parameters.AddWithValue("$id", attemptId);
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$externalReference", ToDbText(externalReference));
        command.Parameters.AddWithValue("$completedAt", ToDbDateTime(DateTime.Now));
        command.ExecuteNonQuery();
    }

    public int ResolveOutstandingPostingAttempts(
        int workEntryId,
        string destination,
        string message,
        string? externalReference = null)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE PostingAttempts
            SET Status = 'Succeeded',
                Message = $message,
                ExternalReference = COALESCE($externalReference, ExternalReference),
                CompletedAt = $completedAt
            WHERE WorkEntryId = $workEntryId
              AND Destination = $destination
              AND Status IN ('Started', 'Unknown')
            """;
        command.Parameters.AddWithValue("$workEntryId", workEntryId);
        command.Parameters.AddWithValue("$destination", destination.Trim());
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$externalReference", ToDbText(externalReference));
        command.Parameters.AddWithValue("$completedAt", ToDbDateTime(DateTime.Now));
        return command.ExecuteNonQuery();
    }

    public int AbandonOutstandingPostingAttempts(int workEntryId, string destination, string message)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE PostingAttempts
            SET Status = 'Abandoned',
                Message = $message,
                CompletedAt = $completedAt
            WHERE WorkEntryId = $workEntryId
              AND Destination = $destination
              AND Status IN ('Started', 'Unknown')
            """;
        command.Parameters.AddWithValue("$workEntryId", workEntryId);
        command.Parameters.AddWithValue("$destination", destination.Trim());
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$completedAt", ToDbDateTime(DateTime.Now));
        return command.ExecuteNonQuery();
    }

    public bool HasSuccessfulSageDraftLog(int workEntryId)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM PostingLogs
                WHERE WorkEntryId = $workEntryId
                  AND Destination = 'Sage'
                  AND Success = 1)
            """;
        command.Parameters.AddWithValue("$workEntryId", workEntryId);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    public IReadOnlyList<PostingLog> GetPostingLogs(
        string? destination = null,
        bool? success = null,
        string? keyword = null,
        DateTime? startDate = null,
        DateTime? endDate = null,
        int limit = 250)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        var sql = new StringBuilder();
        sql.AppendLine("""
            SELECT Id, WorkEntryId, Destination, Payload, Success, Message, ExternalReference, CreatedAt
            FROM PostingLogs
            WHERE 1 = 1
            """);

        if (!string.IsNullOrWhiteSpace(destination) && !destination.Equals("Any", StringComparison.OrdinalIgnoreCase))
        {
            sql.AppendLine("AND Destination = $destination");
            command.Parameters.AddWithValue("$destination", destination.Trim());
        }

        if (success.HasValue)
        {
            sql.AppendLine("AND Success = $success");
            command.Parameters.AddWithValue("$success", success.Value ? 1 : 0);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            sql.AppendLine("""
                AND (
                    Message LIKE $keyword
                    OR Payload LIKE $keyword
                    OR Destination LIKE $keyword
                    OR ExternalReference LIKE $keyword
                    OR CAST(WorkEntryId AS TEXT) LIKE $keyword
                )
                """);
            command.Parameters.AddWithValue("$keyword", $"%{keyword.Trim()}%");
        }

        if (startDate.HasValue)
        {
            sql.AppendLine("AND CreatedAt >= $startDate");
            command.Parameters.AddWithValue("$startDate", ToDbDateTime(startDate.Value.Date));
        }

        if (endDate.HasValue)
        {
            sql.AppendLine("AND CreatedAt < $endDateExclusive");
            command.Parameters.AddWithValue("$endDateExclusive", ToDbDateTime(endDate.Value.Date.AddDays(1)));
        }

        sql.AppendLine("ORDER BY CreatedAt DESC, Id DESC");
        sql.AppendLine("LIMIT $limit");
        command.CommandText = sql.ToString();
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));

        var logs = new List<PostingLog>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            logs.Add(ReadPostingLog(reader));
        }

        return logs;
    }

    public static void UpdatePostingStatus(WorkEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.LastError))
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
                && (entry.ClientId is > 0 || !string.IsNullOrWhiteSpace(entry.ManualClientName))
                && !string.IsNullOrWhiteSpace(entry.Note) => PostingStatus.Ready,
            _ => PostingStatus.Draft
        };
    }

    private static void EnsureClientSyncColumns(SqliteConnection connection)
    {
        EnsureColumn(connection, "Clients", "LastSyncedAt", "TEXT NULL");
        EnsureColumn(connection, "Clients", "WhdLocationName", "TEXT NULL");
        EnsureColumn(connection, "Clients", "WhdContactName", "TEXT NULL");
        EnsureColumn(connection, "Clients", "SageCustomerId", "TEXT NULL");
        EnsureColumn(connection, "Clients", "SageCustomerName", "TEXT NULL");
        EnsureColumn(connection, "Clients", "SageContactName", "TEXT NULL");
        EnsureColumn(connection, "Clients", "SageTelephone", "TEXT NULL");
        EnsureColumn(connection, "Clients", "MatchStatus", "TEXT NOT NULL DEFAULT 'Unmatched'");
    }

    private static void EnsureSageVerificationColumns(SqliteConnection connection)
    {
        EnsureColumn(connection, "WorkEntries", "SageTicketNumber", "TEXT NULL");
        EnsureColumn(connection, "PostingLogs", "ExternalReference", "TEXT NULL");
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE INDEX IF NOT EXISTS IX_WorkEntries_SageTicketNumber ON WorkEntries(SageTicketNumber)";
        command.ExecuteNonQuery();
    }

    private static void EnsurePersonalNotePostingColumn(SqliteConnection connection)
    {
        EnsureColumn(connection, "WorkEntries", "IncludePersonalNoteInWhd", "INTEGER NOT NULL DEFAULT 0");
    }

    private static void EnsurePostingAttemptSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS PostingAttempts (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                WorkEntryId INTEGER NOT NULL,
                Destination TEXT NOT NULL,
                AttemptKey TEXT NOT NULL UNIQUE,
                PayloadHash TEXT NOT NULL,
                Status TEXT NOT NULL,
                Message TEXT NOT NULL DEFAULT '',
                ExternalReference TEXT NULL,
                StartedAt TEXT NOT NULL,
                CompletedAt TEXT NULL,
                FOREIGN KEY (WorkEntryId) REFERENCES WorkEntries(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_PostingAttempts_WorkEntryDestination
                ON PostingAttempts(WorkEntryId, Destination, StartedAt DESC);
            CREATE UNIQUE INDEX IF NOT EXISTS UX_PostingAttempts_ActiveDestination
                ON PostingAttempts(WorkEntryId, Destination)
                WHERE Status IN ('Started', 'Unknown');
            """;
        command.ExecuteNonQuery();
    }

    private static void RecoverInterruptedPostingAttempts(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE PostingAttempts
            SET Status = 'Unknown',
                Message = CASE
                    WHEN TRIM(Message) = '' THEN 'TechBench exited before the external result was recorded.'
                    ELSE Message || ' TechBench exited before the external result was recorded.'
                END,
                CompletedAt = $completedAt
            WHERE Status = 'Started'
            """;
        command.Parameters.AddWithValue("$completedAt", ToDbDateTime(DateTime.Now));
        command.ExecuteNonQuery();
    }

    private static PostingAttempt? GetOutstandingPostingAttempt(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        int workEntryId,
        string destination)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT Id, WorkEntryId, Destination, AttemptKey, PayloadHash, Status, Message,
                   ExternalReference, StartedAt, CompletedAt
            FROM PostingAttempts
            WHERE WorkEntryId = $workEntryId
              AND Destination = $destination
              AND Status IN ('Started', 'Unknown')
            ORDER BY StartedAt DESC, Id DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$workEntryId", workEntryId);
        command.Parameters.AddWithValue("$destination", destination.Trim());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new PostingAttempt
        {
            Id = reader.GetInt32(0),
            WorkEntryId = reader.GetInt32(1),
            Destination = reader.GetString(2),
            AttemptKey = reader.GetString(3),
            PayloadHash = reader.GetString(4),
            Status = Enum.TryParse<PostingAttemptStatus>(reader.GetString(5), out var status)
                ? status
                : PostingAttemptStatus.Unknown,
            Message = reader.GetString(6),
            ExternalReference = reader.IsDBNull(7) ? null : reader.GetString(7),
            StartedAt = FromDbDateTime(reader, 8) ?? DateTime.MinValue,
            CompletedAt = FromDbDateTime(reader, 9)
        };
    }

    private static void BackfillSageTicketNumbers(SqliteConnection connection)
    {
        using (var repairCommand = connection.CreateCommand())
        {
            repairCommand.CommandText = """
                UPDATE WorkEntries
                SET SageTicketNumber = NULL
                WHERE SageTicketNumber IS NOT NULL
                  AND trim(SageTicketNumber) <> ''
                  AND SageTicketNumber GLOB '*[^0-9]*'
                """;
            repairCommand.ExecuteNonQuery();
        }

        using var readCommand = connection.CreateCommand();
        readCommand.CommandText = """
            SELECT p.WorkEntryId, p.Message
            FROM PostingLogs p
            INNER JOIN WorkEntries w ON w.Id = p.WorkEntryId
            WHERE p.Destination = 'Sage'
              AND p.Success = 1
              AND (w.SageTicketNumber IS NULL OR trim(w.SageTicketNumber) = '')
            ORDER BY p.CreatedAt DESC, p.Id DESC
            """;

        var recovered = new Dictionary<int, string>();
        using (var reader = readCommand.ExecuteReader())
        {
            while (reader.Read())
            {
                var entryId = reader.GetInt32(0);
                if (recovered.ContainsKey(entryId))
                {
                    continue;
                }

                var message = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                if (TryExtractSageTicketNumber(message, out var ticketNumber))
                {
                    recovered[entryId] = ticketNumber;
                }
            }
        }

        foreach (var (entryId, ticketNumber) in recovered)
        {
            using var updateCommand = connection.CreateCommand();
            updateCommand.CommandText = "UPDATE WorkEntries SET SageTicketNumber = $ticketNumber WHERE Id = $entryId";
            updateCommand.Parameters.AddWithValue("$ticketNumber", ticketNumber);
            updateCommand.Parameters.AddWithValue("$entryId", entryId);
            updateCommand.ExecuteNonQuery();
        }
    }

    internal static bool TryExtractSageTicketNumber(string message, out string ticketNumber)
    {
        var match = Regex.Match(
            message ?? string.Empty,
            @"\bSage(?:\s+time)?\s+ticket\s+#([0-9]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        ticketNumber = match.Success ? match.Groups[1].Value : string.Empty;
        return match.Success;
    }

    private static void EnsureTicketStatusSchema(SqliteConnection connection)
    {
        EnsureColumn(connection, "Tickets", "WhdStatusTypeId", "INTEGER NULL");

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS TicketStatusOptions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Source TEXT NOT NULL DEFAULT 'WHD',
                ExternalId TEXT NULL,
                WhdStatusTypeId INTEGER NULL,
                IsClosed INTEGER NOT NULL DEFAULT 0,
                LastSyncedAt TEXT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS IX_TicketStatusOptions_SourceExternalId
                ON TicketStatusOptions(Source, ExternalId);
            CREATE INDEX IF NOT EXISTS IX_TicketStatusOptions_Name
                ON TicketStatusOptions(Name);
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureWorkEntryTimeRangeColumn(SqliteConnection connection)
    {
        if (!ColumnExists(connection, "WorkEntries", "HasTimeRange"))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "ALTER TABLE WorkEntries ADD COLUMN HasTimeRange INTEGER NOT NULL DEFAULT 1";
            command.ExecuteNonQuery();
        }
    }

    private void EnsureNoteTakingSchema(SqliteConnection connection)
    {
        EnsureColumn(connection, "WorkEntries", "Tags", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "WorkEntries", "FollowUpState", "TEXT NOT NULL DEFAULT 'None'");
        EnsureColumn(connection, "WorkEntries", "FollowUpDueDate", "TEXT NULL");

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS EditorDrafts (
                    Id INTEGER PRIMARY KEY CHECK (Id = 1),
                    Payload TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS ClientAliases (
                    Alias TEXT PRIMARY KEY COLLATE NOCASE,
                    ClientId INTEGER NOT NULL,
                    FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS IX_WorkEntries_FollowUpState
                    ON WorkEntries(FollowUpState, FollowUpDueDate);
                CREATE INDEX IF NOT EXISTS IX_ClientAliases_ClientId
                    ON ClientAliases(ClientId);
                """;
            command.ExecuteNonQuery();
        }

        try
        {
            using var searchCommand = connection.CreateCommand();
            searchCommand.CommandText = """
                CREATE VIRTUAL TABLE IF NOT EXISTS WorkEntrySearch USING fts5(
                    WorkEntryId UNINDEXED,
                    Note,
                    InternalNote,
                    ClientName,
                    TicketText,
                    Tags,
                    tokenize = 'unicode61 remove_diacritics 2'
                )
                """;
            searchCommand.ExecuteNonQuery();
            _fullTextSearchAvailable = true;
        }
        catch (SqliteException)
        {
            _fullTextSearchAvailable = false;
        }
    }

    private static void EnsureWorkEntryLinkSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS WorkEntryLinks (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SourceWorkEntryId INTEGER NOT NULL,
                TargetWorkEntryId INTEGER NOT NULL,
                LinkType TEXT NOT NULL DEFAULT 'Related',
                CreatedAt TEXT NOT NULL,
                CHECK (SourceWorkEntryId <> TargetWorkEntryId),
                CHECK (LinkType IN ('Related', 'FollowUpTo')),
                FOREIGN KEY (SourceWorkEntryId) REFERENCES WorkEntries(Id) ON DELETE CASCADE,
                FOREIGN KEY (TargetWorkEntryId) REFERENCES WorkEntries(Id) ON DELETE CASCADE
            );

            CREATE UNIQUE INDEX IF NOT EXISTS UX_WorkEntryLinks_Pair
                ON WorkEntryLinks(
                    CASE WHEN SourceWorkEntryId < TargetWorkEntryId THEN SourceWorkEntryId ELSE TargetWorkEntryId END,
                    CASE WHEN SourceWorkEntryId < TargetWorkEntryId THEN TargetWorkEntryId ELSE SourceWorkEntryId END);
            CREATE INDEX IF NOT EXISTS IX_WorkEntryLinks_Source ON WorkEntryLinks(SourceWorkEntryId);
            CREATE INDEX IF NOT EXISTS IX_WorkEntryLinks_Target ON WorkEntryLinks(TargetWorkEntryId);
            """;
        command.ExecuteNonQuery();
    }

    private static void EnsureCommonLinkSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS CommonLinks (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Url TEXT NOT NULL,
                SortOrder INTEGER NOT NULL DEFAULT 0,
                BuiltInKey TEXT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        EnsureColumn(connection, "CommonLinks", "BuiltInKey", "TEXT NULL");

        using var migrationCommand = connection.CreateCommand();
        migrationCommand.CommandText = """
            UPDATE CommonLinks
            SET BuiltInKey = 'watchguard-cloud'
            WHERE BuiltInKey IS NULL
              AND Url = 'https://cloud.watchguard.com/' COLLATE NOCASE;

            UPDATE CommonLinks
            SET BuiltInKey = 'microsoft-365-admin'
            WHERE BuiltInKey IS NULL
              AND Url = 'https://admin.microsoft.com/' COLLATE NOCASE;

            UPDATE CommonLinks
            SET BuiltInKey = 'barracuda-cloud-control'
            WHERE BuiltInKey IS NULL
              AND Url = 'https://login.barracuda.com/' COLLATE NOCASE;

            UPDATE CommonLinks
            SET BuiltInKey = 'eset-protect'
            WHERE BuiltInKey IS NULL
              AND Url = 'https://protect.eset.com/' COLLATE NOCASE;

            UPDATE CommonLinks
            SET BuiltInKey = 'email2phone'
            WHERE BuiltInKey IS NULL
              AND Url = 'https://user.email2phone.net/client/#/authentication/signin' COLLATE NOCASE;

            UPDATE CommonLinks
            SET BuiltInKey = 'godaddy-dns'
            WHERE BuiltInKey IS NULL
              AND Url = 'https://dcc.godaddy.com/control/portfolio' COLLATE NOCASE;

            UPDATE CommonLinks
            SET BuiltInKey = 'network-solutions-dns'
            WHERE BuiltInKey IS NULL
              AND Url = 'https://www.networksolutions.com/my-account/login' COLLATE NOCASE;

            CREATE UNIQUE INDEX IF NOT EXISTS UX_CommonLinks_Url
                ON CommonLinks(Url COLLATE NOCASE);
            CREATE UNIQUE INDEX IF NOT EXISTS UX_CommonLinks_BuiltInKey
                ON CommonLinks(BuiltInKey)
                WHERE BuiltInKey IS NOT NULL;
            CREATE INDEX IF NOT EXISTS IX_CommonLinks_SortOrder
                ON CommonLinks(SortOrder, Name COLLATE NOCASE);
            """;
        migrationCommand.ExecuteNonQuery();
    }

    private void RebuildWorkEntrySearchIndex(SqliteConnection connection)
    {
        if (!_fullTextSearchAvailable)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM WorkEntrySearch";
            deleteCommand.ExecuteNonQuery();
        }

        using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO WorkEntrySearch (WorkEntryId, Note, InternalNote, ClientName, TicketText, Tags)
                SELECT w.Id,
                       w.Note,
                       COALESCE(w.InternalNote, ''),
                       COALESCE(NULLIF(w.ManualClientName, ''), c.Name, ''),
                       TRIM(COALESCE(t.TicketNumber, '') || ' ' || COALESCE(t.Subject, '') || ' ' || COALESCE(w.TicketNumberText, '')),
                       COALESCE(w.Tags, '')
                FROM WorkEntries w
                LEFT JOIN Clients c ON c.Id = w.ClientId
                LEFT JOIN Tickets t ON t.Id = w.TicketId
                """;
            insertCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void EnsureWorkEntryClientReferenceSchema(SqliteConnection connection)
    {
        var hasManualClientName = ColumnExists(connection, "WorkEntries", "ManualClientName");
        var clientIdIsRequired = ColumnIsNotNull(connection, "WorkEntries", "ClientId");

        if (!clientIdIsRequired)
        {
            if (!hasManualClientName)
            {
                using var addColumnCommand = connection.CreateCommand();
                addColumnCommand.CommandText = "ALTER TABLE WorkEntries ADD COLUMN ManualClientName TEXT NULL";
                addColumnCommand.ExecuteNonQuery();
            }

            return;
        }

        var manualClientSelect = hasManualClientName ? "ManualClientName" : "NULL";

        using (var disableForeignKeysCommand = connection.CreateCommand())
        {
            disableForeignKeysCommand.CommandText = "PRAGMA foreign_keys = OFF";
            disableForeignKeysCommand.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                CREATE TABLE WorkEntries_Migrated (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    WorkDate TEXT NOT NULL,
                    ClientId INTEGER NULL,
                    ManualClientName TEXT NULL,
                    TicketId INTEGER NULL,
                    TicketNumberText TEXT NULL,
                    HasTimeRange INTEGER NOT NULL DEFAULT 1,
                    StartTime TEXT NOT NULL,
                    EndTime TEXT NOT NULL,
                    DurationMinutes INTEGER NOT NULL,
                    Billable INTEGER NOT NULL DEFAULT 1,
                    Note TEXT NOT NULL DEFAULT '',
                    InternalNote TEXT NULL,
                    WhdPosted INTEGER NOT NULL DEFAULT 0,
                    WhdPostedAt TEXT NULL,
                    SagePosted INTEGER NOT NULL DEFAULT 0,
                    SagePostedAt TEXT NULL,
                    PostingStatus TEXT NOT NULL DEFAULT 'Draft',
                    LastError TEXT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    FOREIGN KEY (ClientId) REFERENCES Clients(Id),
                    FOREIGN KEY (TicketId) REFERENCES Tickets(Id)
                );

                INSERT INTO WorkEntries_Migrated
                    (Id, WorkDate, ClientId, ManualClientName, TicketId, TicketNumberText, HasTimeRange, StartTime, EndTime,
                     DurationMinutes, Billable, Note, InternalNote, WhdPosted, WhdPostedAt, SagePosted, SagePostedAt,
                     PostingStatus, LastError, CreatedAt, UpdatedAt)
                SELECT Id, WorkDate, ClientId, {manualClientSelect}, TicketId, TicketNumberText, HasTimeRange, StartTime, EndTime,
                       DurationMinutes, Billable, Note, InternalNote, WhdPosted, WhdPostedAt, SagePosted, SagePostedAt,
                       PostingStatus, LastError, CreatedAt, UpdatedAt
                FROM WorkEntries;

                DROP TABLE WorkEntries;
                ALTER TABLE WorkEntries_Migrated RENAME TO WorkEntries;
                """;
            command.ExecuteNonQuery();
        }

        using (var enableForeignKeysCommand = connection.CreateCommand())
        {
            enableForeignKeysCommand.CommandText = "PRAGMA foreign_keys = ON";
            enableForeignKeysCommand.ExecuteNonQuery();
        }

        RecreateWorkEntryIndexes(connection);
    }

    private static void BackfillClientSyncMetadata(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Clients
            SET Source = CASE
                    WHEN Source IN ('WHD', 'Sage', 'Both') THEN Source
                    ELSE 'WHD'
                END,
                ExternalId = CASE
                    WHEN Source IN ('WHD', 'Both') THEN COALESCE(NULLIF(ExternalId, ''), 'WHD-' || printf('%04d', Id))
                    ELSE NULLIF(ExternalId, '')
                END,
                LastSyncedAt = COALESCE(LastSyncedAt, $lastSyncedAt)
            """;
        command.Parameters.AddWithValue("$lastSyncedAt", ToDbDateTime(DateTime.Now));
        command.ExecuteNonQuery();
    }

    private static void RepairSageOnlyClientMetadata(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Clients
            SET ExternalId = NULL,
                WhdLocationName = NULL,
                WhdContactName = NULL,
                MatchStatus = 'Unmatched'
            WHERE Source = 'Sage'
              AND SageCustomerId IS NOT NULL
              AND TRIM(SageCustomerId) <> ''
              AND (
                    ExternalId LIKE 'WHD-%'
                    OR MatchStatus = 'Matched'
                  )
            """;
        command.ExecuteNonQuery();

        using var bothCommand = connection.CreateCommand();
        bothCommand.CommandText = """
            UPDATE Clients
            SET Source = 'Sage',
                ExternalId = NULL,
                MatchStatus = 'Unmatched'
            WHERE Source = 'Both'
              AND SageCustomerId IS NOT NULL
              AND TRIM(SageCustomerId) <> ''
              AND ExternalId GLOB 'WHD-[0-9][0-9][0-9][0-9]'
              AND COALESCE(NULLIF(TRIM(WhdLocationName), ''), NULLIF(TRIM(WhdContactName), '')) IS NULL
            """;
        bothCommand.ExecuteNonQuery();
    }

    private static void BackfillClientMatchMetadata(SqliteConnection connection)
    {
        using var selectCommand = connection.CreateCommand();
        selectCommand.CommandText = $"""
            SELECT {ClientSelectColumns}
            FROM Clients
            """;

        var clients = new List<Client>();
        using (var reader = selectCommand.ExecuteReader())
        {
            while (reader.Read())
            {
                clients.Add(ReadClient(reader));
            }
        }

        foreach (var client in clients)
        {
            var changed = false;
            if ((client.Source.Equals("WHD", StringComparison.OrdinalIgnoreCase)
                    || client.Source.Equals("Both", StringComparison.OrdinalIgnoreCase))
                && string.IsNullOrWhiteSpace(client.WhdLocationName))
            {
                SplitWhdDisplayName(client.Name, out var locationName, out var contactName);
                client.WhdLocationName = locationName;
                client.WhdContactName = string.IsNullOrWhiteSpace(client.WhdContactName) ? contactName : client.WhdContactName;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(client.MatchStatus))
            {
                client.MatchStatus = ResolveMatchStatus(client);
                changed = true;
            }

            var displayName = BuildClientDisplayName(client);
            if (!string.Equals(client.Name, displayName, StringComparison.Ordinal))
            {
                client.Name = displayName;
                changed = true;
            }

            if (!changed)
            {
                continue;
            }

            using var updateCommand = connection.CreateCommand();
            updateCommand.CommandText = """
                UPDATE Clients
                SET Name = $name,
                    WhdLocationName = $whdLocationName,
                    WhdContactName = $whdContactName,
                    MatchStatus = $matchStatus
                WHERE Id = $id
                """;
            updateCommand.Parameters.AddWithValue("$id", client.Id);
            updateCommand.Parameters.AddWithValue("$name", client.Name);
            updateCommand.Parameters.AddWithValue("$whdLocationName", ToDbText(client.WhdLocationName));
            updateCommand.Parameters.AddWithValue("$whdContactName", ToDbText(client.WhdContactName));
            updateCommand.Parameters.AddWithValue("$matchStatus", client.MatchStatus);
            updateCommand.ExecuteNonQuery();
        }
    }

    private void Seed(SqliteConnection connection)
    {
        SeedTemplates(connection);
        SeedCommonLinks(connection);
        SeedSetting(connection, "Whd.AutoSyncEnabled", "true");
        SeedSetting(connection, "Whd.AutoSyncMinutes", "5");
        SeedSetting(connection, "Theme", "Dark");
    }

    private static void ApplyMockModeRemovalMigration(SqliteConnection connection)
    {
        using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "SELECT 1 FROM Settings WHERE Key = 'Posting.MockModesRemovedV2' LIMIT 1";
        if (checkCommand.ExecuteScalar() is not null)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM Settings
            WHERE Key IN ('Whd.MockMode', 'Sage.MockMode');

            UPDATE WorkEntries
            SET WhdPosted = 0,
                WhdPostedAt = NULL
            WHERE WhdPosted = 1
              AND EXISTS (
                  SELECT 1
                  FROM PostingLogs p
                  WHERE p.WorkEntryId = WorkEntries.Id
                    AND p.Destination = 'WHD'
                    AND p.Success = 1
                    AND (p.ExternalReference LIKE 'MOCK-WHD-%'
                         OR p.Message LIKE 'Mock Web Help Desk post%')
              )
              AND NOT EXISTS (
                  SELECT 1
                  FROM PostingLogs p
                  WHERE p.WorkEntryId = WorkEntries.Id
                    AND p.Destination = 'WHD'
                    AND p.Success = 1
                    AND (p.ExternalReference IS NULL OR p.ExternalReference NOT LIKE 'MOCK-WHD-%')
                    AND p.Message NOT LIKE 'Mock Web Help Desk post%'
              );

            UPDATE WorkEntries
            SET SagePosted = 0,
                SagePostedAt = NULL,
                SageTicketNumber = NULL
            WHERE SagePosted = 1
              AND EXISTS (
                  SELECT 1
                  FROM PostingLogs p
                  WHERE p.WorkEntryId = WorkEntries.Id
                    AND p.Destination = 'Sage'
                    AND p.Success = 1
                    AND (p.ExternalReference LIKE 'MOCK-SAGE-%'
                         OR p.Message LIKE 'Mock Sage post%')
              )
              AND NOT EXISTS (
                  SELECT 1
                  FROM PostingLogs p
                  WHERE p.WorkEntryId = WorkEntries.Id
                    AND p.Destination = 'Sage'
                    AND p.Success = 1
                    AND (p.ExternalReference IS NULL OR p.ExternalReference NOT LIKE 'MOCK-SAGE-%')
                    AND p.Message NOT LIKE 'Mock Sage post%'
              );

            UPDATE WorkEntries
            SET PostingStatus = CASE
                    WHEN LastError IS NOT NULL AND trim(LastError) <> '' THEN 'Failed'
                    WHEN WhdPosted = 1 AND SagePosted = 1 THEN 'PostedToBoth'
                    WHEN WhdPosted = 1 THEN 'PostedToWhd'
                    WHEN SagePosted = 1 THEN 'PostedToSage'
                    WHEN DurationMinutes > 0
                         AND (ClientId IS NOT NULL OR (ManualClientName IS NOT NULL AND trim(ManualClientName) <> ''))
                         AND Note IS NOT NULL AND trim(Note) <> '' THEN 'Ready'
                    ELSE 'Draft'
                END;

            INSERT INTO Settings (Key, Value)
            VALUES ('Posting.MockModesRemovedV2', 'true');
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void RemoveSeedDemoData(SqliteConnection connection)
    {
        using var workEntryCommand = connection.CreateCommand();
        workEntryCommand.CommandText = """
            DELETE FROM WorkEntries
            WHERE Note IN (
                'Reviewed shared mailbox permissions, corrected group membership, and confirmed Outlook access with the user.',
                'Validated VPN authentication after password reset and documented the client-side reconnect steps.',
                'Checked overnight backup jobs, reviewed warnings, and sent verification summary.'
            )
               OR ClientId IN (
                    SELECT Id
                    FROM Clients
                    WHERE Name IN ('Contoso Manufacturing', 'Northwind Medical', 'Alpine Security', 'Blue Ridge Law')
                  )
            """;
        workEntryCommand.ExecuteNonQuery();

        using var ticketCommand = connection.CreateCommand();
        ticketCommand.CommandText = """
            DELETE FROM Tickets
            WHERE (
                    (TicketNumber = 'WHD-10421' AND Subject = 'Microsoft 365 shared mailbox access')
                    OR (TicketNumber = 'WHD-10437' AND Subject = 'Firewall rule review')
                    OR (TicketNumber = 'WHD-22018' AND Subject = 'VPN connectivity after password reset')
                    OR (TicketNumber = 'WHD-31004' AND Subject = 'Backup verification report')
                    OR (TicketNumber = 'WHD-41112' AND Subject = 'Server maintenance window')
                  )
               OR ClientId IN (
                    SELECT Id
                    FROM Clients
                    WHERE Name IN ('Contoso Manufacturing', 'Northwind Medical', 'Alpine Security', 'Blue Ridge Law')
                  )
            """;
        ticketCommand.ExecuteNonQuery();

        using var clientCommand = connection.CreateCommand();
        clientCommand.CommandText = """
            DELETE FROM Clients
            WHERE Name IN ('Contoso Manufacturing', 'Northwind Medical', 'Alpine Security', 'Blue Ridge Law')
              AND Id NOT IN (SELECT ClientId FROM Tickets)
              AND Id NOT IN (SELECT ClientId FROM WorkEntries WHERE ClientId IS NOT NULL)
            """;
        clientCommand.ExecuteNonQuery();
    }

    private static void SeedCommonLinks(SqliteConnection connection)
    {
        var now = ToDbDateTime(DateTime.Now);
        var defaults = new[]
        {
            ("watchguard-cloud", "WatchGuard Cloud", "https://cloud.watchguard.com/", 0),
            ("microsoft-365-admin", "Microsoft 365 Admin Center", "https://admin.microsoft.com/", 1),
            ("barracuda-cloud-control", "Barracuda Cloud Control", "https://login.barracuda.com/", 2),
            ("eset-protect", "ESET PROTECT Console", "https://protect.eset.com/", 3),
            ("email2phone", "Email2Phone", "https://user.email2phone.net/client/#/authentication/signin", 4),
            ("godaddy-dns", "GoDaddy", "https://dcc.godaddy.com/control/portfolio", 10),
            ("network-solutions-dns", "Network Solutions", "https://www.networksolutions.com/my-account/login", 11)
        };

        using var transaction = connection.BeginTransaction();
        foreach (var link in defaults)
        {
            using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT OR IGNORE INTO CommonLinks
                    (Name, Url, SortOrder, BuiltInKey, CreatedAt, UpdatedAt)
                VALUES
                    ($name, $url, $sortOrder, $builtInKey, $createdAt, $updatedAt);

                UPDATE CommonLinks
                SET Name = $name,
                    Url = $url,
                    SortOrder = $sortOrder,
                    BuiltInKey = $builtInKey,
                    UpdatedAt = $updatedAt
                WHERE (BuiltInKey = $builtInKey OR Url = $url COLLATE NOCASE)
                  AND (Name <> $name
                       OR Url <> $url COLLATE NOCASE
                       OR SortOrder <> $sortOrder
                       OR BuiltInKey IS NULL)
                """;
            insertCommand.Parameters.AddWithValue("$builtInKey", link.Item1);
            insertCommand.Parameters.AddWithValue("$name", link.Item2);
            insertCommand.Parameters.AddWithValue("$url", link.Item3);
            insertCommand.Parameters.AddWithValue("$sortOrder", link.Item4);
            insertCommand.Parameters.AddWithValue("$createdAt", now);
            insertCommand.Parameters.AddWithValue("$updatedAt", now);
            insertCommand.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void SeedTemplates(SqliteConnection connection)
    {
        if (CountRows(connection, "Templates") > 0)
        {
            return;
        }

        var templates = new[]
        {
            ("Exchange certificate update", "Microsoft 365", "Updated Exchange certificate binding, verified mail flow, and confirmed Outlook connectivity."),
            ("VPN troubleshooting", "Network", "Investigated VPN connection failure, validated credentials and MFA status, reviewed client logs, and confirmed successful reconnect."),
            ("Microsoft 365 licensing", "Microsoft 365", "Reviewed Microsoft 365 license assignment, adjusted user licensing, and confirmed service availability."),
            ("Firewall rule change", "Network", "Reviewed requested firewall rule change, validated source and destination scope, applied the rule, and confirmed expected traffic."),
            ("Password reset", "Help Desk", "Reset user password, confirmed MFA status, and verified successful sign-in with the user."),
            ("Backup verification", "Infrastructure", "Reviewed backup job status, checked warnings or failures, and documented restore-point availability."),
            ("Server reboot/maintenance", "Infrastructure", "Performed scheduled server maintenance, rebooted services as needed, and verified post-maintenance availability.")
        };

        foreach (var template in templates)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Templates (Name, Category, TemplateText)
                VALUES ($name, $category, $templateText)
                """;
            command.Parameters.AddWithValue("$name", template.Item1);
            command.Parameters.AddWithValue("$category", template.Item2);
            command.Parameters.AddWithValue("$templateText", template.Item3);
            command.ExecuteNonQuery();
        }
    }

    private static void SeedSetting(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Settings (Key, Value)
            VALUES ($key, $value)
            ON CONFLICT(Key) DO NOTHING
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static long CountRows(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName}";
        return (long)command.ExecuteScalar()!;
    }

    private static bool ColumnExists(SqliteConnection connection, string tableName, string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void EnsureColumn(SqliteConnection connection, string tableName, string columnName, string definition)
    {
        if (ColumnExists(connection, tableName, columnName))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition}";
        command.ExecuteNonQuery();
    }

    private static bool ColumnIsNotNull(SqliteConnection connection, string tableName, string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return reader.GetInt32(3) == 1;
            }
        }

        return false;
    }

    private static int FindTicketStatusOptionId(SqliteConnection connection, TicketStatusOption option)
    {
        using var command = connection.CreateCommand();
        var source = string.IsNullOrWhiteSpace(option.Source) ? "WHD" : option.Source.Trim();
        var name = string.IsNullOrWhiteSpace(option.Name) ? "Open" : option.Name.Trim();

        if (!string.IsNullOrWhiteSpace(option.ExternalId))
        {
            command.CommandText = """
                SELECT Id
                FROM TicketStatusOptions
                WHERE Source = $source AND ExternalId = $externalId
                """;
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$externalId", option.ExternalId.Trim());
        }
        else if (option.WhdStatusTypeId.HasValue)
        {
            command.CommandText = """
                SELECT Id
                FROM TicketStatusOptions
                WHERE Source = $source AND WhdStatusTypeId = $whdStatusTypeId
                """;
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$whdStatusTypeId", option.WhdStatusTypeId.Value);
        }
        else
        {
            command.CommandText = """
                SELECT Id
                FROM TicketStatusOptions
                WHERE Source = $source AND Name = $name
                """;
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$name", name);
        }

        return command.ExecuteScalar() is long id ? Convert.ToInt32(id, CultureInfo.InvariantCulture) : 0;
    }

    private static void RecreateWorkEntryIndexes(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE INDEX IF NOT EXISTS IX_WorkEntries_WorkDate ON WorkEntries(WorkDate);
            CREATE INDEX IF NOT EXISTS IX_WorkEntries_ClientId ON WorkEntries(ClientId);
            CREATE INDEX IF NOT EXISTS IX_WorkEntries_TicketId ON WorkEntries(TicketId);
            CREATE INDEX IF NOT EXISTS IX_WorkEntries_PostingStatus ON WorkEntries(PostingStatus);
            """;
        command.ExecuteNonQuery();
    }

    private static List<Client> ReadAllClients(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {ClientSelectColumns} FROM Clients";
        using var reader = command.ExecuteReader();
        var clients = new List<Client>();
        while (reader.Read())
        {
            clients.Add(ReadClient(reader));
        }

        return clients;
    }

    private static List<Ticket> ReadAllTickets(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT Id, TicketNumber, ClientId, Subject, Status, Source, ExternalId, WhdStatusTypeId, IsClosed, LastSyncedAt
            FROM Tickets
            """;
        using var reader = command.ExecuteReader();
        var tickets = new List<Ticket>();
        while (reader.Read())
        {
            tickets.Add(ReadTicket(reader));
        }

        return tickets;
    }

    private static Client? FindClientForSync(IReadOnlyList<Client> clients, Client incoming)
    {
        var normalizedSource = NormalizeClientSource(incoming.Source);
        if (!string.IsNullOrWhiteSpace(incoming.ExternalId) && normalizedSource == "WHD")
        {
            var externalMatch = clients.FirstOrDefault(candidate =>
                (candidate.Source.Equals("WHD", StringComparison.OrdinalIgnoreCase)
                    || candidate.Source.Equals("Both", StringComparison.OrdinalIgnoreCase))
                && ContainsExternalId(candidate.ExternalId, incoming.ExternalId));
            if (externalMatch is not null)
            {
                return externalMatch;
            }
        }

        if (!string.IsNullOrWhiteSpace(incoming.SageCustomerId))
        {
            var sageMatch = clients.FirstOrDefault(candidate =>
                string.Equals(candidate.SageCustomerId?.Trim(), incoming.SageCustomerId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (sageMatch is not null)
            {
                return sageMatch;
            }
        }

        var incomingMatchKey = NormalizeClientMatchKey(ResolveCompanyNameForMatch(incoming));
        return string.IsNullOrWhiteSpace(incomingMatchKey)
            ? null
            : clients.FirstOrDefault(candidate =>
                CanAutoMatchClientsByName(incoming, candidate)
                && NormalizeClientMatchKey(ResolveCompanyNameForMatch(candidate)) == incomingMatchKey);
    }

    private static int SaveClient(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Client client)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (client.Id == 0)
        {
            command.CommandText = """
                INSERT INTO Clients
                    (Name, Source, ExternalId, IsActive, LastSyncedAt,
                     WhdLocationName, WhdContactName, SageCustomerId, SageCustomerName, SageContactName, SageTelephone, MatchStatus)
                VALUES
                    ($name, $source, $externalId, $isActive, $lastSyncedAt,
                     $whdLocationName, $whdContactName, $sageCustomerId, $sageCustomerName, $sageContactName, $sageTelephone, $matchStatus);
                SELECT last_insert_rowid();
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE Clients
                SET Name = $name,
                    Source = $source,
                    ExternalId = $externalId,
                    IsActive = $isActive,
                    LastSyncedAt = $lastSyncedAt,
                    WhdLocationName = $whdLocationName,
                    WhdContactName = $whdContactName,
                    SageCustomerId = $sageCustomerId,
                    SageCustomerName = $sageCustomerName,
                    SageContactName = $sageContactName,
                    SageTelephone = $sageTelephone,
                    MatchStatus = $matchStatus
                WHERE Id = $id;
                SELECT $id;
                """;
            command.Parameters.AddWithValue("$id", client.Id);
        }

        client.Name = BuildClientDisplayName(client);
        client.Source = NormalizeClientSource(client.Source);
        client.MatchStatus = ResolveMatchStatus(client);
        command.Parameters.AddWithValue("$name", client.Name.Trim());
        command.Parameters.AddWithValue("$source", client.Source);
        command.Parameters.AddWithValue("$externalId", ToDbText(client.ExternalId));
        command.Parameters.AddWithValue("$isActive", client.IsActive ? 1 : 0);
        command.Parameters.AddWithValue("$lastSyncedAt", ToDbDateTime(client.LastSyncedAt));
        command.Parameters.AddWithValue("$whdLocationName", ToDbText(client.WhdLocationName));
        command.Parameters.AddWithValue("$whdContactName", ToDbText(client.WhdContactName));
        command.Parameters.AddWithValue("$sageCustomerId", ToDbText(client.SageCustomerId));
        command.Parameters.AddWithValue("$sageCustomerName", ToDbText(client.SageCustomerName));
        command.Parameters.AddWithValue("$sageContactName", ToDbText(client.SageContactName));
        command.Parameters.AddWithValue("$sageTelephone", ToDbText(client.SageTelephone));
        command.Parameters.AddWithValue("$matchStatus", client.MatchStatus);
        client.Id = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return client.Id;
    }

    private static int SaveTicket(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Ticket ticket)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (ticket.Id == 0)
        {
            command.CommandText = """
                INSERT INTO Tickets (TicketNumber, ClientId, Subject, Status, Source, ExternalId, WhdStatusTypeId, IsClosed, LastSyncedAt)
                VALUES ($ticketNumber, $clientId, $subject, $status, $source, $externalId, $whdStatusTypeId, $isClosed, $lastSyncedAt);
                SELECT last_insert_rowid();
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE Tickets
                SET TicketNumber = $ticketNumber,
                    ClientId = $clientId,
                    Subject = $subject,
                    Status = $status,
                    Source = $source,
                    ExternalId = $externalId,
                    WhdStatusTypeId = $whdStatusTypeId,
                    IsClosed = $isClosed,
                    LastSyncedAt = $lastSyncedAt
                WHERE Id = $id;
                SELECT $id;
                """;
            command.Parameters.AddWithValue("$id", ticket.Id);
        }

        command.Parameters.AddWithValue("$ticketNumber", ticket.TicketNumber.Trim());
        command.Parameters.AddWithValue("$clientId", ticket.ClientId);
        command.Parameters.AddWithValue("$subject", ticket.Subject.Trim());
        command.Parameters.AddWithValue("$status", string.IsNullOrWhiteSpace(ticket.Status) ? "Open" : ticket.Status.Trim());
        command.Parameters.AddWithValue("$source", string.IsNullOrWhiteSpace(ticket.Source) ? "Manual" : ticket.Source.Trim());
        command.Parameters.AddWithValue("$externalId", ToDbText(ticket.ExternalId));
        command.Parameters.AddWithValue("$whdStatusTypeId", (object?)ticket.WhdStatusTypeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$isClosed", ticket.IsClosed ? 1 : 0);
        command.Parameters.AddWithValue("$lastSyncedAt", ToDbDateTime(ticket.LastSyncedAt));
        ticket.Id = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        return ticket.Id;
    }

    private static void ReconcileMissingWhdTickets(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<WhdSyncedTicket> syncedTickets,
        DateTime syncedAt)
    {
        var externalIds = syncedTickets
            .Select(static ticket => ticket.ExternalId.Trim())
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ticketNumbers = syncedTickets
            .Select(static ticket => ticket.TicketNumber.Trim())
            .Where(static number => !string.IsNullOrWhiteSpace(number))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var selectCommand = connection.CreateCommand();
        selectCommand.Transaction = transaction;
        selectCommand.CommandText = """
            SELECT Id, TicketNumber, ExternalId
            FROM Tickets
            WHERE Source = 'WHD' AND IsClosed = 0
            """;
        var missingIds = new List<int>();
        using (var reader = selectCommand.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetInt32(0);
                var ticketNumber = reader.GetString(1);
                var externalId = reader.IsDBNull(2) ? null : reader.GetString(2);
                var stillAssigned = ticketNumbers.Contains(ticketNumber)
                    || externalIds.Any(candidate => ContainsExternalId(externalId, candidate));
                if (!stillAssigned)
                {
                    missingIds.Add(id);
                }
            }
        }

        foreach (var id in missingIds)
        {
            using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText = """
                UPDATE Tickets
                SET IsClosed = 1,
                    Status = 'No longer assigned in WHD',
                    LastSyncedAt = $syncedAt
                WHERE Id = $id
                """;
            updateCommand.Parameters.AddWithValue("$id", id);
            updateCommand.Parameters.AddWithValue("$syncedAt", ToDbDateTime(syncedAt));
            updateCommand.ExecuteNonQuery();
        }
    }

    private static int RemoveStaleSageCustomers(
        SqliteConnection connection,
        SqliteTransaction transaction,
        List<Client> clients,
        IReadOnlySet<string> activeSageIds,
        DateTime syncedAt)
    {
        var staleClients = clients
            .Where(client => !string.IsNullOrWhiteSpace(client.SageCustomerId)
                && !activeSageIds.Contains(client.SageCustomerId.Trim()))
            .ToList();
        var changedCount = 0;

        foreach (var client in staleClients)
        {
            if (client.Source.Equals("Sage", StringComparison.OrdinalIgnoreCase)
                && !ClientHasReferences(connection, transaction, client.Id))
            {
                using var deleteCommand = connection.CreateCommand();
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = "DELETE FROM Clients WHERE Id = $id";
                deleteCommand.Parameters.AddWithValue("$id", client.Id);
                changedCount += deleteCommand.ExecuteNonQuery();
                continue;
            }

            client.LastSyncedAt = syncedAt;
            if (client.Source.Equals("Sage", StringComparison.OrdinalIgnoreCase))
            {
                client.IsActive = false;
            }
            else
            {
                client.Source = "WHD";
                client.SageCustomerId = null;
                client.SageCustomerName = null;
                client.SageContactName = null;
                client.SageTelephone = null;
                client.MatchStatus = ResolveMatchStatus(client);
            }

            SaveClient(connection, transaction, client);
            changedCount++;
        }

        return changedCount;
    }

    private static int RemoveStaleWhdOnlyClients(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<Client> clients,
        IReadOnlySet<string> activeExternalIds,
        DateTime syncedAt)
    {
        var staleClients = clients
            .Where(client => client.Source.Equals("WHD", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(client.ExternalId)
                && !activeExternalIds.Any(activeId => ContainsExternalId(client.ExternalId, activeId)))
            .ToList();
        var changedCount = 0;

        foreach (var client in staleClients)
        {
            if (!ClientHasReferences(connection, transaction, client.Id))
            {
                using var deleteCommand = connection.CreateCommand();
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = "DELETE FROM Clients WHERE Id = $id";
                deleteCommand.Parameters.AddWithValue("$id", client.Id);
                changedCount += deleteCommand.ExecuteNonQuery();
                continue;
            }

            client.IsActive = false;
            client.LastSyncedAt = syncedAt;
            SaveClient(connection, transaction, client);
            changedCount++;
        }

        return changedCount;
    }

    private static bool ClientHasReferences(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int clientId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM WorkEntries WHERE ClientId = $clientId)
                + (SELECT COUNT(*) FROM Tickets WHERE ClientId = $clientId)
            """;
        command.Parameters.AddWithValue("$clientId", clientId);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static string NormalizeClientSource(string? source)
    {
        return source?.Trim() switch
        {
            "WHD" => "WHD",
            "Sage" => "Sage",
            "Both" => "Both",
            _ => "WHD"
        };
    }

    private Client? FindClientForSync(Client incoming)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        var normalizedSource = NormalizeClientSource(incoming.Source);

        if (!string.IsNullOrWhiteSpace(incoming.ExternalId) && normalizedSource == "WHD")
        {
            using var externalCommand = connection.CreateCommand();
            externalCommand.CommandText = $"""
                SELECT {ClientSelectColumns}
                FROM Clients
                WHERE (Source = $source OR Source = 'Both')
                  AND ExternalId IS NOT NULL
                """;
            externalCommand.Parameters.AddWithValue("$source", normalizedSource);

            using var externalReader = externalCommand.ExecuteReader();
            while (externalReader.Read())
            {
                var candidate = ReadClient(externalReader);
                if (ContainsExternalId(candidate.ExternalId, incoming.ExternalId))
                {
                    return candidate;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(incoming.SageCustomerId))
        {
            using var sageCommand = connection.CreateCommand();
            sageCommand.CommandText = $"""
                SELECT {ClientSelectColumns}
                FROM Clients
                WHERE SageCustomerId = $sageCustomerId
                LIMIT 1
                """;
            sageCommand.Parameters.AddWithValue("$sageCustomerId", incoming.SageCustomerId.Trim());

            using var sageReader = sageCommand.ExecuteReader();
            if (sageReader.Read())
            {
                return ReadClient(sageReader);
            }
        }

        var incomingMatchKey = NormalizeClientMatchKey(ResolveCompanyNameForMatch(new Client
        {
            Name = incoming.Name,
            Source = normalizedSource,
            ExternalId = incoming.ExternalId,
            WhdLocationName = incoming.WhdLocationName,
            WhdContactName = incoming.WhdContactName,
            SageCustomerId = incoming.SageCustomerId,
            SageCustomerName = incoming.SageCustomerName
        }));
        if (string.IsNullOrWhiteSpace(incomingMatchKey))
        {
            return null;
        }

        using var nameCommand = connection.CreateCommand();
        nameCommand.CommandText = $"""
            SELECT {ClientSelectColumns}
            FROM Clients
            """;

        using var nameReader = nameCommand.ExecuteReader();
        while (nameReader.Read())
        {
            var candidate = ReadClient(nameReader);
            if (!CanAutoMatchClientsByName(incoming, candidate))
            {
                continue;
            }

            if (NormalizeClientMatchKey(ResolveCompanyNameForMatch(candidate)) == incomingMatchKey)
            {
                return candidate;
            }
        }

        return null;
    }

    private Ticket? FindTicketForSync(string source, string? externalId, string ticketNumber)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, TicketNumber, ClientId, Subject, Status, Source, ExternalId, WhdStatusTypeId, IsClosed, LastSyncedAt
            FROM Tickets
            WHERE Source = $source
              AND (
                    ($externalId IS NOT NULL AND ExternalId = $externalId)
                    OR TicketNumber = $ticketNumber
                  )
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$source", string.IsNullOrWhiteSpace(source) ? "WHD" : source.Trim());
        command.Parameters.AddWithValue("$externalId", (object?)externalId ?? DBNull.Value);
        command.Parameters.AddWithValue("$ticketNumber", ticketNumber.Trim());

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTicket(reader) : null;
    }

    private static string? MergeExternalIds(string? existingExternalId, string? incomingExternalId)
    {
        if (string.IsNullOrWhiteSpace(existingExternalId))
        {
            return incomingExternalId;
        }

        if (string.IsNullOrWhiteSpace(incomingExternalId)
            || ContainsExternalId(existingExternalId, incomingExternalId))
        {
            return existingExternalId;
        }

        return $"{existingExternalId} / {incomingExternalId}";
    }

    internal static bool ContainsExternalId(string? externalIds, string? candidateExternalId)
    {
        if (string.IsNullOrWhiteSpace(externalIds) || string.IsNullOrWhiteSpace(candidateExternalId))
        {
            return false;
        }

        var candidate = candidateExternalId.Trim();
        return externalIds
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(externalId => externalId.Equals(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static Client MergeClient(Client existing, Client incoming)
    {
        existing.Source = MergeClientSources(existing.Source, incoming.Source);
        existing.ExternalId = incoming.Source.Equals("WHD", StringComparison.OrdinalIgnoreCase)
            ? MergeExternalIds(existing.ExternalId, incoming.ExternalId)
            : existing.ExternalId;
        existing.IsActive = existing.IsActive || incoming.IsActive;
        existing.LastSyncedAt = incoming.LastSyncedAt ?? existing.LastSyncedAt;
        existing.WhdLocationName = CoalesceText(incoming.WhdLocationName, existing.WhdLocationName);
        existing.WhdContactName = CoalesceText(incoming.WhdContactName, existing.WhdContactName);
        existing.SageCustomerId = CoalesceText(incoming.SageCustomerId, existing.SageCustomerId);
        existing.SageCustomerName = CoalesceText(incoming.SageCustomerName, existing.SageCustomerName);
        existing.SageContactName = CoalesceText(incoming.SageContactName, existing.SageContactName);
        existing.SageTelephone = CoalesceText(incoming.SageTelephone, existing.SageTelephone);
        existing.MatchStatus = ResolveMatchStatus(existing);
        existing.Name = BuildClientDisplayName(existing);
        return existing;
    }

    private static bool CanAutoMatchClientsByName(Client incoming, Client candidate)
    {
        var incomingHasSage = HasSageIdentity(incoming);
        var candidateHasSage = HasSageIdentity(candidate);
        var incomingHasWhdLocation = !string.IsNullOrWhiteSpace(incoming.WhdLocationName);
        var candidateHasWhdLocation = !string.IsNullOrWhiteSpace(candidate.WhdLocationName);

        return (incomingHasSage && candidateHasWhdLocation)
            || (candidateHasSage && incomingHasWhdLocation)
            || (!incomingHasSage && !candidateHasSage);
    }

    private static bool HasSageIdentity(Client client)
    {
        return !string.IsNullOrWhiteSpace(client.SageCustomerId)
            || !string.IsNullOrWhiteSpace(client.SageCustomerName)
            || client.Source.Equals("Sage", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasWhdIdentity(Client client)
    {
        return !string.IsNullOrWhiteSpace(client.ExternalId)
            || !string.IsNullOrWhiteSpace(client.WhdLocationName)
            || client.Source.Equals("WHD", StringComparison.OrdinalIgnoreCase);
    }

    private static string MergeClientSources(string? existingSource, string? incomingSource)
    {
        var existing = NormalizeClientSource(existingSource);
        var incoming = NormalizeClientSource(incomingSource);
        return existing == incoming ? existing : "Both";
    }

    private static string CoalesceText(string? preferred, string? fallback)
    {
        return string.IsNullOrWhiteSpace(preferred) ? fallback ?? string.Empty : preferred.Trim();
    }

    private static string BuildClientDisplayName(Client client)
    {
        var locationName = client.WhdLocationName?.Trim();
        if (!string.IsNullOrWhiteSpace(locationName))
        {
            return locationName;
        }

        if (!string.IsNullOrWhiteSpace(client.SageCustomerName))
        {
            return client.SageCustomerName.Trim();
        }

        return string.IsNullOrWhiteSpace(client.Name) ? "Unnamed client" : client.Name.Trim();
    }

    private static string ResolveCompanyNameForMatch(Client client)
    {
        if (!string.IsNullOrWhiteSpace(client.WhdLocationName))
        {
            return client.WhdLocationName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(client.SageCustomerName))
        {
            return client.SageCustomerName.Trim();
        }

        SplitWhdDisplayName(client.Name, out var locationName, out _);
        return locationName;
    }

    private static string ResolveMatchStatus(Client client)
    {
        var hasWhd = !string.IsNullOrWhiteSpace(client.ExternalId)
            || !string.IsNullOrWhiteSpace(client.WhdLocationName)
            || client.Source.Equals("WHD", StringComparison.OrdinalIgnoreCase);
        var hasSage = !string.IsNullOrWhiteSpace(client.SageCustomerId)
            || !string.IsNullOrWhiteSpace(client.SageCustomerName)
            || client.Source.Equals("Sage", StringComparison.OrdinalIgnoreCase);

        if (hasWhd && hasSage)
        {
            return string.Equals(client.MatchStatus, "Manual match", StringComparison.OrdinalIgnoreCase)
                ? "Manual match"
                : "Matched";
        }

        return "Unmatched";
    }

    private static void SplitWhdDisplayName(string value, out string locationName, out string? contactName)
    {
        var trimmed = value.Trim();
        var separatorIndex = trimmed.IndexOf(" - ", StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex + 3 >= trimmed.Length)
        {
            locationName = trimmed;
            contactName = null;
            return;
        }

        locationName = trimmed[..separatorIndex].Trim();
        contactName = trimmed[(separatorIndex + 3)..].Trim();
    }

    private static string NormalizeStatusKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "OPEN";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToUpperInvariant())
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.ToString().Trim('_');
    }

    private static bool IsClosedStatus(string status)
    {
        return status.Trim().Equals("Closed", StringComparison.OrdinalIgnoreCase)
            || status.Contains("closed", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeClientMatchKey(string value)
        => ClientMatchingService.NormalizeCompanyName(value);

    private static bool ClientHasReferences(SqliteConnection connection, int clientId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM WorkEntries WHERE ClientId = $clientId)
                + (SELECT COUNT(*) FROM Tickets WHERE ClientId = $clientId)
            """;
        command.Parameters.AddWithValue("$clientId", clientId);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static Client ReadClient(SqliteDataReader reader)
    {
        return new Client
        {
            Id = reader.GetInt32(0),
            Name = reader.GetString(1),
            Source = reader.GetString(2),
            ExternalId = reader.IsDBNull(3) ? null : reader.GetString(3),
            IsActive = reader.GetInt32(4) == 1,
            LastSyncedAt = FromDbDateTime(reader, 5),
            WhdLocationName = reader.IsDBNull(6) ? null : reader.GetString(6),
            WhdContactName = reader.IsDBNull(7) ? null : reader.GetString(7),
            SageCustomerId = reader.IsDBNull(8) ? null : reader.GetString(8),
            SageCustomerName = reader.IsDBNull(9) ? null : reader.GetString(9),
            SageContactName = reader.IsDBNull(10) ? null : reader.GetString(10),
            SageTelephone = reader.IsDBNull(11) ? null : reader.GetString(11),
            MatchStatus = reader.IsDBNull(12) ? "Unmatched" : reader.GetString(12)
        };
    }

    private static Client? GetClient(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int clientId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {ClientSelectColumns}
            FROM Clients
            WHERE Id = $clientId
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$clientId", clientId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadClient(reader) : null;
    }

    private static Ticket ReadTicket(SqliteDataReader reader)
    {
        return new Ticket
        {
            Id = reader.GetInt32(0),
            TicketNumber = reader.GetString(1),
            ClientId = reader.GetInt32(2),
            Subject = reader.GetString(3),
            Status = reader.GetString(4),
            Source = reader.GetString(5),
            ExternalId = reader.IsDBNull(6) ? null : reader.GetString(6),
            WhdStatusTypeId = reader.IsDBNull(7) ? null : reader.GetInt32(7),
            IsClosed = reader.GetInt32(8) == 1,
            LastSyncedAt = FromDbDateTime(reader, 9)
        };
    }

    private static TicketStatusOption ReadTicketStatusOption(SqliteDataReader reader)
    {
        return new TicketStatusOption
        {
            Id = reader.GetInt32(0),
            Name = reader.GetString(1),
            Source = reader.GetString(2),
            ExternalId = reader.IsDBNull(3) ? null : reader.GetString(3),
            WhdStatusTypeId = reader.IsDBNull(4) ? null : reader.GetInt32(4),
            IsClosed = reader.GetInt32(5) == 1,
            LastSyncedAt = FromDbDateTime(reader, 6)
        };
    }

    private int SaveWorkEntry(SqliteConnection connection, SqliteTransaction transaction, WorkEntry entry)
    {
        if (entry.Id > 0 && IsSagePosted(connection, transaction, entry.Id))
        {
            throw new InvalidOperationException("Entries posted to Sage are permanently locked and cannot be changed.");
        }

        var now = DateTime.Now;
        entry.UpdatedAt = now;
        entry.Tags = WorkEntryTags.Normalize(entry.Tags);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (entry.Id == 0)
        {
            entry.CreatedAt = now;
            command.CommandText = """
                INSERT INTO WorkEntries
                    (WorkDate, ClientId, ManualClientName, TicketId, TicketNumberText, HasTimeRange, StartTime, EndTime, DurationMinutes,
                     Billable, Note, InternalNote, IncludePersonalNoteInWhd, Tags, FollowUpState, FollowUpDueDate,
                     WhdPosted, WhdPostedAt, SagePosted, SagePostedAt, SageTicketNumber,
                     PostingStatus, LastError, CreatedAt, UpdatedAt)
                VALUES
                    ($workDate, $clientId, $manualClientName, $ticketId, $ticketNumberText, $hasTimeRange, $startTime, $endTime, $durationMinutes,
                     $billable, $note, $internalNote, $includePersonalNoteInWhd, $tags, $followUpState, $followUpDueDate,
                     $whdPosted, $whdPostedAt, $sagePosted, $sagePostedAt, $sageTicketNumber,
                     $postingStatus, $lastError, $createdAt, $updatedAt);
                SELECT last_insert_rowid();
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE WorkEntries
                SET WorkDate = $workDate,
                    ClientId = $clientId,
                    ManualClientName = $manualClientName,
                    TicketId = $ticketId,
                    TicketNumberText = $ticketNumberText,
                    HasTimeRange = $hasTimeRange,
                    StartTime = $startTime,
                    EndTime = $endTime,
                    DurationMinutes = $durationMinutes,
                    Billable = $billable,
                    Note = $note,
                    InternalNote = $internalNote,
                    IncludePersonalNoteInWhd = $includePersonalNoteInWhd,
                    Tags = $tags,
                    FollowUpState = $followUpState,
                    FollowUpDueDate = $followUpDueDate,
                    WhdPosted = $whdPosted,
                    WhdPostedAt = $whdPostedAt,
                    SagePosted = $sagePosted,
                    SagePostedAt = $sagePostedAt,
                    SageTicketNumber = $sageTicketNumber,
                    PostingStatus = $postingStatus,
                    LastError = $lastError,
                    UpdatedAt = $updatedAt
                WHERE Id = $id;
                SELECT $id;
                """;
            command.Parameters.AddWithValue("$id", entry.Id);
        }

        AddWorkEntryParameters(command, entry);
        var id = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        entry.Id = id;
        UpdateWorkEntrySearchIndex(connection, transaction, id);
        return id;
    }

    private static bool IsSagePosted(SqliteConnection connection, SqliteTransaction transaction, int workEntryId)
    {
        if (workEntryId <= 0)
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT SagePosted FROM WorkEntries WHERE Id = $id LIMIT 1";
        command.Parameters.AddWithValue("$id", workEntryId);
        return command.ExecuteScalar() is long value && value == 1;
    }

    private static bool IsWhdPosted(SqliteConnection connection, SqliteTransaction transaction, int workEntryId)
    {
        if (workEntryId <= 0)
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT WhdPosted FROM WorkEntries WHERE Id = $id LIMIT 1";
        command.Parameters.AddWithValue("$id", workEntryId);
        return command.ExecuteScalar() is long value && value == 1;
    }

    private static PostingLog ReadPostingLog(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt32(0),
        WorkEntryId = reader.GetInt32(1),
        Destination = reader.GetString(2),
        Payload = reader.GetString(3),
        Success = reader.GetInt32(4) == 1,
        Message = reader.GetString(5),
        ExternalReference = reader.IsDBNull(6) ? null : reader.GetString(6),
        CreatedAt = FromDbDateTime(reader, 7) ?? DateTime.MinValue
    };

    private void UpdateWorkEntrySearchIndex(SqliteConnection connection, SqliteTransaction transaction, int id)
    {
        if (!_fullTextSearchAvailable)
        {
            return;
        }

        using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM WorkEntrySearch WHERE WorkEntryId = $id";
            deleteCommand.Parameters.AddWithValue("$id", id);
            deleteCommand.ExecuteNonQuery();
        }

        using var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = transaction;
        insertCommand.CommandText = """
            INSERT INTO WorkEntrySearch (WorkEntryId, Note, InternalNote, ClientName, TicketText, Tags)
            SELECT w.Id,
                   w.Note,
                   COALESCE(w.InternalNote, ''),
                   COALESCE(NULLIF(w.ManualClientName, ''), c.Name, ''),
                   TRIM(COALESCE(t.TicketNumber, '') || ' ' || COALESCE(t.Subject, '') || ' ' || COALESCE(w.TicketNumberText, '')),
                   COALESCE(w.Tags, '')
            FROM WorkEntries w
            LEFT JOIN Clients c ON c.Id = w.ClientId
            LEFT JOIN Tickets t ON t.Id = w.TicketId
            WHERE w.Id = $id
            """;
        insertCommand.Parameters.AddWithValue("$id", id);
        insertCommand.ExecuteNonQuery();
    }

    private static string? BuildFullTextQuery(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return null;
        }

        var tokens = Regex.Matches(keyword, @"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)
            .Select(static match => match.Value)
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static value => $"\"{value.Replace("\"", "\"\"")}\"*")
            .ToArray();
        return tokens.Length == 0 ? null : string.Join(" AND ", tokens);
    }

    private static WorkEntry ReadWorkEntry(SqliteDataReader reader)
    {
        return new WorkEntry
        {
            Id = reader.GetInt32(0),
            WorkDate = DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
            ClientId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
            ManualClientName = reader.IsDBNull(3) ? null : reader.GetString(3),
            TicketId = reader.IsDBNull(4) ? null : reader.GetInt32(4),
            TicketNumberText = reader.IsDBNull(5) ? null : reader.GetString(5),
            HasTimeRange = reader.GetInt32(6) == 1,
            StartTime = ParseTime(reader.GetString(7)),
            EndTime = ParseTime(reader.GetString(8)),
            DurationMinutes = reader.GetInt32(9),
            Billable = reader.GetInt32(10) == 1,
            Note = reader.GetString(11),
            InternalNote = reader.IsDBNull(12) ? null : reader.GetString(12),
            IncludePersonalNoteInWhd = reader.GetInt32(13) == 1,
            Tags = reader.IsDBNull(14) ? string.Empty : reader.GetString(14),
            FollowUpState = Enum.TryParse<FollowUpState>(reader.GetString(15), out var followUpState)
                ? followUpState
                : FollowUpState.None,
            FollowUpDueDate = FromDbDateTime(reader, 16),
            WhdPosted = reader.GetInt32(17) == 1,
            WhdPostedAt = FromDbDateTime(reader, 18),
            SagePosted = reader.GetInt32(19) == 1,
            SagePostedAt = FromDbDateTime(reader, 20),
            SageTicketNumber = reader.IsDBNull(21) ? null : reader.GetString(21),
            PostingStatus = Enum.TryParse<PostingStatus>(reader.GetString(22), out var status) ? status : PostingStatus.Draft,
            LastError = reader.IsDBNull(23) ? null : reader.GetString(23),
            CreatedAt = DateTime.Parse(reader.GetString(24), CultureInfo.InvariantCulture),
            UpdatedAt = DateTime.Parse(reader.GetString(25), CultureInfo.InvariantCulture),
            ClientName = reader.GetString(26),
            TicketNumber = reader.IsDBNull(27) ? null : reader.GetString(27),
            TicketSubject = reader.IsDBNull(28) ? null : reader.GetString(28),
            SearchSnippet = reader.IsDBNull(29) ? null : reader.GetString(29)
        };
    }

    private static void AddWorkEntryParameters(SqliteCommand command, WorkEntry entry)
    {
        command.Parameters.AddWithValue("$workDate", ToDbDate(entry.WorkDate));
        command.Parameters.AddWithValue("$clientId", (object?)entry.ClientId ?? DBNull.Value);
        command.Parameters.AddWithValue("$manualClientName", string.IsNullOrWhiteSpace(entry.ManualClientName) ? DBNull.Value : entry.ManualClientName.Trim());
        command.Parameters.AddWithValue("$ticketId", (object?)entry.TicketId ?? DBNull.Value);
        command.Parameters.AddWithValue("$ticketNumberText", string.IsNullOrWhiteSpace(entry.TicketNumberText) ? DBNull.Value : entry.TicketNumberText.Trim());
        command.Parameters.AddWithValue("$hasTimeRange", entry.HasTimeRange ? 1 : 0);
        command.Parameters.AddWithValue("$startTime", entry.StartTime.ToString(@"hh\:mm", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$endTime", entry.EndTime.ToString(@"hh\:mm", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$durationMinutes", entry.DurationMinutes);
        command.Parameters.AddWithValue("$billable", entry.Billable ? 1 : 0);
        command.Parameters.AddWithValue("$note", entry.Note.Trim());
        command.Parameters.AddWithValue("$internalNote", string.IsNullOrWhiteSpace(entry.InternalNote) ? DBNull.Value : entry.InternalNote.Trim());
        command.Parameters.AddWithValue("$includePersonalNoteInWhd", entry.IncludePersonalNoteInWhd ? 1 : 0);
        command.Parameters.AddWithValue("$tags", string.IsNullOrWhiteSpace(entry.Tags) ? string.Empty : entry.Tags.Trim());
        command.Parameters.AddWithValue("$followUpState", entry.FollowUpState.ToString());
        command.Parameters.AddWithValue("$followUpDueDate", ToDbDateTime(entry.FollowUpDueDate));
        command.Parameters.AddWithValue("$whdPosted", entry.WhdPosted ? 1 : 0);
        command.Parameters.AddWithValue("$whdPostedAt", ToDbDateTime(entry.WhdPostedAt));
        command.Parameters.AddWithValue("$sagePosted", entry.SagePosted ? 1 : 0);
        command.Parameters.AddWithValue("$sagePostedAt", ToDbDateTime(entry.SagePostedAt));
        command.Parameters.AddWithValue("$sageTicketNumber", ToDbText(entry.SageTicketNumber));
        command.Parameters.AddWithValue("$postingStatus", entry.PostingStatus.ToString());
        command.Parameters.AddWithValue("$lastError", string.IsNullOrWhiteSpace(entry.LastError) ? DBNull.Value : entry.LastError.Trim());
        command.Parameters.AddWithValue("$createdAt", ToDbDateTime(entry.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", ToDbDateTime(entry.UpdatedAt));
    }

    private static string ToDbDate(DateTime value) => value.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static object ToDbDateTime(DateTime? value)
    {
        return value.HasValue
            ? value.Value.ToString("O", CultureInfo.InvariantCulture)
            : DBNull.Value;
    }

    private static object ToDbText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    }

    private static DateTime? FromDbDateTime(SqliteDataReader reader, int index)
    {
        return reader.IsDBNull(index)
            ? null
            : DateTime.Parse(reader.GetString(index), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static TimeSpan ParseTime(string value)
    {
        return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed) ? parsed : TimeSpan.Zero;
    }

    private const string SchemaSql = """
        PRAGMA foreign_keys = ON;

        CREATE TABLE IF NOT EXISTS Clients (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            Source TEXT NOT NULL DEFAULT 'WHD',
            ExternalId TEXT NULL,
            IsActive INTEGER NOT NULL DEFAULT 1,
            LastSyncedAt TEXT NULL,
            WhdLocationName TEXT NULL,
            WhdContactName TEXT NULL,
            SageCustomerId TEXT NULL,
            SageCustomerName TEXT NULL,
            SageContactName TEXT NULL,
            SageTelephone TEXT NULL,
            MatchStatus TEXT NOT NULL DEFAULT 'Unmatched'
        );

        CREATE TABLE IF NOT EXISTS Tickets (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            TicketNumber TEXT NOT NULL,
            ClientId INTEGER NOT NULL,
            Subject TEXT NOT NULL DEFAULT '',
            Status TEXT NOT NULL DEFAULT 'Open',
            Source TEXT NOT NULL DEFAULT 'Manual',
            ExternalId TEXT NULL,
            WhdStatusTypeId INTEGER NULL,
            IsClosed INTEGER NOT NULL DEFAULT 0,
            LastSyncedAt TEXT NULL,
            FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS TicketStatusOptions (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            Source TEXT NOT NULL DEFAULT 'WHD',
            ExternalId TEXT NULL,
            WhdStatusTypeId INTEGER NULL,
            IsClosed INTEGER NOT NULL DEFAULT 0,
            LastSyncedAt TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS WorkEntries (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            WorkDate TEXT NOT NULL,
            ClientId INTEGER NULL,
            ManualClientName TEXT NULL,
            TicketId INTEGER NULL,
            TicketNumberText TEXT NULL,
            HasTimeRange INTEGER NOT NULL DEFAULT 1,
            StartTime TEXT NOT NULL,
            EndTime TEXT NOT NULL,
            DurationMinutes INTEGER NOT NULL,
            Billable INTEGER NOT NULL DEFAULT 1,
            Note TEXT NOT NULL DEFAULT '',
            InternalNote TEXT NULL,
            IncludePersonalNoteInWhd INTEGER NOT NULL DEFAULT 0,
            Tags TEXT NOT NULL DEFAULT '',
            FollowUpState TEXT NOT NULL DEFAULT 'None',
            FollowUpDueDate TEXT NULL,
            WhdPosted INTEGER NOT NULL DEFAULT 0,
            WhdPostedAt TEXT NULL,
            SagePosted INTEGER NOT NULL DEFAULT 0,
            SagePostedAt TEXT NULL,
            SageTicketNumber TEXT NULL,
            PostingStatus TEXT NOT NULL DEFAULT 'Draft',
            LastError TEXT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            FOREIGN KEY (ClientId) REFERENCES Clients(Id),
            FOREIGN KEY (TicketId) REFERENCES Tickets(Id)
        );

        CREATE TABLE IF NOT EXISTS WorkEntryLinks (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            SourceWorkEntryId INTEGER NOT NULL,
            TargetWorkEntryId INTEGER NOT NULL,
            LinkType TEXT NOT NULL DEFAULT 'Related',
            CreatedAt TEXT NOT NULL,
            CHECK (SourceWorkEntryId <> TargetWorkEntryId),
            CHECK (LinkType IN ('Related', 'FollowUpTo')),
            FOREIGN KEY (SourceWorkEntryId) REFERENCES WorkEntries(Id) ON DELETE CASCADE,
            FOREIGN KEY (TargetWorkEntryId) REFERENCES WorkEntries(Id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS CommonLinks (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            Url TEXT NOT NULL,
            SortOrder INTEGER NOT NULL DEFAULT 0,
            BuiltInKey TEXT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS Settings (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Key TEXT NOT NULL UNIQUE,
            Value TEXT NOT NULL DEFAULT ''
        );

        CREATE TABLE IF NOT EXISTS Templates (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            Category TEXT NOT NULL DEFAULT '',
            TemplateText TEXT NOT NULL DEFAULT ''
        );

        CREATE TABLE IF NOT EXISTS EditorDrafts (
            Id INTEGER PRIMARY KEY CHECK (Id = 1),
            Payload TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS ClientAliases (
            Alias TEXT PRIMARY KEY COLLATE NOCASE,
            ClientId INTEGER NOT NULL,
            FOREIGN KEY (ClientId) REFERENCES Clients(Id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS PostingLogs (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            WorkEntryId INTEGER NOT NULL,
            Destination TEXT NOT NULL,
            Payload TEXT NOT NULL DEFAULT '',
            Success INTEGER NOT NULL DEFAULT 0,
            Message TEXT NOT NULL DEFAULT '',
            ExternalReference TEXT NULL,
            CreatedAt TEXT NOT NULL,
            FOREIGN KEY (WorkEntryId) REFERENCES WorkEntries(Id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS PostingAttempts (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            WorkEntryId INTEGER NOT NULL,
            Destination TEXT NOT NULL,
            AttemptKey TEXT NOT NULL UNIQUE,
            PayloadHash TEXT NOT NULL,
            Status TEXT NOT NULL,
            Message TEXT NOT NULL DEFAULT '',
            ExternalReference TEXT NULL,
            StartedAt TEXT NOT NULL,
            CompletedAt TEXT NULL,
            FOREIGN KEY (WorkEntryId) REFERENCES WorkEntries(Id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS IX_Clients_Name ON Clients(Name);
        CREATE INDEX IF NOT EXISTS IX_Tickets_ClientId ON Tickets(ClientId);
        CREATE INDEX IF NOT EXISTS IX_Tickets_TicketNumber ON Tickets(TicketNumber);
        CREATE UNIQUE INDEX IF NOT EXISTS IX_TicketStatusOptions_SourceExternalId ON TicketStatusOptions(Source, ExternalId);
        CREATE INDEX IF NOT EXISTS IX_TicketStatusOptions_Name ON TicketStatusOptions(Name);
        CREATE INDEX IF NOT EXISTS IX_WorkEntries_WorkDate ON WorkEntries(WorkDate);
        CREATE INDEX IF NOT EXISTS IX_WorkEntries_ClientId ON WorkEntries(ClientId);
        CREATE INDEX IF NOT EXISTS IX_WorkEntries_TicketId ON WorkEntries(TicketId);
        CREATE INDEX IF NOT EXISTS IX_WorkEntries_PostingStatus ON WorkEntries(PostingStatus);
        CREATE INDEX IF NOT EXISTS IX_WorkEntries_PendingWhd ON WorkEntries(WhdPosted, TicketId, TicketNumberText);
        CREATE INDEX IF NOT EXISTS IX_WorkEntries_PendingSage ON WorkEntries(SagePosted, Billable);
        CREATE UNIQUE INDEX IF NOT EXISTS UX_WorkEntryLinks_Pair
            ON WorkEntryLinks(
                CASE WHEN SourceWorkEntryId < TargetWorkEntryId THEN SourceWorkEntryId ELSE TargetWorkEntryId END,
                CASE WHEN SourceWorkEntryId < TargetWorkEntryId THEN TargetWorkEntryId ELSE SourceWorkEntryId END);
        CREATE INDEX IF NOT EXISTS IX_WorkEntryLinks_Source ON WorkEntryLinks(SourceWorkEntryId);
        CREATE INDEX IF NOT EXISTS IX_WorkEntryLinks_Target ON WorkEntryLinks(TargetWorkEntryId);
        CREATE UNIQUE INDEX IF NOT EXISTS UX_CommonLinks_Url ON CommonLinks(Url COLLATE NOCASE);
        CREATE INDEX IF NOT EXISTS IX_CommonLinks_SortOrder ON CommonLinks(SortOrder, Name COLLATE NOCASE);
        CREATE INDEX IF NOT EXISTS IX_ClientAliases_ClientId ON ClientAliases(ClientId);
        CREATE INDEX IF NOT EXISTS IX_Clients_ExternalId ON Clients(ExternalId);
        CREATE INDEX IF NOT EXISTS IX_Clients_SageCustomerId ON Clients(SageCustomerId);
        CREATE INDEX IF NOT EXISTS IX_Tickets_SourceExternalId ON Tickets(Source, ExternalId);
        CREATE INDEX IF NOT EXISTS IX_PostingAttempts_WorkEntryDestination ON PostingAttempts(WorkEntryId, Destination, StartedAt DESC);
        CREATE UNIQUE INDEX IF NOT EXISTS UX_PostingAttempts_ActiveDestination
            ON PostingAttempts(WorkEntryId, Destination)
            WHERE Status IN ('Started', 'Unknown');
        """;
}
