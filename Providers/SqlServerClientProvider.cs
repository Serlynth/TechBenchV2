using System.Data;
using Microsoft.Data.SqlClient;
using TechBench.Data;
using TechBench.Models;

namespace TechBench.Providers;

public sealed class SqlServerClientProvider : IClientProvider
{
    public const string SearchClientsStoredProcedure = "[tb_app].[SearchClients]";

    private readonly SqlServerConnectionFactory _connectionFactory;

    public SqlServerClientProvider(SqlServerConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory
            ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    public string SourceName => "TechBench V2 SQL Server";

    public async Task<IReadOnlyList<Client>> SearchClientsAsync(
        string? searchTerm,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = SearchClientsStoredProcedure;
        command.CommandTimeout = _connectionFactory.Options.CommandTimeoutSeconds;

        var normalizedSearch = string.IsNullOrWhiteSpace(searchTerm)
            ? null
            : searchTerm.Trim();
        command.Parameters.Add(
            new SqlParameter("@Search", SqlDbType.NVarChar, 240)
            {
                Value = (object?)normalizedSearch ?? DBNull.Value
            });
        command.Parameters.Add(
            new SqlParameter("@IncludeInactive", SqlDbType.Bit)
            {
                Value = false
            });
        command.Parameters.Add(
            new SqlParameter("@Limit", SqlDbType.Int)
            {
                Value = 250
            });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var ordinals = ClientOrdinals.Create(reader);
        var clients = new List<Client>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            clients.Add(new Client
            {
                Id = reader.GetInt32(ordinals.Id),
                Name = ReadRequiredString(reader, ordinals.Name),
                Source = ReadOptionalString(reader, ordinals.Source) ?? "Manual",
                ExternalId = ReadOptionalString(reader, ordinals.ExternalId),
                IsActive = reader.GetBoolean(ordinals.IsActive),
                LastSyncedAt = ReadUtcAsLocalDateTime(reader, ordinals.LastSyncedAt),
                WhdLocationName = ReadOptionalString(reader, ordinals.WhdLocationName),
                WhdContactName = ReadOptionalString(reader, ordinals.WhdContactName),
                SageCustomerId = ReadOptionalString(reader, ordinals.SageCustomerId),
                SageCustomerName = ReadOptionalString(reader, ordinals.SageCustomerName),
                SageContactName = ReadOptionalString(reader, ordinals.SageContactName),
                SageTelephone = ReadOptionalString(reader, ordinals.SageTelephone),
                MatchStatus = ReadOptionalString(reader, ordinals.MatchStatus) ?? "Unmatched"
            });
        }

        return clients;
    }

    private static string ReadRequiredString(SqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            throw new InvalidOperationException(
                $"{SearchClientsStoredProcedure} returned a required null value.");
        }

        return reader.GetString(ordinal);
    }

    private static string? ReadOptionalString(SqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTime? ReadUtcAsLocalDateTime(SqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return reader.GetValue(ordinal) switch
        {
            DateTimeOffset value => value.LocalDateTime,
            DateTime value => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime(),
            var value => Convert.ToDateTime(value).ToLocalTime()
        };
    }

    private sealed record ClientOrdinals(
        int Id,
        int Name,
        int Source,
        int ExternalId,
        int IsActive,
        int LastSyncedAt,
        int WhdLocationName,
        int WhdContactName,
        int SageCustomerId,
        int SageCustomerName,
        int SageContactName,
        int SageTelephone,
        int MatchStatus)
    {
        public static ClientOrdinals Create(SqlDataReader reader)
        {
            try
            {
                return new ClientOrdinals(
                    reader.GetOrdinal("Id"),
                    reader.GetOrdinal("Name"),
                    reader.GetOrdinal("Source"),
                    reader.GetOrdinal("ExternalId"),
                    reader.GetOrdinal("IsActive"),
                    reader.GetOrdinal("LastSyncedAt"),
                    reader.GetOrdinal("WhdLocationName"),
                    reader.GetOrdinal("WhdContactName"),
                    reader.GetOrdinal("SageCustomerId"),
                    reader.GetOrdinal("SageCustomerName"),
                    reader.GetOrdinal("SageContactName"),
                    reader.GetOrdinal("SageTelephone"),
                    reader.GetOrdinal("MatchStatus"));
            }
            catch (IndexOutOfRangeException ex)
            {
                throw new InvalidOperationException(
                    $"{SearchClientsStoredProcedure} returned an incompatible result set.",
                    ex);
            }
        }
    }
}
