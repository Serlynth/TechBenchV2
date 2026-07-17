using Microsoft.Extensions.Options;

namespace TechBench.SyncService;

public sealed class WhdSyncWorker : BackgroundService
{
    private readonly SyncSqlRepository _repository;
    private readonly WhdSyncEngine _engine;
    private readonly SyncServiceOptions _options;
    private readonly ILogger<WhdSyncWorker> _logger;
    private readonly Guid _workerId = Guid.NewGuid();

    public WhdSyncWorker(
        SyncSqlRepository repository,
        WhdSyncEngine engine,
        IOptions<SyncServiceOptions> options,
        ILogger<WhdSyncWorker> logger)
    {
        _repository = repository;
        _engine = engine;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TechBench WHD Sync Service worker {WorkerId} started.", _workerId);
        while (!stoppingToken.IsCancellationRequested)
        {
            var processedWork = false;
            try
            {
                // Drain a request without waiting for another poll, but yield
                // after a bounded number so service shutdown remains prompt.
                for (var index = 0; index < 25 && !stoppingToken.IsCancellationRequested; index++)
                {
                    var work = await _repository.ClaimWorkAsync(_workerId, stoppingToken).ConfigureAwait(false);
                    if (work is null)
                    {
                        break;
                    }

                    processedWork = true;
                    await ProcessWorkAsync(work, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The WHD synchronization poll failed.");
            }

            if (!processedWork)
            {
                try
                {
                    await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        _logger.LogInformation("TechBench WHD Sync Service worker {WorkerId} stopped.", _workerId);
    }

    private async Task ProcessWorkAsync(WhdSyncWork work, CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Starting WHD {WorkType} work {WorkId} (full: {IsFullSync}).",
            work.WorkType,
            work.WorkId,
            work.IsFullSync);

        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = RenewLeaseUntilCancelledAsync(work, executionCancellation, stoppingToken);
        try
        {
            var result = await _engine
                .ExecuteAsync(work, _workerId, executionCancellation.Token)
                .ConfigureAwait(false);
            executionCancellation.Cancel();
            await ObserveHeartbeatAsync(heartbeat).ConfigureAwait(false);
            await _repository.CompleteWorkAsync(
                work,
                _workerId,
                succeeded: true,
                result.NextCursorUtc,
                result.Message,
                CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation(
                "Completed WHD {WorkType} work {WorkId}: {Message}",
                work.WorkType,
                work.WorkId,
                result.Message);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            executionCancellation.Cancel();
            await ObserveHeartbeatAsync(heartbeat).ConfigureAwait(false);
            // Leave the durable lease to expire so another service process can
            // retry the idempotent work after an orderly server shutdown.
            _logger.LogInformation("WHD work {WorkId} was interrupted by service shutdown.", work.WorkId);
        }
        catch (Exception ex)
        {
            executionCancellation.Cancel();
            await ObserveHeartbeatAsync(heartbeat).ConfigureAwait(false);
            _logger.LogError(ex, "WHD {WorkType} work {WorkId} failed.", work.WorkType, work.WorkId);
            try
            {
                await _repository.CompleteWorkAsync(
                    work,
                    _workerId,
                    succeeded: false,
                    nextCursorUtc: null,
                    ex.Message,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception completionException)
            {
                _logger.LogError(
                    completionException,
                    "Could not record failure for WHD work {WorkId}; its lease will expire for retry.",
                    work.WorkId);
            }
        }
    }

    private async Task RenewLeaseUntilCancelledAsync(
        WhdSyncWork work,
        CancellationTokenSource executionCancellation,
        CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(15, _options.EffectiveLeaseSeconds / 3));
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            executionCancellation.Token,
            stoppingToken);
        try
        {
            while (!heartbeatCancellation.IsCancellationRequested)
            {
                await Task.Delay(interval, heartbeatCancellation.Token).ConfigureAwait(false);
                await _repository
                    .RenewLeaseAsync(work, _workerId, heartbeatCancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (heartbeatCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lease renewal failed for WHD work {WorkId}.", work.WorkId);
            executionCancellation.Cancel();
            throw;
        }
    }

    private static async Task ObserveHeartbeatAsync(Task heartbeat)
    {
        try
        {
            await heartbeat.ConfigureAwait(false);
        }
        catch
        {
            // The execution path reports the actionable lease error.
        }
    }
}
