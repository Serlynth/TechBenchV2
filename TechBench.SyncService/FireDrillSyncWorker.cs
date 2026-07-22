using Microsoft.Extensions.Options;

namespace TechBench.SyncService;

public sealed class FireDrillSyncWorker : BackgroundService
{
    private readonly SyncSqlRepository _repository;
    private readonly FireDrillSyncEngine _engine;
    private readonly SyncServiceOptions _options;
    private readonly ILogger<FireDrillSyncWorker> _logger;
    private readonly Guid _workerId = Guid.NewGuid();

    public FireDrillSyncWorker(SyncSqlRepository repository, FireDrillSyncEngine engine,
        IOptions<SyncServiceOptions> options, ILogger<FireDrillSyncWorker> logger)
    {
        _repository = repository; _engine = engine; _options = options.Value; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TechBench Credentials synchronization worker {WorkerId} started.", _workerId);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var work = await _repository.ClaimFireDrillWorkAsync(_workerId, stoppingToken).ConfigureAwait(false);
                if (work is not null) await ProcessAsync(work, stoppingToken).ConfigureAwait(false);
                else await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The Credentials synchronization poll failed.");
                try { await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task ProcessAsync(FireDrillSyncWork work, CancellationToken stoppingToken)
    {
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = RenewUntilCancelledAsync(work, executionCancellation, stoppingToken);
        var lifecycle = await SyncWorkLifecycle.RunAsync(
            token => _engine.ExecuteAsync(work, _workerId, token), heartbeat, executionCancellation,
            stoppingToken, _options.FinalizationTimeout,
            (result, token) => _repository.CompleteFireDrillWorkAsync(work, _workerId, true, result.Message, result.SourceModifiedAtUtc, token),
            (failure, token) => _repository.CompleteFireDrillWorkAsync(work, _workerId, false, failure.Message, null, token)).ConfigureAwait(false);
        if (lifecycle.Outcome == SyncWorkLifecycleOutcome.Succeeded)
            _logger.LogInformation("Completed Credentials synchronization: {Message}", lifecycle.ExecutionResult!.Message);
        else if (lifecycle.Outcome != SyncWorkLifecycleOutcome.Interrupted)
            _logger.LogError(lifecycle.Failure, "Credentials synchronization failed with outcome {Outcome}.", lifecycle.Outcome);
    }

    private async Task RenewUntilCancelledAsync(FireDrillSyncWork work, CancellationTokenSource executionCancellation, CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(15, _options.EffectiveLeaseSeconds / 3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(executionCancellation.Token, stoppingToken);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                await Task.Delay(interval, linked.Token).ConfigureAwait(false);
                await _repository.RenewFireDrillLeaseAsync(work, _workerId, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch { executionCancellation.Cancel(); throw; }
    }
}
