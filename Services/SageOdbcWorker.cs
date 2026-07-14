using System.Text.Json;

namespace TechBench.Services;

public static class SageOdbcWorker
{
    public const string WorkerArgument = "--sage-odbc-worker";
    public const string VerifyOperation = "verify-time-ticket";
    public const string CustomersOperation = "read-customers";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync()
    {
        SageOdbcWorkerResponse response;
        try
        {
            var input = await Console.In.ReadToEndAsync();
            var request = JsonSerializer.Deserialize<SageOdbcWorkerRequest>(input, JsonOptions)
                ?? throw new InvalidOperationException("The Sage ODBC worker request was empty.");
            response = Execute(request);
        }
        catch (Exception ex)
        {
            response = new SageOdbcWorkerResponse(false, ex.Message);
        }

        await Console.Out.WriteAsync(JsonSerializer.Serialize(response, JsonOptions));
        await Console.Out.FlushAsync();
        return response.Success ? 0 : 1;
    }

    internal static SageOdbcWorkerResponse Execute(SageOdbcWorkerRequest request)
    {
        return request.Operation switch
        {
            VerifyOperation when request.Verification is not null => new SageOdbcWorkerResponse(
                true,
                Verification: new SageOdbcTimeTicketVerifier().Verify(
                    request.Dsn,
                    request.Username,
                    request.Password,
                    request.Verification)),
            CustomersOperation => new SageOdbcWorkerResponse(
                true,
                Customers: new SageOdbcCustomerReader().ReadCustomers(
                    request.Dsn,
                    request.Username,
                    request.Password,
                    request.MaxRows,
                    request.IncludeInactive)),
            _ => throw new InvalidOperationException($"Unsupported Sage ODBC worker operation '{request.Operation}'.")
        };
    }
}
