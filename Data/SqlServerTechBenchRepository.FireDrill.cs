using TechBench.Models;

namespace TechBench.Data;

public sealed partial class SqlServerTechBenchRepository
{
    public IReadOnlyList<FireDrillCredentialSummary> SearchFireDrillCredentials(string? searchTerm = null) =>
        QueryAsync(
            Procedures.SearchFireDrillCredentials,
            command =>
            {
                AddText(command, "@Search", 240, searchTerm);
                AddInt(command, "@Limit", 500);
            },
            (reader, token) => ReadListAsync(reader, token, row => new FireDrillCredentialSummary(
                GetInt64(row, "CredentialId"),
                GetString(row, "ClientName"),
                GetString(row, "FireboxIp"),
                GetString(row, "Status"),
                GetDateTime(row, "LastSyncedAtUtc", DateTime.MinValue))),
            CancellationToken.None).GetAwaiter().GetResult();

    public FireDrillCredential? RevealFireDrillCredential(long credentialId)
    {
        if (credentialId <= 0) return null;
        return QueryAsync(
            Procedures.RevealFireDrillCredential,
            command => AddBigInt(command, "@CredentialId", credentialId),
            (reader, token) => ReadSingleAsync(reader, token, row => new FireDrillCredential(
                GetInt64(row, "CredentialId"),
                GetString(row, "ClientName"),
                GetString(row, "FireboxIp"),
                GetString(row, "Status"),
                GetDateTime(row, "LastSyncedAtUtc", DateTime.MinValue),
                GetString(row, "Admin"),
                GetString(row, "CsriAdmin"),
                GetString(row, "FireboxDbCsri"),
                GetString(row, "AuthpointUser"),
                GetString(row, "SslVpnPassword"),
                GetString(row, "AdAuthUser"),
                GetString(row, "AdPassword"),
                GetString(row, "RustPassword"))),
            CancellationToken.None).GetAwaiter().GetResult();
    }

    public void AuditFireDrillCredentialCopy(long credentialId, string fieldName) =>
        ExecuteNonQueryAsync(
            Procedures.AuditFireDrillCredentialCopy,
            command =>
            {
                AddBigInt(command, "@CredentialId", credentialId);
                AddRequiredText(command, "@FieldName", 40, fieldName);
            },
            CancellationToken.None).GetAwaiter().GetResult();
}
