using System.Globalization;
using System.Text.Json;
using TechBench.Models;
using TechBench.Services;

namespace TechBench.Providers;

public sealed class SageNativeUiPoster : IWorkEntryPoster
{
    private const int MaxDurationMinutes = 23 * 60 + 59;
    private static readonly TimeSpan OdbcVerificationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan[] VerificationDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromMilliseconds(300),
        TimeSpan.FromMilliseconds(700),
        TimeSpan.FromMilliseconds(1500),
        TimeSpan.FromMilliseconds(2500)
    ];

    private readonly ISageTimeTicketAutomation _automation;
    private readonly ISageOdbcProcessClient _odbcClient;

    public SageNativeUiPoster()
        : this(new SageNativeUiAutomation(), new SageOdbcProcessClient())
    {
    }

    public SageNativeUiPoster(ISageTimeTicketAutomation automation)
        : this(automation, new SageOdbcProcessClient())
    {
    }

    public SageNativeUiPoster(
        ISageTimeTicketAutomation automation,
        ISageTimeTicketVerifier ticketVerifier)
        : this(automation, new InProcessSageOdbcClient(ticketVerifier))
    {
    }

    public SageNativeUiPoster(
        ISageTimeTicketAutomation automation,
        ISageOdbcProcessClient odbcClient)
    {
        _automation = automation;
        _odbcClient = odbcClient;
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

        var reference = request.AutoSave || string.IsNullOrWhiteSpace(automationResult.TicketNumber)
            ? null
            : $"SAGE-{automationResult.TicketNumber}";

        if (!request.AutoSave)
        {
            return PostingResult.Succeeded(
                automationResult.Message,
                payload,
                reference,
                markPosted: false);
        }

        if (!automationResult.SaveSubmitted)
        {
            return PostingResult.Failed(
                "Sage automation completed without confirming that the Save command was submitted. The entry remains Sage pending.",
                payload);
        }

        SageTimeTicketVerificationResult verification;
        try
        {
            verification = await VerifySubmittedSaveAsync(
                settings,
                request,
                ticketNumber: null,
                cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            var detail = ex is TimeoutException
                ? $"ODBC did not respond within {OdbcVerificationTimeout.TotalSeconds:0} seconds."
                : ex.Message;
            return PostingResult.Uncertain(
                $"{automationResult.Message} Read-only ODBC confirmation could not run: {detail} The entry remains Sage pending.",
                payload,
                reference);
        }

        var verifiedTicketNumber = verification.IsSaved ? verification.TicketNumber : null;
        reference = string.IsNullOrWhiteSpace(verifiedTicketNumber)
            ? null
            : $"SAGE-{verifiedTicketNumber}";

        return verification.IsSaved
            ? PostingResult.Succeeded(
                $"{automationResult.Message} {verification.Message}",
                payload,
                reference)
            : PostingResult.Uncertain(
                $"{automationResult.Message} {verification.Message}",
                payload,
                reference);
    }

    private async Task<SageTimeTicketVerificationResult> VerifySubmittedSaveAsync(
        IReadOnlyDictionary<string, string> settings,
        SageTimeTicketRequest request,
        string? ticketNumber,
        CancellationToken cancellationToken)
    {
        var verificationRequest = new SageTimeTicketVerificationRequest(
            ticketNumber,
            request.TicketDate,
            request.DurationMinutes,
            request.Note);
        SageTimeTicketVerificationResult? result = null;

        foreach (var delay in VerificationDelays)
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            result = await _odbcClient.VerifyTimeTicketAsync(
                settings.GetValueOrDefault("Sage.Dsn", string.Empty),
                settings.GetValueOrDefault("Sage.Username", string.Empty),
                settings.GetValueOrDefault("Sage.Password", string.Empty),
                verificationRequest,
                cancellationToken);
            if (result.Found)
            {
                break;
            }
        }

        return result ?? new SageTimeTicketVerificationResult(
            false,
            false,
            "The saved Sage ticket was not visible through ODBC. The entry remains Sage pending.",
            ticketNumber);
    }

    internal static SageTimeTicketRequest BuildRequest(
        WorkEntry entry,
        Client client,
        Ticket? ticket,
        IReadOnlyDictionary<string, string> settings)
    {
        var customerId = client.SageCustomerId?.Trim() ?? string.Empty;
        var autoSave = settings.TryGetValue("Sage.NativeAutoSave", out var configuredAutoSave)
            && bool.TryParse(configuredAutoSave, out var enabled)
            && enabled;

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
            autoSave,
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
            return "A TechBench Admin must configure the shared Sage activity item in TechBench Server Manager.";
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
