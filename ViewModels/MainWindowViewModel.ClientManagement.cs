using System.IO;
using Microsoft.Data.SqlClient;
using TechBench.Services;

namespace TechBench.ViewModels;

public sealed partial class MainWindowViewModel
{
    private string _clientNameEditText = string.Empty;
    private string? _suggestedClientName;
    private string _clientNameSuggestionText =
        "Select a client to review or change its TechBench name.";

    public RelayCommand UseSuggestedClientNameCommand { get; private set; } = null!;

    public RelayCommand SaveClientNameCommand { get; private set; } = null!;

    public RelayCommand ExportClientMatchWorkbookCommand { get; private set; } = null!;

    public string ClientNameEditText
    {
        get => _clientNameEditText;
        set
        {
            if (SetProperty(ref _clientNameEditText, value))
            {
                UseSuggestedClientNameCommand.RaiseCanExecuteChanged();
                SaveClientNameCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ClientNameSuggestionText
    {
        get => _clientNameSuggestionText;
        private set => SetProperty(ref _clientNameSuggestionText, value);
    }

    private void InitializeClientNameEditing()
    {
        UseSuggestedClientNameCommand = new RelayCommand(
            _ => UseSuggestedClientName(),
            _ => CanUseSuggestedClientName());
        SaveClientNameCommand = new RelayCommand(
            _ => SaveClientName(),
            _ => CanSaveClientName());
        ExportClientMatchWorkbookCommand = new RelayCommand(
            _ => ExportClientMatchWorkbook(),
            _ => _currentUser.CanManageClients);
    }

    private void ResetClientNameEditor()
    {
        ClientNameEditText = SelectedManagedClient?.Name ?? string.Empty;
        RefreshClientNameSuggestion();
    }

    private void RefreshClientNameSuggestion()
    {
        var client = SelectedManagedClient;
        _suggestedClientName = client is null
            ? null
            : ClientMatchingService.SuggestCanonicalName(
                client,
                SelectedSageMatchCandidate);

        if (client is null)
        {
            ClientNameSuggestionText =
                "Select a client to review or change its TechBench name.";
        }
        else if (client.Source.Equals("WHD", StringComparison.OrdinalIgnoreCase)
                 && SelectedSageMatchCandidate is not null
                 && !string.IsNullOrWhiteSpace(_suggestedClientName))
        {
            ClientNameSuggestionText =
                $"Match the WHD and Sage records first. The likely canonical name is “{_suggestedClientName}”.";
        }
        else if (string.IsNullOrWhiteSpace(_suggestedClientName)
                 || string.Equals(
                     client.Name.Trim(),
                     _suggestedClientName,
                     StringComparison.OrdinalIgnoreCase))
        {
            ClientNameSuggestionText =
                "The TechBench client name agrees with the best available source name.";
        }
        else
        {
            var source = !string.IsNullOrWhiteSpace(client.SageCustomerId)
                ? "matched Sage customer"
                : "WHD location";
            ClientNameSuggestionText =
                $"Suggested from the {source}: “{_suggestedClientName}”. You can use it or enter a different TechBench name.";
        }

        UseSuggestedClientNameCommand.RaiseCanExecuteChanged();
        SaveClientNameCommand.RaiseCanExecuteChanged();
    }

    private bool CanUseSuggestedClientName()
    {
        return _currentUser.CanManageClients
            && SelectedManagedClient is not null
            && !string.IsNullOrWhiteSpace(_suggestedClientName)
            && !string.Equals(
                ClientNameEditText.Trim(),
                _suggestedClientName,
                StringComparison.Ordinal);
    }

    private void UseSuggestedClientName()
    {
        if (!CanUseSuggestedClientName())
        {
            return;
        }

        ClientNameEditText = _suggestedClientName!;
    }

    private bool CanSaveClientName()
    {
        return _currentUser.CanManageClients
            && !IsEntryOperationRunning
            && SelectedManagedClient is { Id: > 0 } client
            && !string.IsNullOrWhiteSpace(ClientNameEditText)
            && ClientNameEditText.Trim().Length <= 240
            && !string.Equals(
                client.Name.Trim(),
                ClientNameEditText.Trim(),
                StringComparison.Ordinal);
    }

    private void SaveClientName()
    {
        if (!CanSaveClientName())
        {
            return;
        }

        var client = SelectedManagedClient!;
        var clientId = client.Id;
        var previousName = client.Name;
        client.Name = ClientNameEditText.Trim();

        try
        {
            _repository.SaveClient(client);
            RefreshClients();
            SelectedManagedClient =
                ManagedClients.FirstOrDefault(candidate => candidate.Id == clientId);
            StatusMessage =
                $"Renamed TechBench client “{previousName}” to “{client.Name}”. WHD and Sage links were preserved.";
        }
        catch (InvalidOperationException ex)
        {
            client.Name = previousName;
            ClientNameEditText = previousName;
            StatusMessage = ex.Message;
            _dialogService.Error("Client name", ex.Message);
        }
        catch (SqlException ex)
        {
            client.Name = previousName;
            ClientNameEditText = previousName;
            StatusMessage = ex.Message;
            _dialogService.Error("Client name", ex.Message);
        }
    }

    private void ExportClientMatchWorkbook()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export client match audit",
            FileName =
                $"TechBench-client-match-audit-{DateTime.Today:yyyy-MM-dd}.xlsx",
            Filter = "Excel workbooks (*.xlsx)|*.xlsx|All files (*.*)|*.*",
            AddExtension = true,
            DefaultExt = ".xlsx"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var clients = _repository.GetClients(includeInactive: true);
            var workbook = ClientMatchExcelExportService.BuildWorkbook(clients);
            File.WriteAllBytes(dialog.FileName, workbook);
            StatusMessage =
                $"Exported {clients.Count} client record(s) to {Path.GetFileName(dialog.FileName)}.";
        }
        catch (IOException ex)
        {
            StatusMessage = ex.Message;
            _dialogService.Error("Client export", ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusMessage = ex.Message;
            _dialogService.Error("Client export", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            StatusMessage = ex.Message;
            _dialogService.Error("Client export", ex.Message);
        }
    }
}
