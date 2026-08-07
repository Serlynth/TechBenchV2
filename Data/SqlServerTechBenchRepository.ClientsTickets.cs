using System.Data;
using Microsoft.Data.SqlClient;
using TechBench.Models;

namespace TechBench.Data;

public sealed partial class SqlServerTechBenchRepository
{
    public IReadOnlyList<Client> GetClients(
        bool includeInactive = false,
        string? searchTerm = null) =>
        GetClientsAsync(includeInactive, searchTerm).GetAwaiter().GetResult();

    public Task<IReadOnlyList<Client>> GetClientsAsync(
        bool includeInactive = false,
        string? searchTerm = null,
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            Procedures.SearchClients,
            command =>
            {
                AddBit(command, "@IncludeInactive", includeInactive);
                AddText(command, "@Search", 240, searchTerm);
                AddInt(command, "@Limit", 1000);
            },
            (reader, token) => ReadListAsync(reader, token, ReadClient),
            cancellationToken);

    public Client? GetClient(int id) =>
        GetClientAsync(id).GetAwaiter().GetResult();

    public Task<Client?> GetClientAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        if (id <= 0)
        {
            return Task.FromResult<Client?>(null);
        }

        return QueryAsync(
            Procedures.GetClient,
            command => AddInt(command, "@Id", id),
            (reader, token) => ReadSingleAsync(reader, token, ReadClient),
            cancellationToken);
    }

    public int SaveClient(Client client) =>
        SaveClientAsync(client).GetAwaiter().GetResult();

    public async Task<int> SaveClientAsync(
        Client client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var saved = await QueryAsync(
                Procedures.SaveClient,
                command =>
                {
                    AddInt(command, "@Id", client.Id > 0 ? client.Id : null);
                    AddRequiredText(command, "@Name", 240, client.Name);
                    AddRequiredText(command, "@Source", 80, client.Source);
                    AddText(command, "@ExternalId", 500, client.ExternalId);
                    AddBit(command, "@IsActive", client.IsActive);
                    AddDateTime(command, "@LastSyncedAtUtc", client.LastSyncedAt);
                    AddText(command, "@WhdLocationName", 240, client.WhdLocationName);
                    AddText(command, "@WhdContactName", 240, client.WhdContactName);
                    AddText(command, "@SageCustomerId", 120, client.SageCustomerId);
                    AddText(command, "@SageCustomerName", 240, client.SageCustomerName);
                    AddText(command, "@SageContactName", 240, client.SageContactName);
                    AddText(command, "@SageTelephone", 80, client.SageTelephone);
                    AddRequiredText(command, "@MatchStatus", 80, client.MatchStatus);
                    AddBinary(
                        command,
                        "@ExpectedRowVersion",
                        8,
                        client.RowVersion
                        ?? GetTrackedRowVersion("Client", client.Id));
                    AddGuid(command, "@RequestId", Guid.NewGuid());
                },
                (reader, token) => ReadSingleAsync(reader, token, ReadClient),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"{Procedures.SaveClient} did not return the saved client.");
        CopyClient(saved, client);
        return client.Id;
    }

    public IReadOnlyList<Ticket> GetTickets(
        int? clientId = null,
        string? searchTerm = null,
        bool includeClosed = false) =>
        GetTicketsAsync(clientId, searchTerm, includeClosed).GetAwaiter().GetResult();

    public Task<IReadOnlyList<Ticket>> GetTicketsAsync(
        int? clientId = null,
        string? searchTerm = null,
        bool includeClosed = false,
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            Procedures.SearchTickets,
            command =>
            {
                AddInt(command, "@ClientId", clientId);
                AddText(command, "@Search", 240, searchTerm);
                AddBit(command, "@IncludeClosed", includeClosed);
                AddInt(command, "@Limit", 500);
            },
            (reader, token) => ReadListAsync(reader, token, ReadTicket),
            cancellationToken);

    public Ticket? GetTicket(int id) =>
        GetTicketAsync(id).GetAwaiter().GetResult();

    public Task<Ticket?> GetTicketAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        if (id <= 0)
        {
            return Task.FromResult<Ticket?>(null);
        }

        return QueryAsync(
            Procedures.GetTicket,
            command => AddInt(command, "@Id", id),
            (reader, token) => ReadSingleAsync(reader, token, ReadTicket),
            cancellationToken);
    }

    public int SaveTicket(Ticket ticket) =>
        SaveTicketAsync(ticket).GetAwaiter().GetResult();

    public async Task<int> SaveTicketAsync(
        Ticket ticket,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var saved = await QueryAsync(
                Procedures.SaveTicket,
                command => AddTicketParameters(
                    command,
                    ticket,
                    GetTrackedRowVersion("Ticket", ticket.Id)),
                (reader, token) => ReadSingleAsync(reader, token, ReadTicket),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"{Procedures.SaveTicket} did not return the saved ticket.");
        CopyTicket(saved, ticket);
        return ticket.Id;
    }

    public IReadOnlyList<TicketStatusOption> GetTicketStatusOptions() =>
        GetTicketStatusOptionsAsync().GetAwaiter().GetResult();

    public Task<IReadOnlyList<TicketStatusOption>> GetTicketStatusOptionsAsync(
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            Procedures.GetTicketStatusOptions,
            null,
            (reader, token) => ReadListAsync(reader, token, ReadTicketStatusOption),
            cancellationToken);

    public int UpsertTicketStatusOption(TicketStatusOption option) =>
        UpsertTicketStatusOptionAsync(option).GetAwaiter().GetResult();

    public async Task<int> UpsertTicketStatusOptionAsync(
        TicketStatusOption option,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(option);
        var saved = await QueryAsync(
                Procedures.UpsertTicketStatusOption,
                command =>
                {
                    AddRequiredText(command, "@Name", 160, option.Name);
                    AddRequiredText(command, "@Source", 40, option.Source);
                    AddText(command, "@ExternalId", 240, option.ExternalId);
                    AddInt(command, "@WhdStatusTypeId", option.WhdStatusTypeId);
                    AddBit(command, "@IsClosed", option.IsClosed);
                    AddDateTime(command, "@LastSyncedAtUtc", option.LastSyncedAt);
                },
                (reader, token) =>
                    ReadSingleAsync(reader, token, ReadTicketStatusOption),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"{Procedures.UpsertTicketStatusOption} did not return a status.");
        CopyTicketStatusOption(saved, option);
        return option.Id;
    }

    public int UpsertSyncedClient(Client client) =>
        UpsertSyncedClientAsync(client).GetAwaiter().GetResult();

    public async Task<int> UpsertSyncedClientAsync(
        Client client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var saved = await QueryAsync(
                Procedures.UpsertClient,
                command => AddClientSyncParameters(command, client),
                (reader, token) => ReadSingleAsync(reader, token, ReadClient),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"{Procedures.UpsertClient} did not return the saved client.");
        CopyClient(saved, client);
        return client.Id;
    }

    public int UpsertSageCustomer(SageCustomer customer, DateTime? syncedAt = null) =>
        UpsertSageCustomerAsync(customer, syncedAt).GetAwaiter().GetResult();

    public async Task<int> UpsertSageCustomerAsync(
        SageCustomer customer,
        DateTime? syncedAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(customer);
        var saved = await QueryAsync(
                Procedures.UpsertSageCustomer,
                command =>
                {
                    AddRequiredText(command, "@CustomerId", 120, customer.CustomerId);
                    AddRequiredText(command, "@CustomerName", 240, customer.CustomerName);
                    AddText(command, "@ContactName", 240, customer.ContactName);
                    AddText(command, "@Telephone", 80, customer.Telephone);
                    AddBit(command, "@IsActive", customer.IsActive);
                    AddDateTime(command, "@SyncedAtUtc", syncedAt ?? DateTime.Now);
                },
                async (reader, token) =>
                {
                    if (!await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        return 0;
                    }

                    return GetInt32(reader, "ClientId", GetInt32(reader, "Id"));
                },
                cancellationToken)
            .ConfigureAwait(false);
        return saved;
    }

    public Client MergeClientRecords(int whdClientId, int sageClientId) =>
        MergeClientRecordsAsync(whdClientId, sageClientId).GetAwaiter().GetResult();

    public async Task<Client> MergeClientRecordsAsync(
        int whdClientId,
        int sageClientId,
        CancellationToken cancellationToken = default)
    {
        return await QueryAsync(
                Procedures.MergeClients,
                command =>
                {
                    AddInt(command, "@TargetClientId", whdClientId);
                    AddInt(command, "@SourceClientId", sageClientId);
                    AddBinary(
                        command,
                        "@ExpectedTargetRowVersion",
                        8,
                        GetTrackedRowVersion("Client", whdClientId));
                    AddBinary(
                        command,
                        "@ExpectedSourceRowVersion",
                        8,
                        GetTrackedRowVersion("Client", sageClientId));
                    AddGuid(command, "@RequestId", Guid.NewGuid());
                },
                (reader, token) => ReadSingleAsync(reader, token, ReadClient),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"{Procedures.MergeClients} did not return the merged client.");
    }

    public Client LinkClientSources(
        int canonicalClientId,
        int? whdClientId,
        int? sageClientId) =>
        LinkClientSourcesAsync(canonicalClientId, whdClientId, sageClientId)
            .GetAwaiter()
            .GetResult();

    public async Task<Client> LinkClientSourcesAsync(
        int canonicalClientId,
        int? whdClientId,
        int? sageClientId,
        CancellationToken cancellationToken = default)
    {
        if (canonicalClientId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(canonicalClientId),
                "A canonical TechBench client is required.");
        }

        if (whdClientId is null && sageClientId is null)
        {
            throw new ArgumentException(
                "Select at least one WHD or Sage source to link.");
        }

        return await QueryAsync(
                Procedures.LinkClientSources,
                command =>
                {
                    AddInt(command, "@CanonicalClientId", canonicalClientId);
                    AddInt(command, "@WhdClientId", whdClientId);
                    AddInt(command, "@SageClientId", sageClientId);
                    AddBinary(
                        command,
                        "@ExpectedCanonicalRowVersion",
                        8,
                        GetTrackedRowVersion("Client", canonicalClientId));
                    AddBinary(
                        command,
                        "@ExpectedWhdRowVersion",
                        8,
                        whdClientId.HasValue
                            ? GetTrackedRowVersion("Client", whdClientId.Value)
                            : null);
                    AddBinary(
                        command,
                        "@ExpectedSageRowVersion",
                        8,
                        sageClientId.HasValue
                            ? GetTrackedRowVersion("Client", sageClientId.Value)
                            : null);
                    AddGuid(command, "@RequestId", Guid.NewGuid());
                },
                (reader, token) => ReadSingleAsync(reader, token, ReadClient),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"{Procedures.LinkClientSources} did not return the linked client.");
    }

    public int ReconcileExactClientMatches() =>
        ReconcileClientMatchesAsync("Exact").GetAwaiter().GetResult();

    public int ReconcileStrongClientMatches() =>
        ReconcileClientMatchesAsync("Strong").GetAwaiter().GetResult();

    public int ReconcileSafeClientMatches() =>
        ReconcileClientMatchesAsync("Safe").GetAwaiter().GetResult();

    public Task<int> ReconcileClientMatchesAsync(
        string mode,
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            Procedures.ReconcileClientMatches,
            command => AddRequiredText(command, "@Mode", 40, mode),
            async (reader, token) =>
            {
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    return 0;
                }

                return GetInt32(reader, "MatchedCount", GetInt32(reader, "AffectedCount"));
            },
            cancellationToken);

    public int RemoveStaleSageCustomers(
        IReadOnlyCollection<string> activeSageCustomerIds,
        DateTime? syncedAt = null) =>
        RemoveStaleSageCustomersAsync(activeSageCustomerIds, syncedAt)
            .GetAwaiter()
            .GetResult();

    public Task<int> RemoveStaleSageCustomersAsync(
        IReadOnlyCollection<string> activeSageCustomerIds,
        DateTime? syncedAt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activeSageCustomerIds);
        return QueryAsync(
            Procedures.RemoveStaleSageCustomers,
            command =>
            {
                AddMaxText(command, "@ActiveCustomerIdsJson", SerializePayload(activeSageCustomerIds));
                AddDateTime(command, "@SyncedAtUtc", syncedAt ?? DateTime.Now);
            },
            async (reader, token) =>
            {
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    return 0;
                }

                return GetInt32(reader, "StaleCount", GetInt32(reader, "AffectedCount"));
            },
            cancellationToken);
    }

    public Client? TryAutoMatchSageCustomerForClient(int clientId) =>
        TryAutoMatchSageCustomerForClientAsync(clientId).GetAwaiter().GetResult();

    public async Task<Client?> TryAutoMatchSageCustomerForClientAsync(
        int clientId,
        CancellationToken cancellationToken = default)
    {
        await ReconcileClientMatchesAsync("Safe", cancellationToken).ConfigureAwait(false);
        var client = await GetClientAsync(clientId, cancellationToken).ConfigureAwait(false);
        return client?.Source.Equals("Both", StringComparison.OrdinalIgnoreCase) == true
            ? client
            : null;
    }

    public int UpsertSyncedTicket(Ticket ticket) =>
        UpsertSyncedTicketAsync(ticket).GetAwaiter().GetResult();

    public async Task<int> UpsertSyncedTicketAsync(
        Ticket ticket,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var saved = await QueryAsync(
                Procedures.UpsertTicket,
                command => AddTicketSyncParameters(command, ticket),
                (reader, token) => ReadSingleAsync(reader, token, ReadTicket),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"{Procedures.UpsertTicket} did not return the saved ticket.");
        CopyTicket(saved, ticket);
        return ticket.Id;
    }

    private Client ReadClient(SqlDataReader reader)
    {
        var client = new Client
        {
            Id = GetInt32(reader, "Id"),
            Name = GetString(reader, "Name"),
            Source = GetString(reader, "Source", "Manual"),
            ExternalId = GetNullableString(reader, "ExternalId"),
            IsActive = GetBoolean(reader, "IsActive", true),
            LastSyncedAt = GetNullableDateTime(reader, "LastSyncedAt"),
            WhdLocationName = GetNullableString(reader, "WhdLocationName"),
            WhdContactName = GetNullableString(reader, "WhdContactName"),
            WhdContactEmail = GetNullableString(reader, "WhdContactEmail"),
            WhdPhone = GetNullableString(reader, "WhdPhone"),
            WhdAddress = GetNullableString(reader, "WhdAddress"),
            SageCustomerId = GetNullableString(reader, "SageCustomerId"),
            SageCustomerName = GetNullableString(reader, "SageCustomerName"),
            SageContactName = GetNullableString(reader, "SageContactName"),
            SageTelephone = GetNullableString(reader, "SageTelephone"),
            MatchStatus = GetString(reader, "MatchStatus", "Unmatched")
        };
        client.HasWhdIdentity = GetBoolean(
            reader,
            "HasWhdIdentity",
            client.Source.Equals("WHD", StringComparison.OrdinalIgnoreCase)
            || client.Source.Equals("Both", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(client.WhdLocationName));
        client.HasSageIdentity = GetBoolean(
            reader,
            "HasSageIdentity",
            client.Source.Equals("Sage", StringComparison.OrdinalIgnoreCase)
            || client.Source.Equals("Both", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(client.SageCustomerId));
        client.IsClientInfoLive = GetBoolean(reader, "IsClientInfoLive");
        // Older server packages do not expose workspace state. Fail closed so
        // those clients cannot be offered as destructive merge sources.
        client.HasClientInfoWorkspace = GetBoolean(
            reader,
            "HasClientInfoWorkspace",
            true);
        client.ClientInfoReviewStatus = GetString(reader, "ClientInfoReviewStatus");
        client.RowVersion = GetBytes(reader, "RowVersion");
        TrackRowVersion("Client", client.Id, reader);
        return client;
    }

    private Ticket ReadTicket(SqlDataReader reader)
    {
        var ticket = new Ticket
        {
            Id = GetInt32(reader, "Id"),
            TicketNumber = GetString(reader, "TicketNumber"),
            ClientId = GetInt32(reader, "ClientId"),
            Subject = GetString(reader, "Subject"),
            Status = GetString(reader, "Status", "Open"),
            Source = GetString(reader, "Source", "Manual"),
            ExternalId = GetNullableString(reader, "ExternalId"),
            WhdStatusTypeId = GetNullableInt32(reader, "WhdStatusTypeId"),
            IsClosed = GetBoolean(reader, "IsClosed"),
            LastSyncedAt = GetNullableDateTime(reader, "LastSyncedAt")
        };
        ticket.RowVersion = GetBytes(reader, "RowVersion");
        TrackRowVersion("Ticket", ticket.Id, reader);
        return ticket;
    }

    private TicketStatusOption ReadTicketStatusOption(SqlDataReader reader)
    {
        var option = new TicketStatusOption
        {
            Id = GetInt32(reader, "Id"),
            Name = GetString(reader, "Name"),
            Source = GetString(reader, "Source", "WHD"),
            ExternalId = GetNullableString(reader, "ExternalId"),
            WhdStatusTypeId = GetNullableInt32(reader, "WhdStatusTypeId"),
            IsClosed = GetBoolean(reader, "IsClosed"),
            LastSyncedAt = GetNullableDateTime(reader, "LastSyncedAt")
        };
        option.RowVersion = GetBytes(reader, "RowVersion");
        TrackRowVersion("TicketStatusOption", option.Id, reader);
        return option;
    }

    private static void AddTicketParameters(
        SqlCommand command,
        Ticket ticket,
        byte[]? expectedRowVersion)
    {
        AddInt(command, "@Id", ticket.Id > 0 ? ticket.Id : null);
        AddRequiredText(command, "@TicketNumber", 120, ticket.TicketNumber);
        AddInt(command, "@ClientId", ticket.ClientId);
        AddRequiredText(command, "@Subject", 500, ticket.Subject, trim: false);
        AddRequiredText(command, "@Status", 160, ticket.Status);
        AddRequiredText(command, "@Source", 40, ticket.Source);
        AddText(command, "@ExternalId", 240, ticket.ExternalId);
        AddInt(command, "@WhdStatusTypeId", ticket.WhdStatusTypeId);
        AddBit(command, "@IsClosed", ticket.IsClosed);
        AddDateTime(command, "@LastSyncedAtUtc", ticket.LastSyncedAt);
        AddBinary(command, "@ExpectedRowVersion", 8, expectedRowVersion);
        AddGuid(command, "@RequestId", Guid.NewGuid());
    }

    private static void AddTicketSyncParameters(SqlCommand command, Ticket ticket)
    {
        AddRequiredText(command, "@ExternalId", 240, ticket.ExternalId ?? ticket.TicketNumber);
        AddRequiredText(command, "@TicketNumber", 120, ticket.TicketNumber);
        AddInt(command, "@ClientId", ticket.ClientId);
        AddRequiredText(command, "@Subject", 500, ticket.Subject, trim: false);
        AddRequiredText(command, "@Status", 120, ticket.Status);
        AddInt(command, "@WhdStatusTypeId", ticket.WhdStatusTypeId);
        AddBit(command, "@IsClosed", ticket.IsClosed);
        AddDateTime(command, "@LastSyncedAtUtc", ticket.LastSyncedAt ?? DateTime.Now);
    }

    private static void AddClientSyncParameters(SqlCommand command, Client client)
    {
        AddRequiredText(command, "@Name", 240, client.Name);
        AddRequiredText(command, "@Source", 80, client.Source);
        AddText(command, "@ExternalId", 500, client.ExternalId);
        AddBit(command, "@IsActive", client.IsActive);
        AddDateTime(command, "@SyncedAtUtc", client.LastSyncedAt ?? DateTime.Now);
        AddText(command, "@WhdLocationName", 240, client.WhdLocationName);
        AddText(command, "@WhdContactName", 240, client.WhdContactName);
        AddText(command, "@SageCustomerId", 120, client.SageCustomerId);
        AddText(command, "@SageCustomerName", 240, client.SageCustomerName);
        AddText(command, "@SageContactName", 240, client.SageContactName);
        AddText(command, "@SageTelephone", 80, client.SageTelephone);
        AddRequiredText(command, "@MatchStatus", 80, client.MatchStatus);
    }

    private static void CopyClient(Client source, Client target)
    {
        target.Id = source.Id;
        target.Name = source.Name;
        target.Source = source.Source;
        target.ExternalId = source.ExternalId;
        target.IsActive = source.IsActive;
        target.LastSyncedAt = source.LastSyncedAt;
        target.WhdLocationName = source.WhdLocationName;
        target.WhdContactName = source.WhdContactName;
        target.WhdContactEmail = source.WhdContactEmail;
        target.WhdPhone = source.WhdPhone;
        target.WhdAddress = source.WhdAddress;
        target.SageCustomerId = source.SageCustomerId;
        target.SageCustomerName = source.SageCustomerName;
        target.SageContactName = source.SageContactName;
        target.SageTelephone = source.SageTelephone;
        target.MatchStatus = source.MatchStatus;
        target.HasWhdIdentity = source.HasWhdIdentity;
        target.HasSageIdentity = source.HasSageIdentity;
        target.IsClientInfoLive = source.IsClientInfoLive;
        target.HasClientInfoWorkspace = source.HasClientInfoWorkspace;
        target.ClientInfoReviewStatus = source.ClientInfoReviewStatus;
        target.RowVersion = source.RowVersion;
    }

    private static void CopyTicket(Ticket source, Ticket target)
    {
        target.Id = source.Id;
        target.TicketNumber = source.TicketNumber;
        target.ClientId = source.ClientId;
        target.Subject = source.Subject;
        target.Status = source.Status;
        target.Source = source.Source;
        target.ExternalId = source.ExternalId;
        target.WhdStatusTypeId = source.WhdStatusTypeId;
        target.IsClosed = source.IsClosed;
        target.LastSyncedAt = source.LastSyncedAt;
        target.RowVersion = source.RowVersion;
    }

    private static void CopyTicketStatusOption(
        TicketStatusOption source,
        TicketStatusOption target)
    {
        target.Id = source.Id;
        target.Name = source.Name;
        target.Source = source.Source;
        target.ExternalId = source.ExternalId;
        target.WhdStatusTypeId = source.WhdStatusTypeId;
        target.IsClosed = source.IsClosed;
        target.LastSyncedAt = source.LastSyncedAt;
        target.RowVersion = source.RowVersion;
    }
}
