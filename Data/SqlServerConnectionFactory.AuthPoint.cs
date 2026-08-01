using System.Data;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using TechBench.Models;

namespace TechBench.Data;

public sealed partial class SqlServerConnectionFactory
{
    private readonly object _authPointSessionLock = new();
    private AuthPointLoginSession? _authPointLoginSession;

    internal AuthPointLoginSession? AuthPointLoginSession
    {
        get
        {
            lock (_authPointSessionLock)
            {
                return _authPointLoginSession;
            }
        }
    }

    public async Task<AuthPointLoginRequirement> GetAuthPointLoginRequirementAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = "[tb_app].[GetAuthPointLoginRequirement]";
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SingleRow,
                cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "SQL Server did not return the AuthPoint login policy.");
        }

        return new AuthPointLoginRequirement
        {
            IsRequired = reader.GetBoolean(reader.GetOrdinal("IsRequired")),
            ProviderLogin = reader.GetString(reader.GetOrdinal("ProviderLogin")),
            SessionHours = reader.GetInt32(reader.GetOrdinal("SessionHours"))
        };
    }

    public async Task<ClientSecretMfaChallenge> BeginAuthPointLoginAsync(
        Guid clientInstanceId,
        CancellationToken cancellationToken = default)
    {
        if (clientInstanceId == Guid.Empty)
        {
            throw new ArgumentException(
                "A client instance identifier is required.",
                nameof(clientInstanceId));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = "[tb_app].[BeginAuthPointLoginMfaChallenge]";
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.Parameters.Add(new SqlParameter(
            "@ClientInstanceId",
            SqlDbType.UniqueIdentifier)
        {
            Value = clientInstanceId
        });
        command.Parameters.Add(new SqlParameter(
            "@ClientMachine",
            SqlDbType.NVarChar,
            128)
        {
            Value = Environment.MachineName
        });
        command.Parameters.Add(new SqlParameter(
            "@RequestId",
            SqlDbType.UniqueIdentifier)
        {
            Value = Guid.NewGuid()
        });
        await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SingleRow,
                cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "SQL Server did not create the AuthPoint login request.");
        }

        return new ClientSecretMfaChallenge
        {
            ChallengeId = reader.IsDBNull(reader.GetOrdinal("ChallengeId"))
                ? Guid.Empty
                : reader.GetGuid(reader.GetOrdinal("ChallengeId")),
            ChallengeNonce = reader.IsDBNull(reader.GetOrdinal("ChallengeNonce"))
                ? []
                : reader.GetFieldValue<byte[]>(reader.GetOrdinal("ChallengeNonce")),
            Status = reader.GetString(reader.GetOrdinal("Status")),
            ExpiresAtUtc = reader.IsDBNull(reader.GetOrdinal("ExpiresAtUtc"))
                ? null
                : DateTime.SpecifyKind(
                    reader.GetDateTime(reader.GetOrdinal("ExpiresAtUtc")),
                    DateTimeKind.Utc),
            ProviderLogin = reader.GetString(reader.GetOrdinal("ProviderLogin"))
        };
    }

    public async Task<ClientSecretMfaStatus> GetAuthPointLoginStatusAsync(
        Guid challengeId,
        byte[] challengeNonce,
        CancellationToken cancellationToken = default)
    {
        if (challengeId == Guid.Empty || challengeNonce.Length != 32)
        {
            throw new ArgumentException("The AuthPoint challenge proof is invalid.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = "[tb_app].[GetClientSecretMfaChallenge]";
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.Parameters.Add(new SqlParameter(
            "@ChallengeId",
            SqlDbType.UniqueIdentifier)
        {
            Value = challengeId
        });
        command.Parameters.Add(new SqlParameter(
            "@ChallengeNonce",
            SqlDbType.VarBinary,
            32)
        {
            Value = challengeNonce
        });
        await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SingleRow,
                cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "SQL Server did not return the AuthPoint login status.");
        }

        return new ClientSecretMfaStatus
        {
            ChallengeId = reader.GetGuid(reader.GetOrdinal("ChallengeId")),
            Status = reader.GetString(reader.GetOrdinal("Status")),
            OutcomeCode = reader.IsDBNull(reader.GetOrdinal("OutcomeCode"))
                ? string.Empty
                : reader.GetString(reader.GetOrdinal("OutcomeCode")),
            OutcomeMessage = reader.IsDBNull(reader.GetOrdinal("OutcomeMessage"))
                ? string.Empty
                : reader.GetString(reader.GetOrdinal("OutcomeMessage")),
            ExpiresAtUtc = reader.IsDBNull(reader.GetOrdinal("ExpiresAtUtc"))
                ? null
                : DateTime.SpecifyKind(
                    reader.GetDateTime(reader.GetOrdinal("ExpiresAtUtc")),
                    DateTimeKind.Utc),
            AuthorizationToken = reader.IsDBNull(reader.GetOrdinal("AuthorizationToken"))
                ? null
                : reader.GetFieldValue<byte[]>(reader.GetOrdinal("AuthorizationToken"))
        };
    }

    public async Task<AuthPointLoginSession> ActivateAuthPointLoginSessionAsync(
        Guid challengeId,
        byte[] challengeNonce,
        byte[] authorizationToken,
        Guid clientInstanceId,
        CancellationToken cancellationToken = default)
    {
        if (challengeId == Guid.Empty
            || challengeNonce.Length != 32
            || authorizationToken.Length != 32
            || clientInstanceId == Guid.Empty)
        {
            throw new ArgumentException("The AuthPoint login proof is invalid.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = "[tb_app].[ActivateAuthPointLoginSession]";
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.Parameters.Add(new SqlParameter("@ChallengeId", SqlDbType.UniqueIdentifier)
        {
            Value = challengeId
        });
        command.Parameters.Add(new SqlParameter("@ChallengeNonce", SqlDbType.VarBinary, 32)
        {
            Value = challengeNonce
        });
        command.Parameters.Add(new SqlParameter("@AuthorizationToken", SqlDbType.VarBinary, 32)
        {
            Value = authorizationToken
        });
        command.Parameters.Add(new SqlParameter("@ClientInstanceId", SqlDbType.UniqueIdentifier)
        {
            Value = clientInstanceId
        });
        command.Parameters.Add(new SqlParameter("@RequestId", SqlDbType.UniqueIdentifier)
        {
            Value = Guid.NewGuid()
        });
        await using var reader = await command.ExecuteReaderAsync(
                CommandBehavior.SingleRow,
                cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "SQL Server did not activate the AuthPoint login session.");
        }

        var session = new AuthPointLoginSession
        {
            SessionId = reader.GetGuid(reader.GetOrdinal("SessionId")),
            ClientInstanceId = clientInstanceId,
            SessionToken = authorizationToken.ToArray(),
            ExpiresAtUtc = DateTime.SpecifyKind(
                reader.GetDateTime(reader.GetOrdinal("ExpiresAtUtc")),
                DateTimeKind.Utc)
        };
        lock (_authPointSessionLock)
        {
            ClearAuthPointSessionUnsafe();
            _authPointLoginSession = session;
        }

        return session;
    }

    public async Task CancelAuthPointLoginAsync(
        Guid challengeId,
        byte[] challengeNonce,
        CancellationToken cancellationToken = default)
    {
        if (challengeId == Guid.Empty || challengeNonce.Length != 32)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = "[tb_app].[CancelClientSecretMfaChallenge]";
        command.CommandTimeout = Options.CommandTimeoutSeconds;
        command.Parameters.Add(new SqlParameter("@ChallengeId", SqlDbType.UniqueIdentifier)
        {
            Value = challengeId
        });
        command.Parameters.Add(new SqlParameter("@ChallengeNonce", SqlDbType.VarBinary, 32)
        {
            Value = challengeNonce
        });
        command.Parameters.Add(new SqlParameter("@RequestId", SqlDbType.UniqueIdentifier)
        {
            Value = Guid.NewGuid()
        });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task EndAuthPointLoginSessionAsync(
        CancellationToken cancellationToken = default)
    {
        AuthPointLoginSession? session;
        lock (_authPointSessionLock)
        {
            session = _authPointLoginSession;
        }

        if (session is null)
        {
            return;
        }

        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandType = CommandType.StoredProcedure;
            command.CommandText = "[tb_app].[EndAuthPointLoginSession]";
            command.CommandTimeout = Options.CommandTimeoutSeconds;
            command.Parameters.Add(new SqlParameter("@SessionId", SqlDbType.UniqueIdentifier)
            {
                Value = session.SessionId
            });
            command.Parameters.Add(new SqlParameter("@SessionToken", SqlDbType.VarBinary, 32)
            {
                Value = session.SessionToken
            });
            command.Parameters.Add(new SqlParameter("@RequestId", SqlDbType.UniqueIdentifier)
            {
                Value = Guid.NewGuid()
            });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_authPointSessionLock)
            {
                ClearAuthPointSessionUnsafe();
            }
        }
    }

    internal void ClearAuthPointLoginSession()
    {
        lock (_authPointSessionLock)
        {
            ClearAuthPointSessionUnsafe();
        }
    }

    private void ClearAuthPointSessionUnsafe()
    {
        if (_authPointLoginSession?.SessionToken is { Length: > 0 } token)
        {
            CryptographicOperations.ZeroMemory(token);
        }

        _authPointLoginSession = null;
    }
}
