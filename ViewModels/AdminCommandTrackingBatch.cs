using System.Collections.ObjectModel;
using TechBench.Models;

namespace TechBench.ViewModels;

public sealed class AdminCommandTrackingBatch : EventArgs
{
    public AdminCommandTrackingBatch(
        string commandType,
        string message,
        IEnumerable<(ClientSessionInfo Session, ClientSessionCommand Command)> queuedCommands)
    {
        BatchId = Guid.NewGuid();
        CommandType = commandType;
        Message = message;
        RequestedAt = DateTime.Now;
        Recipients = new ObservableCollection<AdminCommandRecipientStatus>(
            queuedCommands.Select(static queued => new AdminCommandRecipientStatus(
                queued.Session,
                queued.Command)));
    }

    public Guid BatchId { get; }

    public string CommandType { get; }

    public string Message { get; }

    public DateTime RequestedAt { get; }

    public ObservableCollection<AdminCommandRecipientStatus> Recipients { get; }

    public string Title => CommandType.Equals(
        ClientSessionCommandTypes.SignOut,
        StringComparison.Ordinal)
        ? "TechBench sign-out responses"
        : "TechBench alert responses";

    public string RequestedAtLabel => RequestedAt.ToString("g");

    public string ProgressLabel
    {
        get
        {
            var completed = Recipients.Count(static recipient => recipient.HasResponded);
            return $"{completed} of {Recipients.Count} responded";
        }
    }

    public bool AllResponded =>
        Recipients.Count > 0 && Recipients.All(static recipient => recipient.HasResponded);

    public event EventHandler? TrackingUpdated;

    public void ApplyResponses(IEnumerable<ClientSessionCommandResponse> responses)
    {
        var responsesByCommandId = responses.ToDictionary(
            static response => response.CommandId);
        var changed = false;
        foreach (var recipient in Recipients)
        {
            if (responsesByCommandId.TryGetValue(recipient.CommandId, out var response))
            {
                changed |= recipient.ApplyResponse(response);
            }
        }

        if (changed)
        {
            TrackingUpdated?.Invoke(this, EventArgs.Empty);
        }
    }
}

public sealed class AdminCommandRecipientStatus : ObservableObject
{
    private string _status = "Waiting for response";
    private string _responseMessage = string.Empty;
    private string _respondedAtLabel = string.Empty;
    private bool _hasResponded;

    public AdminCommandRecipientStatus(
        ClientSessionInfo session,
        ClientSessionCommand command)
    {
        CommandId = command.CommandId;
        SessionId = session.SessionId;
        UserLabel = session.UserLabel;
        LoginName = session.LoginName;
        MachineName = session.MachineName;
    }

    public long CommandId { get; }

    public Guid SessionId { get; }

    public string UserLabel { get; }

    public string LoginName { get; }

    public string MachineName { get; }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string ResponseMessage
    {
        get => _responseMessage;
        private set => SetProperty(ref _responseMessage, value);
    }

    public string RespondedAtLabel
    {
        get => _respondedAtLabel;
        private set => SetProperty(ref _respondedAtLabel, value);
    }

    public bool HasResponded
    {
        get => _hasResponded;
        private set => SetProperty(ref _hasResponded, value);
    }

    public string MachineLabel => string.IsNullOrWhiteSpace(LoginName)
        ? MachineName
        : $"{LoginName} on {MachineName}";

    public bool ApplyResponse(ClientSessionCommandResponse response)
    {
        if (HasResponded
            && Status.Equals(response.AcknowledgementResult, StringComparison.Ordinal)
            && ResponseMessage.Equals(response.ResponseLabel, StringComparison.Ordinal)
            && RespondedAtLabel.Equals(response.AcknowledgedAtLabel, StringComparison.Ordinal))
        {
            return false;
        }

        Status = response.AcknowledgementResult;
        ResponseMessage = response.ResponseLabel;
        RespondedAtLabel = response.AcknowledgedAtLabel;
        HasResponded = true;
        return true;
    }
}
