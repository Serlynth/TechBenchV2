namespace TechBench.Providers;

public interface ISageTimeTicketAutomation
{
    SageTimeTicketAutomationResult CreateTimeTicket(
        SageTimeTicketRequest request,
        CancellationToken cancellationToken = default);
}
