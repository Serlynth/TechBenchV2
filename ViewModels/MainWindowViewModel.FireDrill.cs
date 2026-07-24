using System.Collections.ObjectModel;
using TechBench.Models;
using TechBench.Services;

namespace TechBench.ViewModels;

public sealed partial class MainWindowViewModel
{
    private string _fireDrillSearchText = string.Empty;
    private FireDrillCredentialSummary? _selectedFireDrillCredential;
    private FireDrillCredential? _revealedFireDrillCredential;
    private bool _isCopyingFireDrillCredential;

    public ObservableCollection<FireDrillCredentialSummary> FireDrillCredentials { get; } = new();
    public ObservableCollection<FireDrillCredentialField> FireDrillCredentialFields { get; } = new();
    public ObservableCollection<FireDrillCredentialFieldGroup> FireDrillCredentialGroups { get; } = new();
    public RelayCommand SearchFireDrillCommand { get; private set; } = null!;
    public RelayCommand RevealFireDrillCommand { get; private set; } = null!;
    public RelayCommand CopyFireDrillFieldCommand { get; private set; } = null!;
    public RelayCommand HideFireDrillCommand { get; private set; } = null!;

    public bool CanAccessFireDrill => !_currentUser.IsReadOnlyPreview;
    public bool HasFireDrillCredentials => FireDrillCredentials.Count > 0;
    public bool HasSelectedFireDrillCredential => SelectedFireDrillCredential is not null;
    public bool IsFireDrillCredentialRevealed => RevealedFireDrillCredential is not null;
    public bool IsCredentialWorkspaceSection =>
        CurrentSection.Equals("Client Credentials", StringComparison.Ordinal) ||
        IsClientWifiSection;
    public bool IsClientWifiSection =>
        CurrentSection.Equals("Client WiFi", StringComparison.Ordinal);
    public string CredentialWorkspaceTitle =>
        IsClientWifiSection ? "Client WiFi" : "Client Credentials";
    public string CredentialWorkspaceDescription => IsClientWifiSection
        ? "Search synchronized client WiFi information. Wireless values remain hidden until you explicitly reveal a client."
        : "Search the server-synchronized client credentials. Passwords remain hidden until you explicitly reveal a client.";
    public string CredentialEmptyText => IsClientWifiSection
        ? "No matching clients have Wireless fields."
        : "No matching credential clients.";
    public string CredentialRevealButtonLabel =>
        IsClientWifiSection ? "Reveal WiFi" : "Reveal Credentials";
    public string CredentialSelectionPrompt => IsClientWifiSection
        ? "Select a client to view its WiFi fields."
        : "Select a client to view its credential fields.";

    public string FireDrillSearchText
    {
        get => _fireDrillSearchText;
        set => SetProperty(ref _fireDrillSearchText, value);
    }

    public FireDrillCredentialSummary? SelectedFireDrillCredential
    {
        get => _selectedFireDrillCredential;
        set
        {
            if (SetProperty(ref _selectedFireDrillCredential, value))
            {
                ClearRevealedFireDrillCredential();
                PopulateMaskedFireDrillFields();
                OnPropertyChanged(nameof(HasSelectedFireDrillCredential));
                RevealFireDrillCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public FireDrillCredential? RevealedFireDrillCredential
    {
        get => _revealedFireDrillCredential;
        private set
        {
            if (SetProperty(ref _revealedFireDrillCredential, value))
            {
                OnPropertyChanged(nameof(IsFireDrillCredentialRevealed));
                CopyFireDrillFieldCommand.RaiseCanExecuteChanged();
                HideFireDrillCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private void InitializeFireDrillCredentials()
    {
        SearchFireDrillCommand = new RelayCommand(_ => RefreshFireDrillCredentials());
        RevealFireDrillCommand = new RelayCommand(_ => RevealFireDrillCredential(), _ => SelectedFireDrillCredential is not null && CanAccessFireDrill);
        CopyFireDrillFieldCommand = new RelayCommand(
            CopyFireDrillField,
            _ => RevealedFireDrillCredential is not null && !_isCopyingFireDrillCredential);
        HideFireDrillCommand = new RelayCommand(_ => HideFireDrillCredential(), _ => RevealedFireDrillCredential is not null);
    }

    private void RefreshFireDrillCredentials()
    {
        if (!CanAccessFireDrill) return;
        var selectedId = SelectedFireDrillCredential?.CredentialId;
        FireDrillCredentials.Clear();
        foreach (var item in _repository.SearchFireDrillCredentials(FireDrillSearchText)
                     .Where(item => !IsClientWifiSection ||
                                    item.Fields.Any(CredentialFieldGrouper.IsWirelessField)))
            FireDrillCredentials.Add(item);
        SelectedFireDrillCredential = selectedId.HasValue
            ? FireDrillCredentials.FirstOrDefault(item => item.CredentialId == selectedId.Value)
            : null;
        OnPropertyChanged(nameof(HasFireDrillCredentials));
        StatusMessage = IsClientWifiSection
            ? $"Showing {FireDrillCredentials.Count} client WiFi record(s)."
            : $"Showing {FireDrillCredentials.Count} client credential record(s).";
    }

    private void RevealFireDrillCredential()
    {
        if (SelectedFireDrillCredential is null || !CanAccessFireDrill) return;
        RevealedFireDrillCredential = _repository.RevealFireDrillCredential(SelectedFireDrillCredential.CredentialId)
            ?? throw new InvalidOperationException("The selected credential is no longer available.");
        PopulateRevealedFireDrillFields(RevealedFireDrillCredential);
        StatusMessage = $"Revealed credentials for {RevealedFireDrillCredential.ClientName}.";
    }

    private void PopulateMaskedFireDrillFields()
    {
        PopulateFireDrillFields(
            SelectedFireDrillCredential?.Fields.Select(field =>
                field with { Value = "***" })
            ?? []);
    }

    private void PopulateRevealedFireDrillFields(FireDrillCredential credential)
    {
        PopulateFireDrillFields(credential.Fields);
    }

    private void PopulateFireDrillFields(
        IEnumerable<FireDrillCredentialField> fields)
    {
        FireDrillCredentialFields.Clear();
        FireDrillCredentialGroups.Clear();

        var visibleFields = IsClientWifiSection
            ? fields.Where(CredentialFieldGrouper.IsWirelessField)
            : fields;

        foreach (var field in visibleFields)
            FireDrillCredentialFields.Add(field);

        foreach (var group in CredentialFieldGrouper.Group(FireDrillCredentialFields))
            FireDrillCredentialGroups.Add(group);
    }

    private async void CopyFireDrillField(object? parameter)
    {
        if (RevealedFireDrillCredential is null ||
            parameter is not string field ||
            string.IsNullOrWhiteSpace(field))
        {
            StatusMessage = "Select and reveal a credential field before copying it.";
            return;
        }
        var selectedField = RevealedFireDrillCredential.Fields.FirstOrDefault(candidate =>
            string.Equals(candidate.FieldName, field, StringComparison.OrdinalIgnoreCase));
        if (selectedField is null)
        {
            StatusMessage = "That credential field is no longer available. Hide and reveal the credentials, then try again.";
            return;
        }
        var value = selectedField.Value;
        if (string.IsNullOrEmpty(value))
        {
            StatusMessage = "That credential field is blank.";
            return;
        }
        var clientName = RevealedFireDrillCredential.ClientName;
        _isCopyingFireDrillCredential = true;
        CopyFireDrillFieldCommand.RaiseCanExecuteChanged();
        StatusMessage = $"Copying {selectedField.Label}...";
        try
        {
            if (!await ClipboardService.TrySetTextAsync(value))
            {
                StatusMessage = "Windows could not access the clipboard. Close any clipboard manager and try Copy again.";
                return;
            }
            StatusMessage = $"Copied {selectedField.Label} for {clientName}.";
        }
        catch (Exception)
        {
            StatusMessage = "Windows could not access the clipboard. Try Copy again.";
        }
        finally
        {
            _isCopyingFireDrillCredential = false;
            CopyFireDrillFieldCommand.RaiseCanExecuteChanged();
        }
    }

    private void ClearRevealedFireDrillCredential()
    {
        FireDrillCredentialFields.Clear();
        FireDrillCredentialGroups.Clear();
        RevealedFireDrillCredential = null;
    }

    private void HideFireDrillCredential()
    {
        RevealedFireDrillCredential = null;
        PopulateMaskedFireDrillFields();
        if (SelectedFireDrillCredential is not null)
            StatusMessage = $"Hid credentials for {SelectedFireDrillCredential.ClientName}.";
    }
}
