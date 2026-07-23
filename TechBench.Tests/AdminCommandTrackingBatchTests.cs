using TechBench.Models;
using TechBench.ViewModels;

namespace TechBench.Tests;

public sealed class AdminCommandTrackingBatchTests
{
    [Fact]
    public void ApplyResponsesTracksEachRecipientIndependently()
    {
        var firstSession = CreateSession("Ryan", "CSRI\\rskoog", "DESKTOP-1");
        var secondSession = CreateSession("Roger", "CSRI\\rwaters", "DESKTOP-2");
        var batch = new AdminCommandTrackingBatch(
            ClientSessionCommandTypes.UpdateNotice,
            "Please acknowledge.",
            [
                (firstSession, CreateCommand(1001, firstSession.SessionId)),
                (secondSession, CreateCommand(1002, secondSession.SessionId))
            ]);

        batch.ApplyResponses(
        [
            new ClientSessionCommandResponse
            {
                CommandId = 1002,
                SessionId = secondSession.SessionId,
                AcknowledgementResult = "Acknowledged",
                ResponseMessage = "I saw it.",
                AcknowledgedAt = DateTime.Now
            }
        ]);

        Assert.Equal("1 of 2 responded", batch.ProgressLabel);
        Assert.False(batch.AllResponded);
        Assert.False(batch.Recipients[0].HasResponded);
        Assert.True(batch.Recipients[1].HasResponded);
        Assert.Equal("I saw it.", batch.Recipients[1].ResponseMessage);
    }

    [Fact]
    public void ApplyResponsesMarksBatchCompleteWhenEveryoneResponds()
    {
        var session = CreateSession("Ryan", "CSRI\\rskoog", "DESKTOP-1");
        var batch = new AdminCommandTrackingBatch(
            ClientSessionCommandTypes.SignOut,
            "Update required.",
            [(session, CreateCommand(2001, session.SessionId))]);

        batch.ApplyResponses(
        [
            new ClientSessionCommandResponse
            {
                CommandId = 2001,
                SessionId = session.SessionId,
                AcknowledgementResult = "SignedOut",
                ResponseMessage = "Recovery draft saved.",
                AcknowledgedAt = DateTime.Now
            }
        ]);

        Assert.True(batch.AllResponded);
        Assert.Equal("1 of 1 responded", batch.ProgressLabel);
        Assert.Equal("SignedOut", batch.Recipients[0].Status);
    }

    private static ClientSessionInfo CreateSession(
        string displayName,
        string loginName,
        string machineName) =>
        new()
        {
            SessionId = Guid.NewGuid(),
            DisplayName = displayName,
            LoginName = loginName,
            MachineName = machineName
        };

    private static ClientSessionCommand CreateCommand(long commandId, Guid sessionId) =>
        new()
        {
            CommandId = commandId,
            SessionId = sessionId,
            CommandType = ClientSessionCommandTypes.UpdateNotice,
            RequestedAt = DateTime.Now
        };
}
