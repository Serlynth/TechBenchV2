using System.Text.Json;
using TechBench.Models;
using TechBench.Services;

const string ReadCustomersOperation = "read-customers";
var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
SageOdbcWorkerResponse response;
SageOdbcWorkerRequest? request = null;

try
{
    var input = await Console.In.ReadToEndAsync().ConfigureAwait(false);
    request = JsonSerializer.Deserialize<SageOdbcWorkerRequest>(input, jsonOptions)
        ?? throw new InvalidOperationException("The Sage ODBC worker request was empty.");
    if (!request.Operation.Equals(ReadCustomersOperation, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"Unsupported Sage ODBC worker operation '{request.Operation}'.");
    }

    var customers = new SageOdbcCustomerReader().ReadCustomers(
        request.Dsn,
        request.Username,
        request.Password,
        maxRows: 0,
        includeInactive: false,
        // Match the established client-side Sage import behavior: records with
        // no usable ID cannot be synchronized, and a blank display name safely
        // falls back to the ID. Other malformed data, duplicate IDs, and length
        // violations remain protected by SQL snapshot validation.
        preserveInvalidRows: false);
    response = new SageOdbcWorkerResponse(true, Customers: customers);
}
catch (Exception ex)
{
    response = new SageOdbcWorkerResponse(
        false,
        RedactSecret(ex.Message, request?.Password));
}

await Console.Out.WriteAsync(JsonSerializer.Serialize(response, jsonOptions)).ConfigureAwait(false);
await Console.Out.FlushAsync().ConfigureAwait(false);
return response.Success ? 0 : 1;

static string RedactSecret(string message, string? secret)
{
    if (string.IsNullOrEmpty(secret))
    {
        return message;
    }

    return message.Replace(secret, "[redacted]", StringComparison.Ordinal);
}

internal sealed record SageOdbcWorkerRequest(
    string Operation,
    string Dsn,
    string Username,
    string Password);

internal sealed record SageOdbcWorkerResponse(
    bool Success,
    string? Error = null,
    IReadOnlyList<SageCustomer>? Customers = null);
