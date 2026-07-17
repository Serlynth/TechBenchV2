using System.Data;
using Microsoft.Data.SqlClient;
using TechBench.Models;

namespace TechBench.Data;

public sealed class SqlServerConnectionFactory
{
    public const string CurrentUserContextStoredProcedure =
        "[tb_app].[GetCurrentUserContext]";
    public const int SupportedSchemaVersion = 5;

    public SqlServerConnectionFactory(SqlServerConnectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options.NormalizeAndValidate();
    }

    public SqlServerConnectionOptions Options { get; }

    public SqlConnection CreateConnection() =>
        new(Options.BuildConnectionString());

    public async Task<SqlConnection> OpenConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<CurrentUserContext> GetCurrentUserContextAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = CurrentUserContextStoredProcedure;
        command.CommandTimeout = Options.CommandTimeoutSeconds;

        await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SingleRow,
                cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException(
                "The current Windows account is not registered for TechBench.");
        }

        try
        {
            var schemaVersion = reader.GetInt32(reader.GetOrdinal("SchemaVersion"));
            if (schemaVersion != SupportedSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"The TechBench database schema is version {schemaVersion}, "
                    + $"but this client requires version {SupportedSchemaVersion}. "
                    + "Contact the TechBench administrator.");
            }

            var currentUser = new CurrentUserContext(
                reader.GetFieldValue<byte[]>(reader.GetOrdinal("UserSid")),
                ReadRequiredString(reader, "LoginName"),
                ReadRequiredString(reader, "DisplayName"),
                reader.GetGuid(reader.GetOrdinal("DatabaseInstanceId")),
                schemaVersion,
                DateTime.SpecifyKind(
                    reader.GetDateTime(reader.GetOrdinal("ServerUtc")),
                    DateTimeKind.Utc),
                reader.GetBoolean(reader.GetOrdinal("IsTechnician")),
                reader.GetBoolean(reader.GetOrdinal("IsManager")),
                reader.GetBoolean(reader.GetOrdinal("IsAdmin")),
                reader.GetBoolean(reader.GetOrdinal("IsSyncOperator")));
            if (!currentUser.IsTechnician
                && !currentUser.IsManager
                && !currentUser.IsAdmin
                && !currentUser.IsSyncOperator)
            {
                throw new UnauthorizedAccessException(
                    "The current Windows account is not assigned a TechBench database role.");
            }

            return currentUser;
        }
        catch (IndexOutOfRangeException ex)
        {
            throw new InvalidOperationException(
                $"{CurrentUserContextStoredProcedure} returned an incompatible result set.",
                ex);
        }
    }

    private static string ReadRequiredString(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            throw new InvalidOperationException(
                $"{CurrentUserContextStoredProcedure} returned a null {columnName}.");
        }

        return reader.GetString(ordinal);
    }
}
