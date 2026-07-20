namespace TechBench.SyncService;

internal enum SyncWorkLifecycleOutcome
{
    Succeeded,
    Failed,
    Interrupted,
    AppliedButNotFinalized,
    FailureNotFinalized
}

internal sealed record SyncWorkLifecycleResult<TResult>(
    SyncWorkLifecycleOutcome Outcome,
    TResult? ExecutionResult = null,
    Exception? Failure = null,
    Exception? FinalizationFailure = null)
    where TResult : class;

internal static class SyncWorkLifecycle
{
    public static async Task<SyncWorkLifecycleResult<TResult>> RunAsync<TResult>(
        Func<CancellationToken, Task<TResult>> execute,
        Task heartbeat,
        CancellationTokenSource executionCancellation,
        CancellationToken stoppingToken,
        TimeSpan finalizationTimeout,
        Func<TResult, CancellationToken, Task> completeSuccess,
        Func<Exception, CancellationToken, Task> completeFailure)
        where TResult : class
    {
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(heartbeat);
        ArgumentNullException.ThrowIfNull(executionCancellation);
        ArgumentNullException.ThrowIfNull(completeSuccess);
        ArgumentNullException.ThrowIfNull(completeFailure);
        if (finalizationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(finalizationTimeout));
        }

        TResult executionResult;
        try
        {
            executionResult = await execute(executionCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            executionCancellation.Cancel();
            await CaptureHeartbeatFailureAsync(heartbeat).ConfigureAwait(false);
            return new SyncWorkLifecycleResult<TResult>(SyncWorkLifecycleOutcome.Interrupted);
        }
        catch (Exception executionFailure)
        {
            executionCancellation.Cancel();
            var heartbeatFailure = await CaptureHeartbeatFailureAsync(heartbeat).ConfigureAwait(false);
            var effectiveFailure = heartbeatFailure ?? executionFailure;
            var finalizationFailure = await TryFinalizeAsync(
                    token => completeFailure(effectiveFailure, token),
                    finalizationTimeout)
                .ConfigureAwait(false);
            return new SyncWorkLifecycleResult<TResult>(
                finalizationFailure is null
                    ? SyncWorkLifecycleOutcome.Failed
                    : SyncWorkLifecycleOutcome.FailureNotFinalized,
                Failure: effectiveFailure,
                FinalizationFailure: finalizationFailure);
        }

        executionCancellation.Cancel();
        var lateHeartbeatFailure = await CaptureHeartbeatFailureAsync(heartbeat).ConfigureAwait(false);
        if (lateHeartbeatFailure is not null)
        {
            return new SyncWorkLifecycleResult<TResult>(
                SyncWorkLifecycleOutcome.AppliedButNotFinalized,
                executionResult,
                lateHeartbeatFailure);
        }

        var successFinalizationFailure = await TryFinalizeAsync(
                token => completeSuccess(executionResult, token),
                finalizationTimeout)
            .ConfigureAwait(false);
        return successFinalizationFailure is null
            ? new SyncWorkLifecycleResult<TResult>(
                SyncWorkLifecycleOutcome.Succeeded,
                executionResult)
            : new SyncWorkLifecycleResult<TResult>(
                SyncWorkLifecycleOutcome.AppliedButNotFinalized,
                executionResult,
                successFinalizationFailure);
    }

    private static async Task<Exception?> CaptureHeartbeatFailureAsync(Task heartbeat)
    {
        try
        {
            await heartbeat.ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static async Task<Exception?> TryFinalizeAsync(
        Func<CancellationToken, Task> finalizer,
        TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        Task? finalization = null;
        try
        {
            finalization = finalizer(timeoutSource.Token);
            await finalization.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException ex) when (timeoutSource.IsCancellationRequested)
        {
            if (finalization is not null && !finalization.IsCompleted)
            {
                _ = ObserveLateFinalizationAsync(finalization);
            }

            return new TimeoutException(
                $"Sync work finalization did not complete within {timeout.TotalSeconds:0.###} seconds.",
                ex);
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static async Task ObserveLateFinalizationAsync(Task finalization)
    {
        try
        {
            await finalization.ConfigureAwait(false);
        }
        catch
        {
            // The bounded caller has already recorded the finalization timeout.
        }
    }
}
