using System.Globalization;
using System.Text.Json;
using TechBench.Models;
using TechBench.Services;

namespace TechBench.Providers;

public sealed class SageNativeUiPoster : IWorkEntryPoster
{
    private const int MaxDurationMinutes = 23 * 60 + 59;
    private readonly ISageTimeTicketAutomation _automation;

    public SageNativeUiPoster()
        : this(new SageNativeUiAutomation())
    {
    }

    public SageNativeUiPoster(ISageTimeTicketAutomation automation)
    {
        _automation = automation;
    }

    public string DestinationName => "Sage 50";

    public async Task<PostingResult> PostAsync(
        WorkEntry entry,
        Client client,
        Ticket? ticket,
        IReadOnlyDictionary<string, string> settings,
        CancellationToken cancellationToken = default)
    {
        var request = BuildRequest(entry, client, ticket, settings);
        var payload = BuildPayload(request, ticket);
        var validationError = Validate(entry, request);
        if (validationError is not null)
        {
            return PostingResult.Failed(validationError, payload);
        }

        SageTimeTicketAutomationResult automationResult;
        try
        {
            automationResult = await Task.Run(
                () => _automation.CreateTimeTicket(request, cancellationToken),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PostingResult.Failed($"Sage desktop automation failed: {ex.Message}", payload);
        }

        if (!automationResult.Success)
        {
            return PostingResult.Failed(automationResult.Message, payload);
        }

        var reference = string.IsNullOrWhiteSpace(automationResult.TicketNumber)
            ? null
            : $"SAGE-{automationResult.TicketNumber}";

        if (!automationResult.SaveSubmitted)
        {
            return PostingResult.Failed(
                "Sage automation completed without confirming that the Save command was submitted. The entry remains Sage pending.",
                payload);
        }

        return PostingResult.Succeeded(
            automationResult.Message,
            payload,
            reference);
    }

    internal static SageTimeTicketRequest BuildRequest(
        WorkEntry entry,
        Client client,
        Ticket? ticket,
        IReadOnlyDictionary<string, string> settings)
    {
        var customerId = client.SageCustomerId?.Trim() ?? string.Empty;
        return new SageTimeTicketRequest(
            entry.Id,
            entry.WorkDate.Date,
            entry.DurationMinutes,
            settings.GetValueOrDefault("Sage.EmployeeId", string.Empty).Trim(),
            customerId,
            settings.GetValueOrDefault("Sage.ActivityItemId", string.Empty).Trim(),
            "Activity Rate",
            "Regular",
            BuildSageNote(entry),
            AutoSave: true,
            settings.GetValueOrDefault("Sage.ExecutablePath", string.Empty).Trim());
    }

    internal static string? Validate(WorkEntry entry, SageTimeTicketRequest request)
    {
        if (entry.Id <= 0)
        {
            return "Save the TechBench work entry before creating a Sage ticket.";
        }

        if (!entry.Billable)
        {
            return "Native Sage posting currently supports billable entries only.";
        }

        if (request.DurationMinutes <= 0)
        {
            return "Sage posting requires a positive duration.";
        }

        if (request.DurationMinutes > MaxDurationMinutes)
        {
            return "Sage time-ticket duration cannot exceed 23:59.";
        }

        if (string.IsNullOrWhiteSpace(request.EmployeeId))
        {
            return "Enter the Sage Employee ID in Settings.";
        }

        if (string.IsNullOrWhiteSpace(request.CustomerId))
        {
            return $"The selected client '{entry.ClientDisplay}' is not linked to a Sage Customer ID. Select a synced Sage client or map this client first.";
        }

        if (string.IsNullOrWhiteSpace(request.ActivityItemId))
        {
            return "Enter your Sage activity item ID in TechBench Settings.";
        }

        return null;
    }

    private static string BuildSageNote(WorkEntry entry)
    {
        var note = entry.Note.Trim();
        return note.Length <= 2000 ? note : note[..2000];
    }

    private static string BuildPayload(SageTimeTicketRequest request, Ticket? ticket)
    {
        return JsonSerializer.Serialize(new
        {
            Destination = "Sage 50 native Time Ticket",
            request.WorkEntryId,
            TicketDate = request.TicketDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            request.EmployeeId,
            request.CustomerId,
            request.ActivityItemId,
            LinkedTicket = ticket?.TicketNumber,
            request.DurationMinutes,
            Duration = $"{request.DurationMinutes / 60}:{request.DurationMinutes % 60:00}",
            request.BillingType,
            request.ExpectedPayLevel,
            request.Note,
            request.AutoSave
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
