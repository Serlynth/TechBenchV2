using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using TechBench.Providers;

namespace TechBench.Services;

public sealed class SageNativeUiAutomation : ISageTimeTicketAutomation
{
    private const int OpenTimeTicketsCommandId = 30750;
    private const string TimeTicketsTitle = "Time Tickets";
    private const string NoteDialogTitle = "Time Tickets Note";

    private const string EmployeeAutomationId = "m_sdeemployeeWTId";
    private const string CustomerAutomationId = "m_sdecustomerWRId";
    private const string ActivityAutomationId = "m_sdeinventoryTDActivityItem";
    private const string TicketDateAutomationId = "m_datetpTmDTicketDate";
    private const string DurationAutomationId = "m_timespanTmDETDuration";

    private static readonly TimeSpan OpenWindowTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FieldTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FieldValueTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FieldValueStablePeriod = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan ValidationTimeout = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan NoteDialogOpenTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NoteDialogCloseTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NativePollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan AutomationPollInterval = TimeSpan.FromMilliseconds(250);

    public SageTimeTicketAutomationResult CreateTimeTicket(
        SageTimeTicketRequest request,
        CancellationToken cancellationToken = default)
    {
        var originalForeground = NativeMethods.GetForegroundWindow();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sage = ResolveSageProcess(request.ExpectedExecutablePath);
            var timeTickets = GetOrOpenTimeTickets(sage, cancellationToken);
            EnterValidatedTextField(
                timeTickets,
                EmployeeAutomationId,
                "Employee ID",
                request.EmployeeId,
                sage.ProcessId,
                cancellationToken);

            EnterValidatedTextField(
                timeTickets,
                CustomerAutomationId,
                "Customer ID",
                request.CustomerId,
                sage.ProcessId,
                cancellationToken);

            EnterValidatedTextField(
                timeTickets,
                ActivityAutomationId,
                "Activity Item",
                request.ActivityItemId,
                sage.ProcessId,
                cancellationToken);

            var dateText = request.TicketDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
            var dateField = WaitForFieldHandle(
                timeTickets,
                TicketDateAutomationId,
                "Ticket Date",
                sage.ProcessId,
                cancellationToken);
            EnterTextField(timeTickets, dateField, dateText, sage.ProcessId, cancellationToken);
            RequireDate(dateField, request.TicketDate);

            var durationText = $"{request.DurationMinutes / 60}:{request.DurationMinutes % 60:00}";
            var durationField = WaitForFieldHandle(
                timeTickets,
                DurationAutomationId,
                "Duration",
                sage.ProcessId,
                cancellationToken);
            EnterTextField(timeTickets, durationField, durationText, sage.ProcessId, cancellationToken);
            RequireDuration(durationField, request.DurationMinutes);

            var billing = ResolveBillingControls(timeTickets, sage.ProcessId);
            SelectComboItem(
                timeTickets,
                billing.BillingType,
                request.BillingType,
                sage.ProcessId,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(request.Note))
            {
                AddNote(timeTickets, sage.ProcessId, request.Note, cancellationToken);
            }

            // Sage can recreate native edit controls while dependent records resolve.
            // Always validate the live handles immediately before Save.
            var fields = ResolveTicketFields(timeTickets, sage.ProcessId, cancellationToken);
            var validation = ValidateCompletedTicket(
                timeTickets,
                fields,
                billing,
                request,
                sage.ProcessId,
                cancellationToken);

            if (!request.AutoSave)
            {
                var ticketNumber = WaitForTicketNumber(timeTickets, cancellationToken);
                TryRestoreForeground(originalForeground);
                var ticketLabel = string.IsNullOrWhiteSpace(ticketNumber) ? "" : $" #{ticketNumber}";
                return new SageTimeTicketAutomationResult(
                    true,
                    $"Filled Sage ticket{ticketLabel} and left it unsaved for review. {validation}",
                    ticketNumber);
            }

            InvokeToolbarButton(timeTickets, "Save");
            WaitForSaveResult(timeTickets, sage.ProcessId, cancellationToken);

            TryRestoreForeground(originalForeground);
            return new SageTimeTicketAutomationResult(
                true,
                $"Submitted Save for the Sage time ticket. {validation}",
                SaveSubmitted: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SageAutomationException ex)
        {
            return SageTimeTicketAutomationResult.Failed(ex.Message);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or Win32Exception)
        {
            return SageTimeTicketAutomationResult.Failed($"Sage desktop automation stopped safely: {ex.Message}");
        }
    }

    public static string CheckAvailability(string expectedExecutablePath = "")
    {
        try
        {
            var sage = ResolveSageProcess(expectedExecutablePath);
            return $"Found Sage 50 ({sage.ExecutablePath}) in the current Windows session.";
        }
        catch (SageAutomationException ex)
        {
            return ex.Message;
        }
    }

    internal static int FindItemNavigationDelta(IReadOnlyList<string> items, int currentIndex, string target)
    {
        var matches = items
            .Select((item, index) => new { item, index })
            .Where(candidate => candidate.item.Equals(target, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => candidate.index)
            .ToArray();

        if (matches.Length != 1)
        {
            throw new SageAutomationException(
                $"Billing Type must contain exactly one '{target}' option; found {matches.Length}.");
        }

        if (currentIndex < 0 || currentIndex >= items.Count)
        {
            throw new SageAutomationException("Sage did not expose the current Billing Type selection.");
        }

        return matches[0] - currentIndex;
    }

    private static SageProcessInfo ResolveSageProcess(string expectedExecutablePath)
    {
        var currentSession = Process.GetCurrentProcess().SessionId;
        var candidates = new List<SageProcessInfo>();
        foreach (var process in Process.GetProcessesByName("Peachw"))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != currentSession || process.HasExited)
                    {
                        continue;
                    }

                    var path = process.MainModule?.FileName ?? string.Empty;
                    if (!Path.GetFileName(path).Equals("Peachw.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(expectedExecutablePath)
                        && !Path.GetFullPath(path).Equals(
                            Path.GetFullPath(expectedExecutablePath),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    candidates.Add(new SageProcessInfo(process.Id, path));
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    // The process can exit while it is being inspected.
                }
            }
        }

        return candidates.Count switch
        {
            0 => throw new SageAutomationException("Open Sage 50 in the current Windows session before posting."),
            > 1 => throw new SageAutomationException("More than one Sage 50 process is running. Close the extra instance before posting."),
            _ => candidates[0]
        };
    }

    private static IntPtr OpenTimeTickets(SageProcessInfo sage, CancellationToken cancellationToken)
    {
        var topLevelWindows = EnumerateTopLevelWindows(sage.ProcessId)
            .Where(window => NativeMethods.IsWindowVisible(window) && NativeMethods.IsWindowEnabled(window))
            .ToArray();
        var menuHosts = topLevelWindows
            .Where(window => MenuContainsCommand(NativeMethods.GetMenu(window), OpenTimeTicketsCommandId))
            .ToArray();

        IntPtr commandTarget;
        if (menuHosts.Length == 1)
        {
            commandTarget = menuHosts[0];
        }
        else if (menuHosts.Length > 1)
        {
            throw new SageAutomationException("TechBench found multiple Sage windows containing the Time Tickets command.");
        }
        else
        {
            commandTarget = topLevelWindows
                .OrderByDescending(GetWindowArea)
                .FirstOrDefault();
            if (commandTarget == IntPtr.Zero)
            {
                throw new SageAutomationException("TechBench could not find Sage's main application window.");
            }
        }

        ActivateWindow(commandTarget, sage.ProcessId);
        NativeMethods.PostWindowCommand(commandTarget, OpenTimeTicketsCommandId);

        var timeTickets = WaitForWindow(sage.ProcessId, TimeTicketsTitle, OpenWindowTimeout, cancellationToken);
        ValidateWindow(timeTickets, sage.ProcessId, expectedRoot: IntPtr.Zero, expectedClassPrefix: null);
        ActivateWindow(timeTickets, sage.ProcessId);
        Thread.Sleep(100);

        return PrepareFreshTimeTicket(timeTickets, sage.ProcessId, cancellationToken);
    }

    private static IntPtr GetOrOpenTimeTickets(
        SageProcessInfo sage,
        CancellationToken cancellationToken)
    {
        var existing = FindTopLevelWindow(sage.ProcessId, TimeTicketsTitle);
        if (existing == IntPtr.Zero)
        {
            return OpenTimeTickets(sage, cancellationToken);
        }

        ValidateWindow(existing, sage.ProcessId, expectedRoot: IntPtr.Zero, expectedClassPrefix: null);
        if (!NativeMethods.IsWindowEnabled(existing))
        {
            throw new SageAutomationException(
                "The open Sage Time Tickets window is blocked by a dialog. Resolve that dialog before posting.");
        }

        ActivateWindow(existing, sage.ProcessId);
        Thread.Sleep(100);
        return PrepareFreshTimeTicket(existing, sage.ProcessId, cancellationToken);
    }

    private static IntPtr PrepareFreshTimeTicket(
        IntPtr timeTickets,
        int processId,
        CancellationToken cancellationToken)
    {
        if (!HasEnteredTicketData(timeTickets, processId, cancellationToken))
        {
            return timeTickets;
        }

        InvokeToolbarButton(timeTickets, "New");
        WaitForFreshBlankTicket(timeTickets, processId, cancellationToken);

        return timeTickets;
    }

    private static bool HasEnteredTicketData(
        IntPtr timeTickets,
        int processId,
        CancellationToken cancellationToken)
    {
        var values = new[]
        {
            NativeMethods.ReadWindowText(WaitForFieldHandle(
                timeTickets, EmployeeAutomationId, "Employee ID", processId, cancellationToken)),
            NativeMethods.ReadWindowText(WaitForFieldHandle(
                timeTickets, CustomerAutomationId, "Customer ID", processId, cancellationToken)),
            NativeMethods.ReadWindowText(WaitForFieldHandle(
                timeTickets, ActivityAutomationId, "Activity Item", processId, cancellationToken))
        };

        return ContainsEnteredTicketData(values);
    }

    private static void WaitForFreshBlankTicket(
        IntPtr timeTickets,
        int processId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var lastSelectorError = string.Empty;
        while (stopwatch.Elapsed < FieldValueTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!NativeMethods.IsWindowEnabled(timeTickets))
            {
                throw new SageAutomationException(
                    "Sage has an unsaved Time Ticket open. TechBench left the save/discard prompt untouched.");
            }

            try
            {
                var values = new[]
                {
                    NativeMethods.ReadWindowText(ResolveAutomationIdHandle(
                        timeTickets, EmployeeAutomationId, processId)),
                    NativeMethods.ReadWindowText(ResolveAutomationIdHandle(
                        timeTickets, CustomerAutomationId, processId)),
                    NativeMethods.ReadWindowText(ResolveAutomationIdHandle(
                        timeTickets, ActivityAutomationId, processId))
                };
                if (!ContainsEnteredTicketData(values))
                {
                    return;
                }

                lastSelectorError = string.Empty;
            }
            catch (SageAutomationException ex)
            {
                lastSelectorError = ex.Message;
            }

            Thread.Sleep(AutomationPollInterval);
        }

        var selectorDetail = string.IsNullOrWhiteSpace(lastSelectorError)
            ? string.Empty
            : $" Last selector check: {lastSelectorError}";
        throw new SageAutomationException(
            $"Sage did not present a fresh blank Time Ticket within {FieldValueTimeout.TotalSeconds:0} seconds after New.{selectorDetail}");
    }

    internal static bool ContainsEnteredTicketData(IEnumerable<string?> values) =>
        values.Any(value => !string.IsNullOrWhiteSpace(value));

    private static IntPtr WaitForFieldHandle(
        IntPtr timeTickets,
        string automationId,
        string fieldName,
        int processId,
        CancellationToken cancellationToken)
    {
        var result = IntPtr.Zero;
        var lastError = "No selector attempt completed.";
        try
        {
            WaitUntil(() =>
            {
                try
                {
                    result = ResolveAutomationIdHandle(timeTickets, automationId, processId);
                    return true;
                }
                catch (SageAutomationException ex)
                {
                    lastError = ex.Message;
                    return false;
                }
            }, FieldTimeout, cancellationToken, AutomationPollInterval);
        }
        catch (SageAutomationException ex) when (ex.Message.StartsWith("Sage did not reach", StringComparison.Ordinal))
        {
            throw new SageAutomationException(
                $"Time Tickets opened, but {fieldName} was not available. Last check: {lastError}");
        }

        return result != IntPtr.Zero
            ? result
            : throw new SageAutomationException($"Sage {fieldName} did not become available.");
    }

    private static IntPtr ResolveAutomationIdHandle(IntPtr root, string automationId, int processId)
    {
        var rootElement = AutomationElement.FromHandle(root)
            ?? throw new SageAutomationException("UI Automation could not attach to the Time Tickets window.");
        var localMatches = FindControlViewMatches(rootElement, automationId);
        var nativeEditHandles = ReadVerifiedFieldHandles(localMatches, root, processId);
        var projectionCount = localMatches.Length;

        if (nativeEditHandles.Length != 1)
        {
            throw new SageAutomationException(
                $"Expected one native WinForms EDIT for Sage field '{automationId}'; Control View exposed {projectionCount} cycle-suppressed projection(s) and {nativeEditHandles.Length} verified native handle(s).");
        }

        var handle = nativeEditHandles[0];
        ValidateWindow(handle, processId, root, "WindowsForms10.EDIT");
        return handle;
    }

    private static AutomationElement[] FindControlViewMatches(
        AutomationElement root,
        string automationId)
    {
        return EnumerateControlView(root)
            .Where(element => TryReadAutomationId(element).Equals(automationId, StringComparison.Ordinal))
            .ToArray();
    }

    private static AutomationElement[] EnumerateControlView(AutomationElement root)
    {
        const int maxElements = 512;
        var elements = new List<AutomationElement>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var walker = TreeWalker.ControlViewWalker;
        var pending = new Stack<AutomationElement>();
        pending.Push(root);

        while (pending.Count > 0 && visited.Count < maxElements)
        {
            var current = pending.Pop();
            var runtimeKey = TryReadRuntimeKey(current);
            if (!visited.Add(runtimeKey))
            {
                continue;
            }

            elements.Add(current);
            try
            {
                var children = new List<AutomationElement>();
                var child = walker.GetFirstChild(current);
                while (child is not null && children.Count < maxElements)
                {
                    children.Add(child);
                    child = walker.GetNextSibling(child);
                }

                for (var index = children.Count - 1; index >= 0; index--)
                {
                    pending.Push(children[index]);
                }
            }
            catch (Exception ex) when (ex is COMException or ElementNotAvailableException)
            {
                // Sage can recreate provider fragments while dependent fields validate.
            }
        }

        return elements.ToArray();
    }

    private static string TryReadAutomationId(AutomationElement element)
    {
        try
        {
            return element.Current.AutomationId ?? string.Empty;
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException)
        {
            return string.Empty;
        }
    }

    private static string TryReadName(AutomationElement element)
    {
        try
        {
            return element.Current.Name ?? string.Empty;
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException)
        {
            return string.Empty;
        }
    }

    private static string TryReadRuntimeKey(AutomationElement element)
    {
        try
        {
            var runtimeId = element.GetRuntimeId();
            if (runtimeId is { Length: > 0 })
            {
                return string.Join(".", runtimeId);
            }

            return $"HWND:{element.Current.NativeWindowHandle}:ID:{element.Current.AutomationId}:NAME:{element.Current.Name}";
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException)
        {
            return $"UNAVAILABLE:{element.GetHashCode()}";
        }
    }

    private static IntPtr[] ReadVerifiedFieldHandles(
        IEnumerable<AutomationElement> elements,
        IntPtr root,
        int processId)
    {
        return elements
            .Select(TryReadNativeWindowHandle)
            .Where(handle => handle != IntPtr.Zero)
            .Distinct()
            .Where(handle => IsVerifiedFieldHandle(handle, root, processId))
            .ToArray();
    }

    private static IntPtr TryReadNativeWindowHandle(AutomationElement element)
    {
        try
        {
            return new IntPtr(element.Current.NativeWindowHandle);
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException)
        {
            return IntPtr.Zero;
        }
    }

    private static bool IsVerifiedFieldHandle(IntPtr handle, IntPtr root, int processId)
    {
        return IsVerifiedNativeHandle(handle, root, processId, "WindowsForms10.EDIT");
    }

    private static bool IsVerifiedNativeHandle(
        IntPtr handle,
        IntPtr root,
        int processId,
        string expectedClassPrefix)
    {
        try
        {
            ValidateWindow(handle, processId, root, expectedClassPrefix);
            return true;
        }
        catch (SageAutomationException)
        {
            return false;
        }
    }

    private static void EnterTextField(
        IntPtr root,
        IntPtr field,
        string value,
        int processId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateWindow(field, processId, root, "WindowsForms10.EDIT");
        WithFocusedControl(root, field, processId, () =>
        {
            NativeMethods.SendMessageWithTimeout(
                field,
                NativeMethods.EmSetSel,
                UIntPtr.Zero,
                new IntPtr(-1),
                "select Sage field text");
            NativeMethods.SendUnicodeText(value);
            NativeMethods.SendVirtualKey(NativeMethods.VkTab);
            Thread.Sleep(120);
        });
    }

    private static IntPtr EnterValidatedTextField(
        IntPtr root,
        string automationId,
        string fieldName,
        string value,
        int processId,
        CancellationToken cancellationToken)
    {
        var field = WaitForFieldHandle(
            root,
            automationId,
            fieldName,
            processId,
            cancellationToken);
        EnterTextField(root, field, value, processId, cancellationToken);
        return WaitForStableTextFieldValue(
            root,
            automationId,
            fieldName,
            value,
            processId,
            cancellationToken);
    }

    private static IntPtr WaitForStableTextFieldValue(
        IntPtr root,
        string automationId,
        string fieldName,
        string expected,
        int processId,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var expectedText = expected.Trim();
        TimeSpan? matchingSince = null;
        var matchingHandle = IntPtr.Zero;
        var currentHandle = IntPtr.Zero;
        var lastActual = string.Empty;
        var lastSelectorError = string.Empty;

        while (stopwatch.Elapsed < FieldValueTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                currentHandle = ResolveAutomationIdHandle(root, automationId, processId);
                lastActual = NativeMethods.ReadWindowText(currentHandle).Trim();
                lastSelectorError = string.Empty;
                if (lastActual.Equals(expectedText, StringComparison.OrdinalIgnoreCase))
                {
                    if (matchingSince is null || matchingHandle != currentHandle)
                    {
                        matchingSince = stopwatch.Elapsed;
                        matchingHandle = currentHandle;
                    }

                    if (stopwatch.Elapsed - matchingSince.Value >= FieldValueStablePeriod)
                    {
                        return currentHandle;
                    }
                }
                else
                {
                    matchingSince = null;
                    matchingHandle = IntPtr.Zero;
                }
            }
            catch (SageAutomationException ex)
            {
                matchingSince = null;
                matchingHandle = IntPtr.Zero;
                lastSelectorError = ex.Message;
            }

            Thread.Sleep(AutomationPollInterval);
        }

        if (lastActual.Equals(expectedText, StringComparison.OrdinalIgnoreCase))
        {
            throw new SageAutomationException(
                $"{fieldName} '{expectedText}' did not reach a stable validated state within {FieldValueTimeout.TotalSeconds:0} seconds. No ticket was saved.");
        }

        var returnedText = string.IsNullOrWhiteSpace(lastActual) ? "blank" : $"'{lastActual}'";
        var selectorDetail = string.IsNullOrWhiteSpace(lastSelectorError)
            ? string.Empty
            : $" Last selector check: {lastSelectorError}";
        throw new SageAutomationException(
            $"Sage did not accept {fieldName} '{expectedText}'; the field returned {returnedText} after validation. No ticket was saved.{selectorDetail}");
    }

    private static TicketFields ResolveTicketFields(
        IntPtr root,
        int processId,
        CancellationToken cancellationToken)
    {
        return new TicketFields(
            WaitForFieldHandle(root, EmployeeAutomationId, "Employee ID", processId, cancellationToken),
            WaitForFieldHandle(root, CustomerAutomationId, "Customer ID", processId, cancellationToken),
            WaitForFieldHandle(root, ActivityAutomationId, "Activity Item", processId, cancellationToken),
            WaitForFieldHandle(root, TicketDateAutomationId, "Ticket Date", processId, cancellationToken),
            WaitForFieldHandle(root, DurationAutomationId, "Duration", processId, cancellationToken));
    }

    private static BillingControls ResolveBillingControls(IntPtr root, int processId)
    {
        var combos = EnumerateChildWindows(root)
            .Select(handle => NativeControl.FromHandle(handle, root))
            .Where(control => control.ClassName.StartsWith("WindowsForms10.COMBOBOX", StringComparison.Ordinal)
                && NativeMethods.IsWindowVisible(control.Handle))
            .ToArray();

        var statusMatches = combos
            .Where(control => control.Text.Equals("Billable", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (statusMatches.Length != 1)
        {
            throw new SageAutomationException($"Expected one native Billable ComboBox, found {statusMatches.Length}.");
        }

        var status = statusMatches[0];
        var billingTypeCandidates = combos
            .Where(control => control.Bounds.Top > status.Bounds.Bottom
                && Math.Abs(control.Bounds.Left - status.Bounds.Left) <= 12
                && Math.Abs(control.Bounds.Right - status.Bounds.Right) <= 12)
            .OrderBy(control => control.Bounds.Top - status.Bounds.Bottom)
            .ToArray();
        if (billingTypeCandidates.Length == 0
            || billingTypeCandidates[0].Bounds.Top - status.Bounds.Bottom > 40)
        {
            throw new SageAutomationException("TechBench could not resolve the Billing Type dropdown below Billable.");
        }

        var billingType = billingTypeCandidates[0];
        ValidateWindow(billingType.Handle, processId, root, "WindowsForms10.COMBOBOX");

        var payLevelMatches = combos
            .Where(control => control.Text.Equals("Regular", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (payLevelMatches.Length != 1)
        {
            throw new SageAutomationException($"Expected Pay Level Regular after Employee validation; found {payLevelMatches.Length} matches.");
        }

        var appliedMatches = combos
            .Where(control => control.Text.Equals("To a Customer Invoice", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (appliedMatches.Length != 1)
        {
            throw new SageAutomationException("Sage is not set to apply this ticket to a Customer Invoice.");
        }

        return new BillingControls(
            status,
            billingType,
            payLevelMatches[0],
            appliedMatches[0]);
    }

    private static void SelectComboItem(
        IntPtr root,
        NativeControl combo,
        string target,
        int processId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var items = NativeMethods.ReadComboItems(combo.Handle);
        var currentIndex = NativeMethods.ReadComboCurrentIndex(combo.Handle);
        var delta = FindItemNavigationDelta(items, currentIndex, target);

        WithFocusedControl(root, combo.Handle, processId, () =>
        {
            NativeMethods.SendVirtualKey(NativeMethods.VkF4);
            Thread.Sleep(80);
            var direction = delta >= 0 ? NativeMethods.VkDown : NativeMethods.VkUp;
            for (var index = 0; index < Math.Abs(delta); index++)
            {
                NativeMethods.SendVirtualKey(direction);
                Thread.Sleep(35);
            }

            NativeMethods.SendVirtualKey(NativeMethods.VkReturn);
            NativeMethods.SendVirtualKey(NativeMethods.VkTab);
            Thread.Sleep(120);
        });

        try
        {
            WaitUntil(
                () => NativeMethods.ReadWindowText(combo.Handle).Equals(target, StringComparison.OrdinalIgnoreCase),
                ValidationTimeout,
                cancellationToken);
        }
        catch (SageAutomationException ex) when (ex.Message.StartsWith("Sage did not reach", StringComparison.Ordinal))
        {
            throw new SageAutomationException(
                $"Billing Type did not commit '{target}' within {ValidationTimeout.TotalSeconds:0} seconds.");
        }
    }

    private static void AddNote(
        IntPtr timeTickets,
        int processId,
        string note,
        CancellationToken cancellationToken)
    {
        ActivateWindow(timeTickets, processId);
        // Sage's toolbar item is virtual (no HWND), and Invoke blocks while its modal dialog is open.
        var noteInvocation = Task.Run(() => InvokeToolbarButton(timeTickets, "Note"));
        var noteDialog = WaitForNoteDialog(processId, noteInvocation, cancellationToken);

        ValidateWindow(noteDialog, processId, expectedRoot: IntPtr.Zero, expectedClassPrefix: null);

        var controls = EnumerateChildWindows(noteDialog)
            .Select(handle => NativeControl.FromHandle(handle, noteDialog))
            .ToArray();
        var editors = controls
            .Where(control => control.ClassName.StartsWith("WindowsForms10.EDIT", StringComparison.Ordinal))
            .ToArray();
        if (editors.Length != 1)
        {
            throw new SageAutomationException($"Expected one Note editor, found {editors.Length}.");
        }

        ValidateWindow(editors[0].Handle, processId, noteDialog, "WindowsForms10.EDIT");
        NativeMethods.SetWindowText(editors[0].Handle, note);
        var noteReadback = NativeMethods.ReadWindowText(editors[0].Handle);
        if (!noteReadback.Equals(note, StringComparison.Ordinal))
        {
            throw new SageAutomationException("Sage Note text did not match after entry. The dialog was left open.");
        }

        var okButton = ResolveNativeNoteOkButton(noteDialog, processId, editors[0].Handle, controls);
        ValidateWindow(okButton, processId, noteDialog, "WindowsForms10.BUTTON");

        NativeMethods.SendMessageWithTimeout(
            okButton,
            NativeMethods.BmClick,
            UIntPtr.Zero,
            IntPtr.Zero,
            "accept Sage Note");
        WaitUntil(
            () => !NativeMethods.IsWindow(noteDialog),
            NoteDialogCloseTimeout,
            cancellationToken);
        WaitUntil(
            () => noteInvocation.IsCompleted,
            NoteDialogCloseTimeout,
            cancellationToken);
        _ = noteInvocation.Exception;

        if (!NativeMethods.IsWindowEnabled(timeTickets))
        {
            throw new SageAutomationException("The Note dialog closed, but Time Tickets did not become enabled.");
        }
    }

    private static IntPtr ResolveNativeNoteOkButton(
        IntPtr noteDialog,
        int processId,
        IntPtr noteEditor,
        IReadOnlyList<NativeControl> controls)
    {
        var nativeButtons = controls
            .Where(control => control.ClassName.StartsWith("WindowsForms10.BUTTON", StringComparison.Ordinal))
            .ToArray();
        var defaultButton = NativeMethods.ReadDefaultDialogButton(noteDialog);
        if (defaultButton != IntPtr.Zero
            && nativeButtons.Any(button => button.Handle == defaultButton)
            && IsVerifiedNativeHandle(defaultButton, noteDialog, processId, "WindowsForms10.BUTTON"))
        {
            return defaultButton;
        }

        var styledDefaults = nativeButtons
            .Where(button => NativeMethods.IsDefaultPushButton(button.Handle))
            .Where(button => IsVerifiedNativeHandle(button.Handle, noteDialog, processId, "WindowsForms10.BUTTON"))
            .ToArray();
        if (styledDefaults.Length == 1)
        {
            return styledDefaults[0].Handle;
        }

        var tabOrderedButton = NativeMethods.FindPreviousDialogButton(
            noteDialog,
            noteEditor,
            nativeButtons.Select(button => button.Handle).ToHashSet());
        if (tabOrderedButton != IntPtr.Zero
            && IsVerifiedNativeHandle(tabOrderedButton, noteDialog, processId, "WindowsForms10.BUTTON"))
        {
            return tabOrderedButton;
        }

        // Sage's Note dialog contains only OK and Cancel; Windows convention places OK first.
        var sameRowButtons = nativeButtons
            .Where(button => IsVerifiedNativeHandle(button.Handle, noteDialog, processId, "WindowsForms10.BUTTON"))
            .OrderBy(button => button.Bounds.Left)
            .ToArray();
        if (sameRowButtons.Length == 2
            && Math.Abs(sameRowButtons[0].Bounds.Top - sameRowButtons[1].Bounds.Top) <= 32)
        {
            return sameRowButtons[0].Handle;
        }

        throw new SageAutomationException(
            $"The note was entered and verified, but TechBench could not identify Sage's native default OK button ({nativeButtons.Length} native button(s), {styledDefaults.Length} default-styled button(s)).");
    }

    private static IntPtr WaitForNoteDialog(
        int processId,
        Task noteInvocation,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < NoteDialogOpenTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (noteInvocation.IsFaulted)
            {
                throw new SageAutomationException(
                    $"Sage Note could not be opened: {noteInvocation.Exception?.GetBaseException().Message}");
            }

            var noteDialog = FindTopLevelWindow(processId, NoteDialogTitle);
            if (noteDialog != IntPtr.Zero)
            {
                return noteDialog;
            }

            Thread.Sleep(NativePollInterval);
        }

        throw new SageAutomationException(
            $"Sage did not open the Note dialog within {NoteDialogOpenTimeout.TotalSeconds:0} seconds. The ticket was not saved.");
    }

    private static string ValidateCompletedTicket(
        IntPtr timeTickets,
        TicketFields fields,
        BillingControls billing,
        SageTimeTicketRequest request,
        int processId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateWindow(timeTickets, processId, IntPtr.Zero, expectedClassPrefix: null);
        if (!NativeMethods.IsWindowEnabled(timeTickets))
        {
            throw new SageAutomationException("Time Tickets became disabled by an unexpected Sage dialog.");
        }

        RequireExactText(fields.EmployeeId, request.EmployeeId, "Employee ID");
        RequireExactText(fields.CustomerId, request.CustomerId, "Customer ID");
        RequireExactText(fields.ActivityItem, request.ActivityItemId, "Activity Item");
        RequireDate(fields.TicketDate, request.TicketDate);
        RequireDuration(fields.Duration, request.DurationMinutes);

        RequireControlText(billing.AppliedTo, "To a Customer Invoice", "To be applied");
        RequireControlText(billing.BillingStatus, "Billable", "Billing Status");
        RequireControlText(billing.BillingType, request.BillingType, "Billing Type");
        RequireControlText(billing.PayLevel, request.ExpectedPayLevel, "Pay Level");

        var expectedUnits = Math.Round(request.DurationMinutes / 60m, 2, MidpointRounding.AwayFromZero);
        var unitDuration = TryReadLabeledDecimal(timeTickets, "Unit duration:");
        if (unitDuration.HasValue && unitDuration.Value != expectedUnits)
        {
            throw new SageAutomationException(
                $"Sage calculated Unit Duration {unitDuration.Value:0.00}; expected {expectedUnits:0.00}.");
        }

        var unitsText = unitDuration.HasValue ? unitDuration.Value.ToString("0.00", CultureInfo.InvariantCulture) : expectedUnits.ToString("0.00", CultureInfo.InvariantCulture);
        return $"Sage retained Activity Rate and unit duration {unitsText}. Billing Rate and Billing Amount remain entirely Sage-controlled.";
    }

    private static decimal? TryReadLabeledDecimal(IntPtr root, string labelText)
    {
        try
        {
            var rootElement = AutomationElement.FromHandle(root);
            if (rootElement is null)
            {
                return null;
            }

            var elements = rootElement.FindAll(TreeScope.Descendants, Condition.TrueCondition)
                .Cast<AutomationElement>()
                .Select(element => AutomationSnapshot.TryCreate(element))
                .Where(snapshot => snapshot is not null)
                .Cast<AutomationSnapshot>()
                .ToArray();
            var labels = elements
                .Where(element => element.Text.Trim().Equals(labelText, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (labels.Length != 1)
            {
                return null;
            }

            var label = labels[0];
            var candidates = elements
                .Where(element => element.Bounds.Left >= label.Bounds.Right - 8
                    && Math.Abs(CenterY(element.Bounds) - CenterY(label.Bounds)) <= 14
                    && TryParseDecimal(element.Text, out _))
                .OrderBy(element => element.Bounds.Left - label.Bounds.Right)
                .ToArray();
            return candidates.Length == 0 || !TryParseDecimal(candidates[0].Text, out var value)
                ? null
                : value;
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException)
        {
            return null;
        }
    }

    private static string? WaitForTicketNumber(IntPtr root, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < ValidationTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ticketNumber = TryReadTicketNumber(root);
            if (!string.IsNullOrWhiteSpace(ticketNumber))
            {
                return ticketNumber;
            }

            Thread.Sleep(AutomationPollInterval);
        }

        return null;
    }

    private static string? TryReadTicketNumber(IntPtr root)
    {
        try
        {
            var rootElement = AutomationElement.FromHandle(root);
            if (rootElement is null)
            {
                return null;
            }

            var elements = EnumerateControlView(rootElement)
                .Concat(rootElement.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>())
                .ToArray();
            foreach (var element in elements)
            {
                var name = TryReadName(element);
                var match = Regex.Match(name, @"Ticket\s*Number\s*:\s*(\d+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }

            foreach (var handle in EnumerateChildWindows(root))
            {
                var match = Regex.Match(
                    NativeMethods.ReadWindowText(handle),
                    @"Ticket\s*Number\s*:\s*(\d+)",
                    RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException)
        {
            return null;
        }

        return null;
    }

    private static void WaitForSaveResult(IntPtr timeTickets, int processId, CancellationToken cancellationToken)
    {
        Thread.Sleep(350);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateWindow(timeTickets, processId, IntPtr.Zero, expectedClassPrefix: null);
        if (!NativeMethods.IsWindowEnabled(timeTickets))
        {
            var foreground = NativeMethods.GetForegroundWindow();
            var title = foreground == IntPtr.Zero ? "unknown dialog" : NativeMethods.ReadTopLevelText(foreground);
            throw new SageAutomationException($"Sage displayed '{title}' while saving. TechBench left it untouched.");
        }
    }

    private static void InvokeToolbarButton(IntPtr root, string name)
    {
        var rootElement = AutomationElement.FromHandle(root)
            ?? throw new SageAutomationException("UI Automation could not attach to Time Tickets.");
        var matches = EnumerateControlView(rootElement)
            .Where(element => IsNamedButton(element, name))
            .ToArray();
        if (matches.Length != 1)
        {
            throw new SageAutomationException($"Expected one Sage '{name}' toolbar button, found {matches.Length}.");
        }

        if (!matches[0].TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)
            || pattern is not InvokePattern invokePattern)
        {
            throw new SageAutomationException($"The Sage '{name}' toolbar button cannot be invoked safely.");
        }

        invokePattern.Invoke();
    }

    private static bool IsNamedButton(AutomationElement element, string name)
    {
        try
        {
            return element.Current.ControlType == ControlType.Button
                && element.Current.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is COMException or ElementNotAvailableException)
        {
            return false;
        }
    }

    private static void RequireExactText(IntPtr handle, string expected, string fieldName)
    {
        var actual = NativeMethods.ReadWindowText(handle).Trim();
        if (!actual.Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            var returnedText = string.IsNullOrWhiteSpace(actual) ? "blank" : $"'{actual}'";
            throw new SageAutomationException(
                $"{fieldName} changed from '{expected.Trim()}' to {returnedText} before Save. No ticket was saved.");
        }
    }

    private static void RequireControlText(NativeControl control, string expected, string fieldName)
    {
        var actual = NativeMethods.ReadWindowText(control.Handle).Trim();
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new SageAutomationException($"{fieldName} is '{actual}', expected '{expected}'.");
        }
    }

    private static void RequireDate(IntPtr handle, DateTime expected)
    {
        var actual = NativeMethods.ReadWindowText(handle).Trim();
        if (!DateTime.TryParse(actual, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            && !DateTime.TryParse(actual, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
        {
            throw new SageAutomationException($"Ticket Date did not retain a valid date after validation: '{actual}'.");
        }

        if (parsed.Date != expected.Date)
        {
            throw new SageAutomationException($"Ticket Date became {parsed:d}; expected {expected:d}.");
        }
    }

    private static void RequireDuration(IntPtr handle, int expectedMinutes)
    {
        var actual = NativeMethods.ReadWindowText(handle).Trim();
        if (!TimeSpan.TryParse(actual, CultureInfo.CurrentCulture, out var parsed)
            && !TimeSpan.TryParse(actual, CultureInfo.InvariantCulture, out parsed))
        {
            throw new SageAutomationException($"Duration did not retain a valid value after validation: '{actual}'.");
        }

        if ((int)Math.Round(parsed.TotalMinutes) != expectedMinutes)
        {
            throw new SageAutomationException(
                $"Duration became {parsed:hh\\:mm}; expected {expectedMinutes / 60}:{expectedMinutes % 60:00}.");
        }
    }

    private static bool TryParseDecimal(string value, out decimal result)
    {
        return decimal.TryParse(value.Trim(), NumberStyles.Number, CultureInfo.CurrentCulture, out result)
            || decimal.TryParse(value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out result);
    }

    private static void WithFocusedControl(IntPtr root, IntPtr control, int processId, Action input)
    {
        ValidateWindow(root, processId, IntPtr.Zero, expectedClassPrefix: null);
        ValidateWindow(control, processId, root, expectedClassPrefix: null);
        if (!NativeMethods.IsWindowEnabled(root) || !NativeMethods.IsWindowEnabled(control))
        {
            throw new SageAutomationException("The verified Sage input control is disabled.");
        }

        NativeMethods.ShowWindow(root, NativeMethods.SwRestore);
        NativeMethods.BringWindowToTop(root);
        NativeMethods.SetForegroundWindow(root);

        if (NativeMethods.GetForegroundWindow() == root
            && NativeMethods.SetFocus(control) != IntPtr.Zero
            && FocusMatches(control))
        {
            input();
            return;
        }

        WithAttachedInput(root, control, input);
    }

    private static void WithAttachedInput(IntPtr root, IntPtr control, Action input)
    {
        var currentThread = NativeMethods.GetCurrentThreadId();
        var targetThread = NativeMethods.GetWindowThreadProcessId(root, out _);
        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero
            ? 0
            : NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var attached = new List<uint>();

        try
        {
            foreach (var thread in new[] { foregroundThread, targetThread }.Where(thread => thread != 0 && thread != currentThread).Distinct())
            {
                if (!NativeMethods.AttachThreadInput(currentThread, thread, true))
                {
                    throw new SageAutomationException("Windows did not allow TechBench to focus the verified Sage control.");
                }

                attached.Add(thread);
            }

            NativeMethods.ShowWindow(root, NativeMethods.SwRestore);
            NativeMethods.BringWindowToTop(root);
            NativeMethods.SetForegroundWindow(root);
            if (NativeMethods.GetForegroundWindow() != root)
            {
                throw new SageAutomationException("Time Tickets could not be brought to the foreground.");
            }

            NativeMethods.SetFocus(control);
            if (!FocusMatches(control))
            {
                throw new SageAutomationException("The verified Sage control could not receive focus.");
            }

            input();
        }
        finally
        {
            for (var index = attached.Count - 1; index >= 0; index--)
            {
                NativeMethods.AttachThreadInput(currentThread, attached[index], false);
            }
        }
    }

    private static bool FocusMatches(IntPtr control)
    {
        var focus = NativeMethods.GetFocus();
        return focus == control || (focus != IntPtr.Zero && NativeMethods.IsChild(control, focus));
    }

    private static void ActivateWindow(IntPtr root, int processId)
    {
        ValidateWindow(root, processId, IntPtr.Zero, expectedClassPrefix: null);
        NativeMethods.ShowWindow(root, NativeMethods.SwRestore);
        NativeMethods.BringWindowToTop(root);
        NativeMethods.SetForegroundWindow(root);
        if (NativeMethods.GetForegroundWindow() == root)
        {
            return;
        }

        var currentThread = NativeMethods.GetCurrentThreadId();
        var targetThread = NativeMethods.GetWindowThreadProcessId(root, out _);
        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero
            ? 0
            : NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var attached = new List<uint>();
        try
        {
            foreach (var thread in new[] { foregroundThread, targetThread }.Where(thread => thread != 0 && thread != currentThread).Distinct())
            {
                if (!NativeMethods.AttachThreadInput(currentThread, thread, true))
                {
                    throw new SageAutomationException("Windows did not allow TechBench to activate Sage.");
                }

                attached.Add(thread);
            }

            NativeMethods.ShowWindow(root, NativeMethods.SwRestore);
            NativeMethods.BringWindowToTop(root);
            NativeMethods.SetForegroundWindow(root);
            if (NativeMethods.GetForegroundWindow() != root)
            {
                throw new SageAutomationException("Sage could not be brought to the foreground.");
            }
        }
        finally
        {
            for (var index = attached.Count - 1; index >= 0; index--)
            {
                NativeMethods.AttachThreadInput(currentThread, attached[index], false);
            }
        }
    }

    private static void TryRestoreForeground(IntPtr originalForeground)
    {
        if (originalForeground != IntPtr.Zero && NativeMethods.IsWindow(originalForeground))
        {
            NativeMethods.SetForegroundWindow(originalForeground);
        }
    }

    private static IntPtr WaitForWindow(
        int processId,
        string title,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var result = IntPtr.Zero;
        WaitUntil(() =>
        {
            result = FindTopLevelWindow(processId, title);
            return result != IntPtr.Zero;
        }, timeout, cancellationToken, NativePollInterval);
        return result;
    }

    private static void WaitUntil(
        Func<bool> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        TimeSpan? pollInterval = null)
    {
        var delay = pollInterval ?? NativePollInterval;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (condition())
            {
                return;
            }

            Thread.Sleep(delay);
        }

        throw new SageAutomationException($"Sage did not reach the expected state within {timeout.TotalSeconds:0} seconds.");
    }

    private static IntPtr FindTopLevelWindow(int processId, string exactTitle)
    {
        var matches = EnumerateTopLevelWindows(processId)
            .Where(NativeMethods.IsWindowVisible)
            .Where(window => NativeMethods.ReadTopLevelText(window).Equals(exactTitle, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length switch
        {
            0 => IntPtr.Zero,
            1 => matches[0],
            _ => throw new SageAutomationException($"Found multiple Sage windows titled '{exactTitle}'.")
        };
    }

    private static IReadOnlyList<IntPtr> EnumerateTopLevelWindows(int processId)
    {
        var result = new List<IntPtr>();
        NativeMethods.EnumWindows((window, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(window, out var ownerProcessId);
            if (ownerProcessId == processId)
            {
                result.Add(window);
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static IReadOnlyList<IntPtr> EnumerateChildWindows(IntPtr parent)
    {
        var result = new List<IntPtr>();
        NativeMethods.EnumChildWindows(parent, (window, _) =>
        {
            result.Add(window);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static bool MenuContainsCommand(IntPtr menu, int commandId)
    {
        if (menu == IntPtr.Zero)
        {
            return false;
        }

        var count = NativeMethods.GetMenuItemCount(menu);
        for (var index = 0; index < count; index++)
        {
            var id = NativeMethods.GetMenuItemID(menu, index);
            if (id == commandId)
            {
                return true;
            }

            var submenu = NativeMethods.GetSubMenu(menu, index);
            if (submenu != IntPtr.Zero && MenuContainsCommand(submenu, commandId))
            {
                return true;
            }
        }

        return false;
    }

    private static long GetWindowArea(IntPtr window)
    {
        return NativeMethods.GetWindowRect(window, out var rect)
            ? Math.Max(0, rect.Right - rect.Left) * (long)Math.Max(0, rect.Bottom - rect.Top)
            : 0;
    }

    private static void ValidateWindow(
        IntPtr window,
        int processId,
        IntPtr expectedRoot,
        string? expectedClassPrefix)
    {
        if (window == IntPtr.Zero || !NativeMethods.IsWindow(window))
        {
            throw new SageAutomationException("A verified Sage window no longer exists.");
        }

        NativeMethods.GetWindowThreadProcessId(window, out var actualProcessId);
        if (actualProcessId != processId)
        {
            throw new SageAutomationException("A Sage control changed process ownership before input.");
        }

        if (expectedRoot != IntPtr.Zero && !NativeMethods.IsChild(expectedRoot, window))
        {
            throw new SageAutomationException("A Sage control moved outside the verified Time Tickets hierarchy.");
        }

        var className = NativeMethods.ReadClassName(window);
        if (!string.IsNullOrWhiteSpace(expectedClassPrefix)
            && !className.StartsWith(expectedClassPrefix, StringComparison.Ordinal))
        {
            throw new SageAutomationException(
                $"A Sage control class changed from '{expectedClassPrefix}' to '{className}'.");
        }

        if (expectedRoot != IntPtr.Zero
            && (!NativeMethods.GetWindowRect(window, out var childBounds)
                || !NativeMethods.GetWindowRect(expectedRoot, out var rootBounds)
                || childBounds.Left < rootBounds.Left
                || childBounds.Top < rootBounds.Top
                || childBounds.Right > rootBounds.Right
                || childBounds.Bottom > rootBounds.Bottom))
        {
            throw new SageAutomationException("A Sage control moved outside the Time Tickets window bounds.");
        }
    }

    private static double CenterY(System.Windows.Rect rect) => rect.Top + rect.Height / 2;

    private sealed record SageProcessInfo(int ProcessId, string ExecutablePath);

    private sealed record TicketFields(
        IntPtr EmployeeId,
        IntPtr CustomerId,
        IntPtr ActivityItem,
        IntPtr TicketDate,
        IntPtr Duration);

    private sealed record BillingControls(
        NativeControl BillingStatus,
        NativeControl BillingType,
        NativeControl PayLevel,
        NativeControl AppliedTo);

    private sealed record NativeControl(IntPtr Handle, string ClassName, string Text, NativeMethods.Rect Bounds)
    {
        public static NativeControl FromHandle(IntPtr handle, IntPtr root)
        {
            NativeMethods.GetWindowRect(handle, out var bounds);
            NativeMethods.GetWindowRect(root, out var rootBounds);
            bounds.Left -= rootBounds.Left;
            bounds.Right -= rootBounds.Left;
            bounds.Top -= rootBounds.Top;
            bounds.Bottom -= rootBounds.Top;
            return new NativeControl(
                handle,
                NativeMethods.ReadClassName(handle),
                NativeMethods.ReadWindowText(handle).Trim(),
                bounds);
        }
    }

    private sealed record AutomationSnapshot(string Text, System.Windows.Rect Bounds)
    {
        public static AutomationSnapshot? TryCreate(AutomationElement element)
        {
            try
            {
                var text = element.Current.Name ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text)
                    && element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                    && pattern is ValuePattern valuePattern)
                {
                    text = valuePattern.Current.Value ?? string.Empty;
                }

                return new AutomationSnapshot(text, element.Current.BoundingRectangle);
            }
            catch (Exception ex) when (ex is COMException or ElementNotAvailableException)
            {
                return null;
            }
        }
    }

    internal sealed class SageAutomationException(string message) : Exception(message);

    private static class NativeMethods
    {
        internal const uint WmCommand = 0x0111;
        internal const uint WmGetText = 0x000D;
        internal const uint WmGetTextLength = 0x000E;
        internal const uint WmSetText = 0x000C;
        internal const uint EmSetSel = 0x00B1;
        internal const uint BmClick = 0x00F5;
        internal const uint CbGetCount = 0x0146;
        internal const uint CbGetCurSel = 0x0147;
        internal const uint CbGetLbText = 0x0148;
        internal const uint CbGetLbTextLen = 0x0149;
        private const uint DmGetDefId = 0x0400;
        private const ushort DcHasDefId = 0x534B;
        private const int GwlStyle = -16;
        private const long BsTypeMask = 0x0000000F;
        private const long BsDefPushButton = 0x00000001;

        internal const ushort VkTab = 0x09;
        internal const ushort VkReturn = 0x0D;
        internal const ushort VkUp = 0x26;
        internal const ushort VkDown = 0x28;
        internal const ushort VkF4 = 0x73;
        internal const int SwRestore = 9;

        private const uint InputKeyboard = 1;
        private const uint KeyeventfKeyup = 0x0002;
        private const uint KeyeventfUnicode = 0x0004;
        private const uint SmtoAbortIfHung = 0x0002;
        private const uint MessageTimeoutMilliseconds = 2000;

        internal delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            internal uint Type;
            internal InputUnion Union;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] internal MouseInput Mouse;
            [FieldOffset(0)] internal KeyboardInput Keyboard;
            [FieldOffset(0)] internal HardwareInput Hardware;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInput
        {
            internal int Dx;
            internal int Dy;
            internal uint MouseData;
            internal uint Flags;
            internal uint Time;
            internal UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            internal ushort VirtualKey;
            internal ushort ScanCode;
            internal uint Flags;
            internal uint Time;
            internal UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HardwareInput
        {
            internal uint Message;
            internal ushort ParamLow;
            internal ushort ParamHigh;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowEnabled(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsChild(IntPtr parent, IntPtr child);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BringWindowToTop(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        internal static extern IntPtr SetFocus(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetFocus();

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool value);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetMenu(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern int GetMenuItemCount(IntPtr menu);

        [DllImport("user32.dll")]
        internal static extern int GetMenuItemID(IntPtr menu, int position);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetSubMenu(IntPtr menu, int position);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr window, out Rect rect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDlgItem(IntPtr dialog, int itemId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetNextDlgTabItem(
            IntPtr dialog,
            IntPtr control,
            [MarshalAs(UnmanagedType.Bool)] bool previous);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
        private static extern IntPtr SendMessageTimeoutPointer(
            IntPtr window,
            uint message,
            UIntPtr wParam,
            IntPtr lParam,
            uint flags,
            uint timeout,
            out UIntPtr result);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
        private static extern IntPtr SendMessageTimeoutBuffer(
            IntPtr window,
            uint message,
            UIntPtr wParam,
            StringBuilder lParam,
            uint flags,
            uint timeout,
            out UIntPtr result);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
        private static extern IntPtr SendMessageTimeoutString(
            IntPtr window,
            uint message,
            UIntPtr wParam,
            string lParam,
            uint flags,
            uint timeout,
            out UIntPtr result);

        internal static string ReadClassName(IntPtr window)
        {
            var buffer = new StringBuilder(256);
            GetClassName(window, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        internal static string ReadTopLevelText(IntPtr window)
        {
            var length = GetWindowTextLength(window);
            var buffer = new StringBuilder(Math.Max(length + 1, 256));
            GetWindowText(window, buffer, buffer.Capacity);
            return buffer.ToString();
        }

        internal static string ReadWindowText(IntPtr window)
        {
            var length = ReadMessageResult(window, WmGetTextLength, UIntPtr.Zero, IntPtr.Zero, "read Sage text length");
            var capacity = checked((int)Math.Min(length + 1, 32768));
            var buffer = new StringBuilder(Math.Max(capacity, 2));
            if (SendMessageTimeoutBuffer(
                    window,
                    WmGetText,
                    (UIntPtr)buffer.Capacity,
                    buffer,
                    SmtoAbortIfHung,
                    MessageTimeoutMilliseconds,
                    out _) == IntPtr.Zero)
            {
                throw new SageAutomationException("Sage did not respond while TechBench read a verified control.");
            }

            return buffer.ToString();
        }

        internal static void SetWindowText(IntPtr window, string value)
        {
            if (SendMessageTimeoutString(
                    window,
                    WmSetText,
                    UIntPtr.Zero,
                    value,
                    SmtoAbortIfHung,
                    MessageTimeoutMilliseconds,
                    out _) == IntPtr.Zero)
            {
                throw new SageAutomationException("Sage did not respond while TechBench wrote the verified Note editor.");
            }
        }

        internal static IReadOnlyList<string> ReadComboItems(IntPtr combo)
        {
            var count = checked((int)ReadMessageResult(combo, CbGetCount, UIntPtr.Zero, IntPtr.Zero, "read Billing Type item count"));
            if (count <= 0 || count > 100)
            {
                throw new SageAutomationException($"Billing Type exposed an invalid item count: {count}.");
            }

            var items = new List<string>(count);
            for (var index = 0; index < count; index++)
            {
                var length = checked((int)ReadMessageResult(combo, CbGetLbTextLen, (UIntPtr)index, IntPtr.Zero, "read Billing Type item length"));
                var buffer = new StringBuilder(Math.Max(length + 1, 2));
                if (SendMessageTimeoutBuffer(
                        combo,
                        CbGetLbText,
                        (UIntPtr)index,
                        buffer,
                        SmtoAbortIfHung,
                        MessageTimeoutMilliseconds,
                        out _) == IntPtr.Zero)
                {
                    throw new SageAutomationException("Sage did not respond while TechBench read Billing Type items.");
                }

                items.Add(buffer.ToString());
            }

            return items;
        }

        internal static int ReadComboCurrentIndex(IntPtr combo)
        {
            return checked((int)ReadMessageResult(combo, CbGetCurSel, UIntPtr.Zero, IntPtr.Zero, "read Billing Type selection"));
        }

        internal static void SendMessageWithTimeout(
            IntPtr window,
            uint message,
            UIntPtr wParam,
            IntPtr lParam,
            string operation)
        {
            _ = ReadMessageResult(window, message, wParam, lParam, operation);
        }

        internal static void PostWindowCommand(IntPtr window, int commandId)
        {
            if (!PostMessage(window, WmCommand, (UIntPtr)commandId, IntPtr.Zero))
            {
                throw new SageAutomationException(
                    $"Windows could not queue Sage's Time Tickets command: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            }
        }

        internal static IntPtr ReadDefaultDialogButton(IntPtr dialog)
        {
            var result = unchecked((ulong)ReadMessageResult(
                dialog,
                DmGetDefId,
                UIntPtr.Zero,
                IntPtr.Zero,
                "read Sage Note's default button"));
            if ((ushort)((result >> 16) & 0xFFFF) != DcHasDefId)
            {
                return IntPtr.Zero;
            }

            return GetDlgItem(dialog, (int)(result & 0xFFFF));
        }

        internal static bool IsDefaultPushButton(IntPtr button)
        {
            var style = IntPtr.Size == 8
                ? GetWindowLongPtr64(button, GwlStyle).ToInt64()
                : GetWindowLong32(button, GwlStyle);
            return (style & BsTypeMask) == BsDefPushButton;
        }

        internal static IntPtr FindPreviousDialogButton(
            IntPtr dialog,
            IntPtr start,
            IReadOnlySet<IntPtr> buttons)
        {
            var current = start;
            for (var index = 0; index < 16; index++)
            {
                var next = GetNextDlgTabItem(dialog, current, previous: true);
                if (next == IntPtr.Zero || next == start)
                {
                    return IntPtr.Zero;
                }

                if (buttons.Contains(next))
                {
                    return next;
                }

                current = next;
            }

            return IntPtr.Zero;
        }

        private static long ReadMessageResult(
            IntPtr window,
            uint message,
            UIntPtr wParam,
            IntPtr lParam,
            string operation)
        {
            if (SendMessageTimeoutPointer(
                    window,
                    message,
                    wParam,
                    lParam,
                    SmtoAbortIfHung,
                    MessageTimeoutMilliseconds,
                    out var result) == IntPtr.Zero)
            {
                throw new SageAutomationException($"Sage did not respond while TechBench attempted to {operation}.");
            }

            return unchecked((long)result.ToUInt64());
        }

        internal static void SendUnicodeText(string value)
        {
            var inputs = new List<Input>(value.Length * 2);
            foreach (var character in value)
            {
                inputs.Add(CreateUnicodeInput(character, keyUp: false));
                inputs.Add(CreateUnicodeInput(character, keyUp: true));
            }

            SendInputs(inputs);
        }

        internal static void SendVirtualKey(ushort virtualKey)
        {
            SendInputs([
                CreateVirtualKeyInput(virtualKey, keyUp: false),
                CreateVirtualKeyInput(virtualKey, keyUp: true)
            ]);
        }

        private static void SendInputs(IReadOnlyCollection<Input> inputs)
        {
            var array = inputs.ToArray();
            var sent = SendInput((uint)array.Length, array, Marshal.SizeOf<Input>());
            if (sent != array.Length)
            {
                throw new SageAutomationException(
                    $"Windows delivered {sent} of {array.Length} verified Sage keyboard events.");
            }
        }

        private static Input CreateUnicodeInput(char character, bool keyUp)
        {
            return new Input
            {
                Type = InputKeyboard,
                Union = new InputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        ScanCode = character,
                        Flags = KeyeventfUnicode | (keyUp ? KeyeventfKeyup : 0)
                    }
                }
            };
        }

        private static Input CreateVirtualKeyInput(ushort virtualKey, bool keyUp)
        {
            return new Input
            {
                Type = InputKeyboard,
                Union = new InputUnion
                {
                    Keyboard = new KeyboardInput
                    {
                        VirtualKey = virtualKey,
                        Flags = keyUp ? KeyeventfKeyup : 0
                    }
                }
            };
        }
    }
}
