using System.Text.Json;
using TechBench.Models;

namespace TechBench.Data;

public sealed partial class SqlServerTechBenchRepository
{
    private static readonly JsonSerializerOptions FireDrillJsonOptions =
        new(JsonSerializerDefaults.Web);

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
                GetDateTime(row, "LastSyncedAtUtc", DateTime.MinValue),
                ParseFireDrillFields(GetString(row, "FieldsJson")))),
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
                ParseFireDrillFields(GetString(row, "FieldsJson")))),
            CancellationToken.None).GetAwaiter().GetResult();
    }

    private static IReadOnlyList<FireDrillCredentialField> ParseFireDrillFields(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        return JsonSerializer.Deserialize<List<FireDrillCredentialField>>(json, FireDrillJsonOptions)?
            .OrderBy(field => field.SortOrder)
            .ThenBy(field => field.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
    }
}
