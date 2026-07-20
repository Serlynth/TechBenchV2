using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TechBench.SyncService;

public interface ISageOdbcWorkerProcessClient
{
    Task<IReadOnlyList<SageSyncCustomer>> ReadCustomersAsync(
        string dsn,
        string username,
        string password,
        CancellationToken cancellationToken);
}

public sealed class SageOdbcWorkerProcessClient : ISageOdbcWorkerProcessClient
{
    private const string ReadCustomersOperation = "read-customers";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SyncServiceOptions _options;

    public SageOdbcWorkerProcessClient(IOptions<SyncServiceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public async Task<IReadOnlyList<SageSyncCustomer>> ReadCustomersAsync(
        string dsn,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var workerPath = _options.ResolveSageOdbcWorkerPath();
        if (!File.Exists(workerPath))
        {
            throw new InvalidOperationException(
                $"The 32-bit Sage ODBC worker is missing: {workerPath}");
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = workerPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(workerPath) ?? AppContext.BaseDirectory
            }
        };
        using var workerJob = WindowsKillOnCloseJob.Create();
        Task<string>? standardOutput = null;
        Task<string>? standardError = null;
        var processStarted = false;
        var processExited = false;
        var requestJson = string.Empty;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("TechBench could not start its 32-bit Sage ODBC worker.");
            }

            processStarted = true;
            workerJob.Add(process);
            standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
            standardError = process.StandardError.ReadToEndAsync(cancellationToken);
            requestJson = JsonSerializer.Serialize(
                new SageOdbcWorkerRequest(ReadCustomersOperation, dsn, username, password),
                JsonOptions);
            await process.StandardInput.WriteAsync(requestJson.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            requestJson = string.Empty;

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(_options.SageOdbcTimeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
                processExited = true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Sage ODBC did not respond within {_options.SageOdbcTimeout.TotalSeconds:0} seconds. The isolated 32-bit worker was stopped.");
            }

            var output = await standardOutput.ConfigureAwait(false);
            var error = await standardError.ConfigureAwait(false);
            SageOdbcWorkerResponse? response;
            try
            {
                response = JsonSerializer.Deserialize<SageOdbcWorkerResponse>(output, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"The Sage ODBC worker returned an invalid response{FormatWorkerError(error, password)}.",
                    ex);
            }

            if (response is null)
            {
                throw new InvalidOperationException(
                    $"The Sage ODBC worker returned no response{FormatWorkerError(error, password)}.");
            }

            if (!response.Success)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(response.Error)
                        ? $"The Sage ODBC worker failed{FormatWorkerError(error, password)}."
                        : response.Error);
            }

            return response.Customers ?? Array.Empty<SageSyncCustomer>();
        }
        finally
        {
            requestJson = string.Empty;
            if (processStarted && !processExited)
            {
                TryTerminate(process);
            }

            if (standardOutput is not null && standardError is not null)
            {
                await ObserveOutputAfterTerminationAsync(standardOutput, standardError).ConfigureAwait(false);
            }
        }
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static async Task ObserveOutputAfterTerminationAsync(
        Task<string> output,
        Task<string> error)
    {
        try
        {
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
        {
        }
    }

    private static string FormatWorkerError(string error, string password)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return string.Empty;
        }

        var redacted = string.IsNullOrEmpty(password)
            ? error
            : error.Replace(password, "[redacted]", StringComparison.Ordinal);
        return $": {redacted.Trim()}";
    }

    private sealed record SageOdbcWorkerRequest(
        string Operation,
        string Dsn,
        string Username,
        string Password);

    private sealed record SageOdbcWorkerResponse(
        bool Success,
        string? Error = null,
        IReadOnlyList<SageSyncCustomer>? Customers = null);
}
