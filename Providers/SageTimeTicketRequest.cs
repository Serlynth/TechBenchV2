namespace TechBench.Providers;

public sealed record SageTimeTicketRequest(
    int WorkEntryId,
    DateTime TicketDate,
    int DurationMinutes,
    string EmployeeId,
    string CustomerId,
    string ActivityItemId,
    string BillingType,
    string ExpectedPayLevel,
    string Note,
    bool AutoSave,
    string ExpectedExecutablePath);

public sealed record SageTimeTicketAutomationResult(
    bool Success,
    string Message,
    string? TicketNumber = null,
    bool SaveSubmitted = false)
{
    public static SageTimeTicketAutomationResult Failed(string message) => new(false, message);
}
