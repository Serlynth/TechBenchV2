using Microsoft.Extensions.Options;

namespace TechBench.SyncService;

public sealed class SageCustomerSyncWorker : BackgroundService
{
    private readonly SyncSqlRepository _repository;
    private readonly SageCustomerSyncEngine _engine;
    private readonly SyncServiceOptions _options;
    private readonly ILogger<SageCustomerSyncWorker> _logger;
    private readonly Guid _workerId = Guid.NewGuid();

    public SageCustomerSyncWorker(
        SyncSqlRepository repository,
        SageCustomerSyncEngine engine,
        IOptions<SyncServiceOptions> options,
        ILogger<SageCustomerSyncWorker> logger)
    {
        _repository = repository;
        _engine = engine;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TechBench Sage customer-sync worker {WorkerId} started.", _workerId);
        while (!stoppingToken.IsCancellationRequested)
        {
            var processedWork = false;
            try
            {
                for (var index = 0; index < 10 && !stoppingToken.IsCancellationRequested; index++)
                {
                    var work = await _repository
                        .ClaimSageWorkAsync(_workerId, stoppingToken)
                        .ConfigureAwait(false);
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
                _logger.LogError(ex, "The Sage customer synchronization poll failed.");
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

        _logger.LogInformation("TechBench Sage customer-sync worker {WorkerId} stopped.", _workerId);
    }

    private async Task ProcessWorkAsync(SageSyncWork work, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Sage customer-sync work {WorkId}.", work.WorkId);

        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = RenewLeaseUntilCancelledAsync(work, executionCancellation, stoppingToken);
        var lifecycle = await SyncWorkLifecycle.RunAsync(
                token => _engine.ExecuteAsync(work, _workerId, token),
                heartbeat,
                executionCancellation,
                stoppingToken,
                _options.FinalizationTimeout,
                (result, token) => _repository.CompleteSageWorkAsync(
                    work,
                    _workerId,
                    succeeded: true,
                    result.Message,
                    token),
                (failure, token) => _repository.CompleteSageWorkAsync(
                    work,
                    _workerId,
                    succeeded: false,
                    failure.Message,
                    token))
            .ConfigureAwait(false);

        switch (lifecycle.Outcome)
        {
            case SyncWorkLifecycleOutcome.Succeeded:
                _logger.LogInformation(
                    "Completed Sage customer-sync work {WorkId}: {Message}",
                    work.WorkId,
                    lifecycle.ExecutionResult!.Message);
                break;
            case SyncWorkLifecycleOutcome.Interrupted:
                _logger.LogInformation(
                    "Sage customer-sync work {WorkId} was interrupted by service shutdown.",
                    work.WorkId);
                break;
            case SyncWorkLifecycleOutcome.Failed:
                _logger.LogError(
                    lifecycle.Failure,
                    "Sage customer-sync work {WorkId} failed.",
                    work.WorkId);
                break;
            case SyncWorkLifecycleOutcome.FailureNotFinalized:
                _logger.LogError(
                    lifecycle.Failure,
                    "Sage customer-sync work {WorkId} failed.",
                    work.WorkId);
                _logger.LogError(
                    lifecycle.FinalizationFailure,
                    "Could not record failure for Sage work {WorkId}; its lease will expire for retry.",
                    work.WorkId);
                break;
            case SyncWorkLifecycleOutcome.AppliedButNotFinalized:
                _logger.LogError(
                    lifecycle.Failure,
                    "Sage customer-sync work {WorkId} applied its snapshot but could not record successful completion; its lease will expire for idempotent retry.",
                    work.WorkId);
                break;
        }
    }

    private async Task RenewLeaseUntilCancelledAsync(
        SageSyncWork work,
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
                    .RenewSageLeaseAsync(work, _workerId, heartbeatCancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (heartbeatCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lease renewal failed for Sage work {WorkId}.", work.WorkId);
            executionCancellation.Cancel();
            throw;
        }
    }

}
