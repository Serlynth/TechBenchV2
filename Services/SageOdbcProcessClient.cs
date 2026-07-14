using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using TechBench.Models;

namespace TechBench.Services;

public interface ISageOdbcProcessClient
{
    Task<SageTimeTicketVerificationResult> VerifyTimeTicketAsync(
        string dsn,
        string username,
        string password,
        SageTimeTicketVerificationRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SageCustomer>> ReadCustomersAsync(
        string dsn,
        string username,
        string password,
        int maxRows = 0,
        bool includeInactive = false,
        CancellationToken cancellationToken = default);
}

public sealed class SageOdbcProcessClient : ISageOdbcProcessClient
{
    internal static readonly TimeSpan VerificationTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan CustomerReadTimeout = TimeSpan.FromSeconds(75);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SageTimeTicketVerificationResult> VerifyTimeTicketAsync(
        string dsn,
        string username,
        string password,
        SageTimeTicketVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await ExecuteAsync(
            new SageOdbcWorkerRequest(
                SageOdbcWorker.VerifyOperation,
                dsn,
                username,
                password,
                request,
                MaxRows: 0,
                IncludeInactive: false),
            VerificationTimeout,
            cancellationToken);

        return response.Verification
            ?? throw new InvalidOperationException("The Sage ODBC worker did not return a verification result.");
    }

    public async Task<IReadOnlyList<SageCustomer>> ReadCustomersAsync(
        string dsn,
        string username,
        string password,
        int maxRows = 0,
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var response = await ExecuteAsync(
            new SageOdbcWorkerRequest(
                SageOdbcWorker.CustomersOperation,
                dsn,
                username,
                password,
                Verification: null,
                maxRows,
                includeInactive),
            CustomerReadTimeout,
            cancellationToken);

        return response.Customers ?? Array.Empty<SageCustomer>();
    }

    private static async Task<SageOdbcWorkerResponse> ExecuteAsync(
        SageOdbcWorkerRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = BuildStartInfo() };
        if (!process.Start())
        {
            throw new InvalidOperationException("TechBench could not start its Sage ODBC worker.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request, JsonOptions));
        await process.StandardInput.FlushAsync(cancellationToken);
        process.StandardInput.Close();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryTerminate(process);
            await AwaitOutputAfterTerminationAsync(standardOutput, standardError);
            throw new TimeoutException(
                $"Sage ODBC did not respond within {timeout.TotalSeconds:0} seconds. The isolated ODBC worker was stopped.");
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process);
            await AwaitOutputAfterTerminationAsync(standardOutput, standardError);
            throw;
        }

        var output = await standardOutput;
        var error = await standardError;
        SageOdbcWorkerResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<SageOdbcWorkerResponse>(output, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"The Sage ODBC worker returned an invalid response{FormatWorkerError(error)}.",
                ex);
        }

        if (response is null)
        {
            throw new InvalidOperationException(
                $"The Sage ODBC worker returned no response{FormatWorkerError(error)}.");
        }

        if (!response.Success)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(response.Error)
                    ? $"The Sage ODBC worker failed{FormatWorkerError(error)}."
                    : response.Error);
        }

        return response;
    }

    private static ProcessStartInfo BuildStartInfo()
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("TechBench could not determine its executable path.");
        var runningThroughDotnet = Path.GetFileNameWithoutExtension(processPath)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase);

        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        if (runningThroughDotnet)
        {
            var assemblyName = Assembly.GetEntryAssembly()?.GetName().Name;
            var assemblyPath = string.IsNullOrWhiteSpace(assemblyName)
                ? null
                : Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll");
            if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
            {
                throw new InvalidOperationException("TechBench could not locate its application assembly.");
            }

            startInfo.ArgumentList.Add(assemblyPath);
        }

        startInfo.ArgumentList.Add(SageOdbcWorker.WorkerArgument);
        return startInfo;
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

    private static async Task AwaitOutputAfterTerminationAsync(Task<string> output, Task<string> error)
    {
        try
        {
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException)
        {
        }
    }

    private static string FormatWorkerError(string error) =>
        string.IsNullOrWhiteSpace(error) ? string.Empty : $": {error.Trim()}";
}

internal sealed class InProcessSageOdbcClient(ISageTimeTicketVerifier verifier) : ISageOdbcProcessClient
{
    public Task<SageTimeTicketVerificationResult> VerifyTimeTicketAsync(
        string dsn,
        string username,
        string password,
        SageTimeTicketVerificationRequest request,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(verifier.Verify(dsn, username, password, request));

    public Task<IReadOnlyList<SageCustomer>> ReadCustomersAsync(
        string dsn,
        string username,
        string password,
        int maxRows = 0,
        bool includeInactive = false,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

internal sealed record SageOdbcWorkerRequest(
    string Operation,
    string Dsn,
    string Username,
    string Password,
    SageTimeTicketVerificationRequest? Verification,
    int MaxRows,
    bool IncludeInactive);

internal sealed record SageOdbcWorkerResponse(
    bool Success,
    string? Error = null,
    SageTimeTicketVerificationResult? Verification = null,
    IReadOnlyList<SageCustomer>? Customers = null);
