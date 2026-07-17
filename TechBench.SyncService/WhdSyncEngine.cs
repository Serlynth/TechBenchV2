using System.Text.Json;
using Microsoft.Extensions.Options;
using TechBench.Models;
using TechBench.Providers;

namespace TechBench.SyncService;

public sealed class WhdSyncEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SyncSqlRepository _repository;
    private readonly WhdRestClient _whd;
    private readonly WhdSecretStore _secretStore;
    private readonly SyncServiceOptions _options;

    public WhdSyncEngine(
        SyncSqlRepository repository,
        WhdRestClient whd,
        WhdSecretStore secretStore,
        IOptions<SyncServiceOptions> options)
    {
        _repository = repository;
        _whd = whd;
        _secretStore = secretStore;
        _options = options.Value;
    }

    public async Task<WhdSyncExecutionResult> ExecuteAsync(
        WhdSyncWork work,
        Guid workerId,
        CancellationToken cancellationToken)
    {
        var configuration = await _repository
            .GetConfigurationAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!configuration.IsConfigured)
        {
            throw new InvalidOperationException(
                "The shared WHD base URL and service username must be configured by a TechBench Admin.");
        }

        var connection = new WhdConnectionSettings
        {
            BaseUrl = configuration.BaseUrl,
            Username = configuration.Username,
            AuthenticationMode = configuration.AuthenticationMode,
            Secret = _secretStore.Read()
        };

        return work.WorkType.ToUpperInvariant() switch
        {
            "CLIENTS" => await SyncClientsAsync(work, workerId, connection, cancellationToken).ConfigureAwait(false),
            "STATUSES" => await SyncStatusesAsync(work, workerId, connection, cancellationToken).ConfigureAwait(false),
            "TECHNICIANS" => await SyncTechniciansAsync(work, workerId, connection, cancellationToken).ConfigureAwait(false),
            "GROUPS" => await SyncGroupsAsync(work, workerId, connection, cancellationToken).ConfigureAwait(false),
            "TICKETS" => await SyncTicketsAsync(work, workerId, connection, configuration, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"Unknown WHD synchronization work type '{work.WorkType}'.")
        };
    }

    private async Task<WhdSyncExecutionResult> SyncClientsAsync(
        WhdSyncWork work,
        Guid workerId,
        WhdConnectionSettings connection,
        CancellationToken cancellationToken)
    {
        var result = await _whd.GetClientsAsync(connection, cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(result.Success, result.IsComplete, result.Message);
        var syncedAt = DateTimeOffset.UtcNow;
        await _repository.ApplyClientsAsync(
            work,
            workerId,
            Serialize(result.Clients),
            syncedAt,
            cancellationToken).ConfigureAwait(false);
        return Completed(result.Clients.Count, result.Message);
    }

    private async Task<WhdSyncExecutionResult> SyncStatusesAsync(
        WhdSyncWork work,
        Guid workerId,
        WhdConnectionSettings connection,
        CancellationToken cancellationToken)
    {
        var result = await _whd.GetStatusTypesAsync(connection, cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(result.Success, isComplete: true, result.Message);
        var payload = result.StatusTypes.Select(static status => new
        {
            externalId = status.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            whdStatusTypeId = status.Id,
            name = status.Name,
            isClosed = status.IsClosed
        });
        await _repository.ApplyStatusesAsync(
            work,
            workerId,
            Serialize(payload),
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);
        return Completed(result.StatusTypes.Count, result.Message);
    }

    private async Task<WhdSyncExecutionResult> SyncTechniciansAsync(
        WhdSyncWork work,
        Guid workerId,
        WhdConnectionSettings connection,
        CancellationToken cancellationToken)
    {
        var result = await _whd.GetTechniciansAsync(connection, cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(result.Success, result.IsComplete, result.Message);
        await _repository.ApplyTechniciansAsync(
            work,
            workerId,
            Serialize(result.Technicians),
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);
        return Completed(result.Technicians.Count, result.Message);
    }

    private async Task<WhdSyncExecutionResult> SyncGroupsAsync(
        WhdSyncWork work,
        Guid workerId,
        WhdConnectionSettings connection,
        CancellationToken cancellationToken)
    {
        var result = await _whd.GetTechnicianGroupsAsync(connection, cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(result.Success, result.IsComplete, result.Message);
        await _repository.ApplyGroupsAsync(
            work,
            workerId,
            Serialize(result.Groups),
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);
        return Completed(result.Groups.Count, result.Message);
    }

    private async Task<WhdSyncExecutionResult> SyncTicketsAsync(
        WhdSyncWork work,
        Guid workerId,
        WhdConnectionSettings connection,
        WhdServiceConfiguration configuration,
        CancellationToken cancellationToken)
    {
        // The cursor is the UTC upper bound captured before the request. The
        // next delta subtracts an overlap, so changes on a paging boundary are
        // safely repeated and idempotently upserted.
        var nextCursor = DateTimeOffset.UtcNow;
        var cursor = work.CursorUtc ?? configuration.CursorUtc;
        var result = work.IsFullSync || cursor is null
            ? await _whd.GetOrganizationTicketsAsync(connection, cancellationToken).ConfigureAwait(false)
            : await _whd.GetOrganizationTicketsChangedSinceAsync(
                connection,
                cursor.Value.Subtract(_options.DeltaOverlap),
                cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(result.Success, result.IsComplete, result.Message);

        var clients = result.Tickets
            .Select(static ticket => ticket.Client)
            .Where(static client => !string.IsNullOrWhiteSpace(client.ExternalId))
            .GroupBy(static client => client.ExternalId, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
        var syncedAt = DateTimeOffset.UtcNow;
        if (clients.Count > 0)
        {
            await _repository.ApplyClientsAsync(
                work,
                workerId,
                Serialize(clients),
                syncedAt,
                cancellationToken).ConfigureAwait(false);
        }

        var tickets = result.Tickets.Select(static ticket => new
        {
            ticket.ExternalId,
            ticket.TicketNumber,
            ticket.Subject,
            ticket.Status,
            ticket.StatusTypeId,
            ClientExternalId = ticket.Client.ExternalId,
            ticket.IsClosed,
            ticket.IsDeleted,
            ticket.LastUpdatedUtc,
            ticket.AssignedTechnicianExternalId,
            ticket.AssignedTechnicianName,
            ticket.AssignedGroupExternalId,
            ticket.AssignedGroupName
        });
        await _repository.ApplyTicketsAsync(
            work,
            workerId,
            Serialize(tickets),
            syncedAt,
            cancellationToken).ConfigureAwait(false);

        return new WhdSyncExecutionResult(
            new WhdSyncCounts(result.Tickets.Count, result.Tickets.Count + clients.Count, result.Tickets.Count + clients.Count),
            nextCursor,
            result.Message);
    }

    private static void EnsureSucceeded(bool success, bool isComplete, string message)
    {
        if (!success)
        {
            throw new InvalidOperationException(message);
        }

        if (!isComplete)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(message)
                    ? "Web Help Desk returned an incomplete paged result. No cursor was advanced."
                    : $"{message} No cursor was advanced.");
        }
    }

    private static WhdSyncExecutionResult Completed(int count, string message) => new(
        new WhdSyncCounts(0, count, count),
        null,
        message);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
