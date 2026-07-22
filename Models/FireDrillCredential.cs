namespace TechBench.Models;

public sealed record FireDrillCredentialSummary(
    long CredentialId,
    string ClientName,
    string FireboxIp,
    string Status,
    DateTime LastSyncedAtUtc)
{
    public string LastSyncedLabel => LastSyncedAtUtc == DateTime.MinValue
        ? "Never"
        : LastSyncedAtUtc.ToLocalTime().ToString("g");
}

public sealed record FireDrillCredential(
    long CredentialId,
    string ClientName,
    string FireboxIp,
    string Status,
    DateTime LastSyncedAtUtc,
    string Admin,
    string CsriAdmin,
    string FireboxDbCsri,
    string AuthpointUser,
    string SslVpnPassword,
    string AdAuthUser,
    string AdPassword,
    string RustPassword);

public sealed record FireDrillCredentialField(string Label, string FieldName, string Value);
