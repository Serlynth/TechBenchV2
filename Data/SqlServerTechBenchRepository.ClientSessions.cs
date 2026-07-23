using Microsoft.Data.SqlClient;
using TechBench.Models;

namespace TechBench.Data;

public sealed partial class SqlServerTechBenchRepository
{
    public ClientSessionHeartbeatResult HeartbeatClientSession(
        Guid sessionId,
        string machineName,
        string clientVersion,
        string currentSection,
        bool hasUnsavedChanges,
        bool isBusy) =>
        HeartbeatClientSessionAsync(
                sessionId,
                machineName,
                clientVersion,
                currentSection,
                hasUnsavedChanges,
                isBusy)
            .GetAwaiter()
            .GetResult();

    public Task<ClientSessionHeartbeatResult> HeartbeatClientSessionAsync(
        Guid sessionId,
        string machineName,
        string clientVersion,
        string currentSection,
        bool hasUnsavedChanges,
        bool isBusy,
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            Procedures.HeartbeatClientSession,
            command =>
            {
                AddGuid(command, "@SessionId", sessionId);
                AddGuid(command, "@DeviceId", DeviceId);
                AddRequiredText(command, "@MachineName", 128, machineName);
                AddRequiredText(command, "@ClientVersion", 40, clientVersion);
                AddText(command, "@CurrentSection", 80, currentSection);
                AddBit(command, "@HasUnsavedChanges", hasUnsavedChanges);
                AddBit(command, "@IsBusy", isBusy);
            },
            async (reader, token) =>
            {
                var serverTime = DateTime.Now;
                ClientSessionCommand? pendingCommand = null;
                if (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    serverTime = GetDateTime(reader, "ServerUtc", DateTime.Now);
                    var commandId = GetInt64(reader, "CommandId");
                    if (commandId > 0)
                    {
                        pendingCommand = ReadClientSessionCommand(reader);
                    }
                }

                return new ClientSessionHeartbeatResult
                {
                    ServerTime = serverTime,
                    PendingCommand = pendingCommand
                };
            },
            cancellationToken);

    public IReadOnlyList<ClientSessionInfo> GetActiveClientSessions(
        Guid currentSessionId) =>
        GetActiveClientSessionsAsync(currentSessionId).GetAwaiter().GetResult();

    public Task<IReadOnlyList<ClientSessionInfo>> GetActiveClientSessionsAsync(
        Guid currentSessionId,
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            Procedures.GetActiveClientSessions,
            command => AddGuid(command, "@CurrentSessionId", currentSessionId),
            (reader, token) => ReadListAsync(reader, token, ReadClientSessionInfo),
            cancellationToken);

    public ClientSessionCommand QueueClientSessionCommand(
        Guid requesterSessionId,
        Guid targetSessionId,
        string commandType,
        string message) =>
        QueueClientSessionCommandAsync(
                requesterSessionId,
                targetSessionId,
                commandType,
                message)
            .GetAwaiter()
            .GetResult();

    public Task<ClientSessionCommand> QueueClientSessionCommandAsync(
        Guid requesterSessionId,
        Guid targetSessionId,
        string commandType,
        string message,
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            Procedures.QueueClientSessionCommand,
            command =>
            {
                AddGuid(command, "@RequesterSessionId", requesterSessionId);
                AddGuid(command, "@TargetSessionId", targetSessionId);
                AddRequiredText(command, "@CommandType", 30, commandType);
                AddRequiredText(command, "@Message", 500, message);
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            async (reader, token) =>
            {
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        "SQL Server did not return the queued client command.");
                }

                return ReadClientSessionCommand(reader);
            },
            cancellationToken);

    public void AcknowledgeClientSessionCommand(
        Guid sessionId,
        long commandId,
        string result) =>
        AcknowledgeClientSessionCommandAsync(sessionId, commandId, result)
            .GetAwaiter()
            .GetResult();

    public async Task AcknowledgeClientSessionCommandAsync(
        Guid sessionId,
        long commandId,
        string result,
        CancellationToken cancellationToken = default)
    {
        _ = await ExecuteNonQueryAsync(
                Procedures.AcknowledgeClientSessionCommand,
                command =>
                {
                    AddGuid(command, "@SessionId", sessionId);
                    AddBigInt(command, "@CommandId", commandId);
                    AddRequiredText(command, "@Result", 40, result);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public void CloseClientSession(Guid sessionId) =>
        CloseClientSessionAsync(sessionId).GetAwaiter().GetResult();

    public async Task CloseClientSessionAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        _ = await ExecuteNonQueryAsync(
                Procedures.CloseClientSession,
                command => AddGuid(command, "@SessionId", sessionId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static ClientSessionInfo ReadClientSessionInfo(SqlDataReader reader) => new()
    {
        SessionId = GetNullableGuid(reader, "SessionId") ?? Guid.Empty,
        LoginName = GetString(reader, "LoginName"),
        DisplayName = GetString(reader, "DisplayName"),
        IsAdmin = GetBoolean(reader, "IsAdmin"),
        MachineName = GetString(reader, "MachineName"),
        ClientVersion = GetString(reader, "ClientVersion"),
        CurrentSection = GetString(reader, "CurrentSection"),
        HasUnsavedChanges = GetBoolean(reader, "HasUnsavedChanges"),
        IsBusy = GetBoolean(reader, "IsBusy"),
        StartedAt = GetDateTime(reader, "StartedAtUtc"),
        LastSeenAt = GetDateTime(reader, "LastSeenAtUtc"),
        IsCurrentSession = GetBoolean(reader, "IsCurrentSession")
    };

    private static ClientSessionCommand ReadClientSessionCommand(SqlDataReader reader) => new()
    {
        CommandId = GetInt64(reader, "CommandId"),
        SessionId = GetNullableGuid(reader, "SessionId") ?? Guid.Empty,
        CommandType = GetString(reader, "CommandType"),
        Message = GetString(reader, "Message"),
        RequestedBy = GetString(reader, "RequestedBy"),
        RequestedAt = GetDateTime(reader, "RequestedAtUtc")
    };
}
