using System.Text.Json;
using TechBench.Models;

namespace TechBench.Data;

public sealed partial class SqlServerTechBenchRepository
{
    public IReadOnlyList<ClientUserSummary> SearchClientUsers(
        int? clientId = null,
        string? searchTerm = null) =>
        QueryAsync(
            Procedures.SearchClientUsers,
            command =>
            {
                AddInt(command, "@ClientId", clientId);
                AddText(command, "@Search", 240, searchTerm);
                AddInt(command, "@Limit", 500);
            },
            (reader, token) => ReadListAsync(
                reader,
                token,
                ReadClientUserSummary),
            CancellationToken.None).GetAwaiter().GetResult();

    public ClientUserSummary? RevealClientUser(long clientUserId)
    {
        if (clientUserId <= 0) return null;
        return QueryAsync(
            Procedures.RevealClientUser,
            command => AddBigInt(command, "@ClientUserId", clientUserId),
            (reader, token) => ReadSingleAsync(
                reader,
                token,
                ReadClientUserSummary),
            CancellationToken.None).GetAwaiter().GetResult();
    }

    private static ClientUserSummary ReadClientUserSummary(
        Microsoft.Data.SqlClient.SqlDataReader row) =>
        new(
            GetInt64(row, "ClientUserId"),
            GetInt32(row, "ClientId"),
            GetString(row, "ClientName"),
            GetString(row, "DisplayName"),
            GetString(row, "RoleDepartment"),
            GetString(row, "Email"),
            GetString(row, "Phone"),
            GetString(row, "LocationName"),
            GetDateTime(row, "LastSyncedAtUtc", DateTime.MinValue),
            GetInt32(row, "AccountCount"),
            ParseClientUserAccounts(GetString(row, "AccountsJson")));

    private static IReadOnlyList<ClientUserAccountGroup> ParseClientUserAccounts(
        string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        return JsonSerializer.Deserialize<List<ClientUserAccountGroup>>(
                   json,
                   FireDrillJsonOptions)?
               .OrderBy(group => group.SortOrder)
               .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
               .Select(group => group with
               {
                   Fields = group.Fields
                       .OrderBy(field => field.SortOrder)
                       .ThenBy(field => field.Label, StringComparer.OrdinalIgnoreCase)
                       .ToArray()
               })
               .ToArray()
            ?? [];
    }
}
