using System.Data;
using Microsoft.Data.SqlClient;
using TechBench.Models;

namespace TechBench.Data;

public sealed class SqlServerConnectionFactory
{
    public const string CurrentUserContextStoredProcedure =
        "[tb_app].[GetCurrentUserContext]";
    public const string BeginUserPreviewStoredProcedure =
        "[tb_app].[AdminBeginUserPreview]";
    public const string ActivateReadOnlyPreviewStoredProcedure =
        "[tb_app].[ActivateReadOnlyPreview]";
    public const string EndUserPreviewStoredProcedure =
        "[tb_app].[AdminEndUserPreview]";
    public const string ListPreviewUsersStoredProcedure =
        "[tb_app].[AdminListPreviewUsers]";
    public const string PreviewReaderExecutionStatement =
        "EXECUTE AS USER = N'tb_preview_reader';";
    public const int MinimumSupportedSchemaVersion = 13;
    public const int SupportedSchemaVersion = 14;

    private const string PreviewApplicationName =
        "TechBench V2 Read-only User Preview";

    private readonly UserPreviewSession? _previewSession;
    private readonly CurrentUserContext? _authenticatedUser;

    public SqlServerConnectionFactory(SqlServerConnectionOptions options)
        : this(options, null, null)
    {
    }

    private SqlServerConnectionFactory(
        SqlServerConnectionOptions options,
        UserPreviewSession? previewSession,
        CurrentUserContext? authenticatedUser)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options.NormalizeAndValidate();
        _previewSession = previewSession;
        _authenticatedUser = authenticatedUser;

        if ((_previewSession is null) != (_authenticatedUser is null))
        {
            throw new ArgumentException(
                "A read-only preview requires both its server session and authenticated Admin context.");
        }
    }

    public SqlServerConnectionOptions Options { get; }

    public bool IsReadOnlyPreview => _previewSession is not null;

    public Guid? PreviewSessionId => _previewSession?.PreviewSessionId;

    public static string NormalizePreviewLoginName(
        string? targetLoginName,
        string? authenticatedLoginName,
        string? fallbackDomainName = null)
    {
        var normalizedLoginName = targetLoginName?.Trim() ?? string.Empty;
        if (normalizedLoginName.Length < 1)
        {
            throw new ArgumentException(
                "Enter a username, such as username or CSRI\\username.",
                nameof(targetLoginName));
        }

        if (!normalizedLoginName.Contains('\\'))
        {
            var authenticatedSeparator = authenticatedLoginName?.IndexOf('\\') ?? -1;
            var domainName = authenticatedSeparator > 0
                ? authenticatedLoginName![..authenticatedSeparator].Trim()
                : (string.IsNullOrWhiteSpace(fallbackDomainName)
                    ? Environment.UserDomainName
                    : fallbackDomainName.Trim());
            if (string.IsNullOrWhiteSpace(domainName))
            {
                throw new ArgumentException(
                    "Enter the full domain username because the Windows domain could not be determined.",
                    nameof(targetLoginName));
            }

            normalizedLoginName = $"{domainName}\\{normalizedLoginName}";
        }

        if (normalizedLoginName.Length > 256)
        {
            throw new ArgumentException(
                "The domain username cannot exceed 256 characters.",
                nameof(targetLoginName));
        }

        return normalizedLoginName;
    }

    public SqlConnection CreateConnection()
    {
        if (IsReadOnlyPreview)
        {
            throw new InvalidOperationException(
                "Preview connections must be opened asynchronously so the server can apply the read-only security context.");
        }

        return CreateUnconfiguredConnection();
    }

    public SqlServerConnectionFactory CreateReadOnlyPreviewFactory(
        UserPreviewSession previewSession,
        CurrentUserContext authenticatedUser)
    {
        ArgumentNullException.ThrowIfNull(previewSession);
        ArgumentNullException.ThrowIfNull(authenticatedUser);
        if (IsReadOnlyPreview)
        {
            throw new InvalidOperationException(
                "A read-only preview cannot begin another preview session.");
        }

        if (!authenticatedUser.IsAdmin || authenticatedUser.IsReadOnlyPreview)
        {
            throw new UnauthorizedAccessException(
                "Only an authenticated TechBench Admin may begin a read-only user preview.");
        }

        return new SqlServerConnectionFactory(Options, previewSession, authenticatedUser);
    }

    public async Task<SqlConnection> OpenConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = CreateUnconfiguredConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (_previewSession is not null)
            {
                await ActivateReadOnlyPreviewAsync(connection, cancellationToken)
                    .ConfigureAwait(false);
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<UserPreviewSession> BeginUserPreviewAsync(
        string targetLoginName,
        Guid? clientInstanceId = null,
        CancellationToken cancellationToken = default)
    {
        if (IsReadOnlyPreview)
        {
            throw new InvalidOperationException(
                "A read-only preview cannot begin another preview session.");
        }

        var normalizedLoginName = targetLoginName?.Trim() ?? string.Empty;
        if (normalizedLoginName.Length is < 1 or > 256)
        {
            throw new ArgumentException(
                "Enter the target user's domain login, such as CSRI\\username.",
                nameof(targetLoginName));
        }

        var effectiveClientInstanceId = clientInstanceId ?? Guid.NewGuid();
        await using var connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = BeginUserPreviewStoredProcedure;
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.Parameters.Add(new SqlParameter(
            "@TargetLoginName",
            SqlDbType.NVarChar,
            256)
        {
            Value = normalizedLoginName
        });
        command.Parameters.Add(new SqlParameter(
            "@ClientInstanceId",
            SqlDbType.UniqueIdentifier)
        {
            Value = effectiveClientInstanceId
        });

        await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SingleRow,
                cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException(
                "SQL Server did not create a read-only user preview session.");
        }

        var sessionId = reader.GetGuid(reader.GetOrdinal("PreviewSessionId"));
        if (sessionId == Guid.Empty)
        {
            throw new InvalidOperationException(
                $"{BeginUserPreviewStoredProcedure} returned an empty preview session ID.");
        }

        return new UserPreviewSession(
            sessionId,
            effectiveClientInstanceId,
            reader.GetFieldValue<byte[]>(reader.GetOrdinal("UserSid")),
            ReadRequiredString(reader, "LoginName", BeginUserPreviewStoredProcedure),
            ReadRequiredString(reader, "DisplayName", BeginUserPreviewStoredProcedure),
            reader.GetBoolean(reader.GetOrdinal("IsTechnician")),
            reader.GetBoolean(reader.GetOrdinal("IsManager")),
            reader.GetBoolean(reader.GetOrdinal("IsAdmin")),
            reader.GetBoolean(reader.GetOrdinal("IsSyncOperator")),
            DateTime.SpecifyKind(
                reader.GetDateTime(reader.GetOrdinal("ExpiresAtUtc")),
                DateTimeKind.Utc));
    }

    public async Task EndUserPreviewAsync(
        CancellationToken cancellationToken = default)
    {
        if (_previewSession is null)
        {
            return;
        }

        // Use a fresh, unconfigured Windows-authenticated connection. Calling
        // through OpenConnectionAsync would first enter the restricted preview
        // execution context, which intentionally cannot end server sessions.
        await using var connection = CreateUnconfiguredConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = EndUserPreviewStoredProcedure;
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.Parameters.Add(new SqlParameter(
            "@PreviewSessionId",
            SqlDbType.UniqueIdentifier)
        {
            Value = _previewSession.PreviewSessionId
        });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
            if (schemaVersion < MinimumSupportedSchemaVersion
                || schemaVersion > SupportedSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"The TechBench database schema is version {schemaVersion}, "
                    + $"but this client supports versions "
                    + $"{MinimumSupportedSchemaVersion} through {SupportedSchemaVersion}. "
                    + "Contact the TechBench administrator.");
            }

            var userSid = reader.GetFieldValue<byte[]>(reader.GetOrdinal("UserSid"));
            var loginName = ReadRequiredString(reader, "LoginName");
            var displayName = ReadRequiredString(reader, "DisplayName");
            var authenticatedUserSid = ReadOptionalBytes(reader, "AuthenticatedUserSid")
                ?? _authenticatedUser?.CredentialOwnerSid
                ?? userSid;
            var authenticatedLoginName = ReadOptionalString(reader, "AuthenticatedLoginName")
                ?? _authenticatedUser?.AuthenticatedLoginName
                ?? _authenticatedUser?.LoginName
                ?? loginName;
            var authenticatedDisplayName = ReadOptionalString(reader, "AuthenticatedDisplayName")
                ?? _authenticatedUser?.AuthenticatedDisplayName
                ?? _authenticatedUser?.DisplayName
                ?? displayName;
            var isReadOnlyPreview = ReadOptionalBoolean(reader, "IsReadOnlyPreview")
                ?? IsReadOnlyPreview;
            var previewSessionId = ReadOptionalGuid(reader, "PreviewSessionId")
                ?? _previewSession?.PreviewSessionId;
            var previewExpiresAtUtc = ReadOptionalDateTimeUtc(reader, "PreviewExpiresAtUtc")
                ?? _previewSession?.ExpiresAtUtc;

            if (IsReadOnlyPreview != isReadOnlyPreview)
            {
                throw new UnauthorizedAccessException(
                    "SQL Server returned an unexpected user-preview security context.");
            }

            if (_previewSession is not null
                && (!userSid.AsSpan().SequenceEqual(_previewSession.UserSid)
                    || !loginName.Equals(
                        _previewSession.LoginName,
                        StringComparison.OrdinalIgnoreCase)
                    || previewSessionId != _previewSession.PreviewSessionId))
            {
                throw new UnauthorizedAccessException(
                    "SQL Server returned a different user than the requested read-only preview.");
            }

            var currentUser = new CurrentUserContext(
                userSid,
                loginName,
                displayName,
                reader.GetGuid(reader.GetOrdinal("DatabaseInstanceId")),
                schemaVersion,
                DateTime.SpecifyKind(
                    reader.GetDateTime(reader.GetOrdinal("ServerUtc")),
                    DateTimeKind.Utc),
                reader.GetBoolean(reader.GetOrdinal("IsTechnician")),
                reader.GetBoolean(reader.GetOrdinal("IsManager")),
                reader.GetBoolean(reader.GetOrdinal("IsAdmin")),
                reader.GetBoolean(reader.GetOrdinal("IsSyncOperator")),
                authenticatedUserSid,
                authenticatedLoginName,
                authenticatedDisplayName,
                isReadOnlyPreview,
                previewSessionId,
                previewExpiresAtUtc);
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

    internal string BuildConnectionString() =>
        Options.BuildConnectionString(
            pooling: !IsReadOnlyPreview,
            applicationName: IsReadOnlyPreview
                ? PreviewApplicationName
                : SqlServerConnectionOptions.DefaultApplicationName);

    private SqlConnection CreateUnconfiguredConnection() =>
        new(BuildConnectionString());

    private async Task ActivateReadOnlyPreviewAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        if (_previewSession is null)
        {
            return;
        }

        using (var activateCommand = connection.CreateCommand())
        {
            activateCommand.CommandType = CommandType.StoredProcedure;
            activateCommand.CommandText = ActivateReadOnlyPreviewStoredProcedure;
            activateCommand.CommandTimeout = Options.CommandTimeoutSeconds;
            activateCommand.Parameters.Add(new SqlParameter(
                "@PreviewSessionId",
                SqlDbType.UniqueIdentifier)
            {
                Value = _previewSession.PreviewSessionId
            });
            await activateCommand.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        // This must remain a separate, constant statement after activation.
        // It removes the authenticated Admin's normal database permissions for
        // the lifetime of this non-pooled connection.
        using var restrictCommand = connection.CreateCommand();
        restrictCommand.CommandType = CommandType.Text;
        restrictCommand.CommandText = PreviewReaderExecutionStatement;
        restrictCommand.CommandTimeout = Options.CommandTimeoutSeconds;
        await restrictCommand.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private static string ReadRequiredString(
        SqlDataReader reader,
        string columnName,
        string procedure = CurrentUserContextStoredProcedure)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            throw new InvalidOperationException(
                $"{procedure} returned a null {columnName}.");
        }

        return reader.GetString(ordinal);
    }

    private static int FindOrdinal(SqlDataReader reader, string columnName)
    {
        for (var index = 0; index < reader.FieldCount; index++)
        {
            if (reader.GetName(index).Equals(
                    columnName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static byte[]? ReadOptionalBytes(SqlDataReader reader, string columnName)
    {
        var ordinal = FindOrdinal(reader, columnName);
        return ordinal < 0 || reader.IsDBNull(ordinal)
            ? null
            : reader.GetFieldValue<byte[]>(ordinal);
    }

    private static string? ReadOptionalString(SqlDataReader reader, string columnName)
    {
        var ordinal = FindOrdinal(reader, columnName);
        return ordinal < 0 || reader.IsDBNull(ordinal)
            ? null
            : reader.GetString(ordinal);
    }

    private static bool? ReadOptionalBoolean(SqlDataReader reader, string columnName)
    {
        var ordinal = FindOrdinal(reader, columnName);
        return ordinal < 0 || reader.IsDBNull(ordinal)
            ? null
            : reader.GetBoolean(ordinal);
    }

    private static Guid? ReadOptionalGuid(SqlDataReader reader, string columnName)
    {
        var ordinal = FindOrdinal(reader, columnName);
        return ordinal < 0 || reader.IsDBNull(ordinal)
            ? null
            : reader.GetGuid(ordinal);
    }

    private static DateTime? ReadOptionalDateTimeUtc(
        SqlDataReader reader,
        string columnName)
    {
        var ordinal = FindOrdinal(reader, columnName);
        return ordinal < 0 || reader.IsDBNull(ordinal)
            ? null
            : DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc);
    }
}
