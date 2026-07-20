using System.Diagnostics;
using TechBench.SyncService;

namespace TechBench.Tests;

public sealed class SyncWorkLifecycleTests
{
    [Fact]
    public async Task SuccessFinalizationFailureNeverDowngradesAppliedWorkToFailed()
    {
        using var executionCancellation = new CancellationTokenSource();
        var failureCompletionCalls = 0;

        var lifecycle = await SyncWorkLifecycle.RunAsync(
            _ => Task.FromResult(new TestResult("applied")),
            Task.CompletedTask,
            executionCancellation,
            CancellationToken.None,
            TimeSpan.FromSeconds(1),
            (_, _) => Task.FromException(new IOException("success completion unavailable")),
            (_, _) =>
            {
                failureCompletionCalls++;
                return Task.CompletedTask;
            });

        Assert.Equal(SyncWorkLifecycleOutcome.AppliedButNotFinalized, lifecycle.Outcome);
        Assert.IsType<IOException>(lifecycle.Failure);
        Assert.Equal(0, failureCompletionCalls);
    }

    [Fact]
    public async Task HeartbeatFailureReplacesCancellationAsDurableRootCause()
    {
        using var executionCancellation = new CancellationTokenSource();
        var heartbeatFailure = new InvalidOperationException("lease renewal root cause");
        Exception? completedFailure = null;

        var lifecycle = await SyncWorkLifecycle.RunAsync<TestResult>(
            _ => Task.FromException<TestResult>(new OperationCanceledException("generic cancellation")),
            Task.FromException(heartbeatFailure),
            executionCancellation,
            CancellationToken.None,
            TimeSpan.FromSeconds(1),
            (_, _) => Task.CompletedTask,
            (failure, _) =>
            {
                completedFailure = failure;
                return Task.CompletedTask;
            });

        Assert.Equal(SyncWorkLifecycleOutcome.Failed, lifecycle.Outcome);
        Assert.Same(heartbeatFailure, lifecycle.Failure);
        Assert.Same(heartbeatFailure, completedFailure);
    }

    [Fact]
    public async Task SuccessFinalizationUsesIndependentBoundedToken()
    {
        using var executionCancellation = new CancellationTokenSource();
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var lifecycle = await SyncWorkLifecycle.RunAsync(
            _ => Task.FromResult(new TestResult("applied")),
            Task.CompletedTask,
            executionCancellation,
            CancellationToken.None,
            TimeSpan.FromMilliseconds(50),
            (_, _) => neverCompletes.Task,
            (_, _) => Task.CompletedTask);

        Assert.Equal(SyncWorkLifecycleOutcome.AppliedButNotFinalized, lifecycle.Outcome);
        Assert.IsType<TimeoutException>(lifecycle.Failure);
    }

    [Fact]
    public async Task ServiceShutdownLeavesLeaseForRetryWithoutFinalization()
    {
        using var stopping = new CancellationTokenSource();
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
        stopping.Cancel();
        var completionCalls = 0;

        var lifecycle = await SyncWorkLifecycle.RunAsync<TestResult>(
            _ => Task.FromException<TestResult>(new OperationCanceledException(stopping.Token)),
            Task.CompletedTask,
            executionCancellation,
            stopping.Token,
            TimeSpan.FromSeconds(1),
            (_, _) =>
            {
                completionCalls++;
                return Task.CompletedTask;
            },
            (_, _) =>
            {
                completionCalls++;
                return Task.CompletedTask;
            });

        Assert.Equal(SyncWorkLifecycleOutcome.Interrupted, lifecycle.Outcome);
        Assert.Equal(0, completionCalls);
    }

    [Fact]
    public async Task ClosingJobObjectTerminatesAssignedWorker()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = powershell,
                Arguments = "-NoLogo -NoProfile -NonInteractive -Command Start-Sleep -Seconds 30",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        using var job = WindowsKillOnCloseJob.Create();
        try
        {
            Assert.True(process.Start());
            job.Add(process);
            await Task.Delay(150);
            Assert.False(process.HasExited);

            job.Dispose();

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(process.HasExited);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private sealed record TestResult(string Message);
}
