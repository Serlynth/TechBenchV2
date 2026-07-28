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
    public RelayCommand ClearFireDrillSearchCommand { get; private set; } = null!;
    public RelayCommand RevealFireDrillCommand { get; private set; } = null!;
    public RelayCommand CopyFireDrillFieldCommand { get; private set; } = null!;
    public RelayCommand HideFireDrillCommand { get; private set; } = null!;

    public bool CanAccessFireDrill => !_currentUser.IsReadOnlyPreview;
    public bool HasFireDrillCredentials => FireDrillCredentials.Count > 0;
    public bool HasSelectedFireDrillCredential => SelectedFireDrillCredential is not null;
    public bool IsFireDrillCredentialRevealed => RevealedFireDrillCredential is not null;
    public bool IsCredentialWorkspaceSection =>
        CurrentSection.Equals("Client Info", StringComparison.Ordinal) ||
        IsClientWifiSection ||
        IsDomainAdSection ||
        IsConnectionSection ||
        IsMiscInfoSection;
    public bool IsClientWifiSection =>
        CurrentSection.Equals("Client WiFi", StringComparison.Ordinal);
    public bool IsDomainAdSection =>
        CurrentSection.Equals("Domain/AD", StringComparison.Ordinal);
    public bool IsConnectionSection =>
        CurrentSection.Equals("Connection", StringComparison.Ordinal);
    public bool IsMiscInfoSection =>
        CurrentSection.Equals("Misc Info", StringComparison.Ordinal);
    public string CredentialWorkspaceTitle => CurrentSection;
    public string CredentialWorkspaceDescription => CurrentSection switch
    {
        "Client WiFi" => "Search synchronized client WiFi information. WiFi values remain hidden until you explicitly reveal a client.",
        "Domain/AD" => "Search synchronized local domain and Active Directory information. Values remain hidden until you explicitly reveal a client.",
        "Connection" => "Search synchronized WatchGuard connection information. Values remain hidden until you explicitly reveal a client.",
        "Misc Info" => "Search synchronized client information that is not WiFi, Domain/AD, or WatchGuard connection data. Values remain hidden until you explicitly reveal a client.",
        _ => "Search all synchronized client information. Values remain hidden until you explicitly reveal a client."
    };
    public string CredentialEmptyText => CurrentSection switch
    {
        "Client WiFi" => "No matching clients have WiFi fields.",
        "Domain/AD" => "No matching clients have Domain/AD fields.",
        "Connection" => "No matching clients have WatchGuard connection fields.",
        "Misc Info" => "No matching clients have miscellaneous information.",
        _ => "No matching client information."
    };
    public string CredentialRevealButtonLabel => CurrentSection switch
    {
        "Client WiFi" => "Reveal WiFi",
        "Domain/AD" => "Reveal Domain/AD",
        "Connection" => "Reveal Connection",
        "Misc Info" => "Reveal Misc Info",
        _ => "Reveal Client Info"
    };
    public string CredentialSelectionPrompt => CurrentSection switch
    {
        "Client WiFi" => "Select a client to view its WiFi fields.",
        "Domain/AD" => "Select a client to view its local domain and Active Directory fields.",
        "Connection" => "Select a client to view its WatchGuard connection fields.",
        "Misc Info" => "Select a client to view its miscellaneous information.",
        _ => "Select a client to view all synchronized information."
    };

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
        ClearFireDrillSearchCommand = new RelayCommand(_ => ClearFireDrillSearch());
        RevealFireDrillCommand = new RelayCommand(_ => RevealFireDrillCredential(), _ => SelectedFireDrillCredential is not null && CanAccessFireDrill);
        CopyFireDrillFieldCommand = new RelayCommand(
            CopyFireDrillField,
            _ => RevealedFireDrillCredential is not null && !_isCopyingFireDrillCredential);
        HideFireDrillCommand = new RelayCommand(_ => HideFireDrillCredential(), _ => RevealedFireDrillCredential is not null);
    }

    internal ClientInfoProfileViewModel CreateClientInfoProfile(
        FireDrillCredentialSummary summary)
    {
        Client? whdMatch = null;
        IReadOnlyList<EquipmentItem> equipment = [];
        IReadOnlyList<ClientUserSummary> clientUsers = [];
        try
        {
            whdMatch = ClientProfileWhdMatcher.FindConfidentMatch(
                summary.ClientName,
                _repository.GetClients());
        }
        catch
        {
            // Contact enrichment is best-effort. A transient client-list read
            // must never prevent the synchronized profile from opening.
        }

        try
        {
            if (whdMatch is not null)
            {
                equipment = _repository.GetEquipmentInventory(clientId: whdMatch.Id);
            }

            if (equipment.Count == 0)
            {
                equipment = _repository.GetEquipmentInventory(clientName: summary.ClientName);
            }
        }
        catch
        {
            // Inventory enrichment is best-effort. The profile remains useful
            // while the matching SQL installer is being applied.
            equipment = [];
        }

        try
        {
            clientUsers = whdMatch is not null
                ? _repository.SearchClientUsers(clientId: whdMatch.Id)
                : _repository.SearchClientUsers(searchTerm: summary.ClientName)
                    .Where(user => string.Equals(
                        user.ClientName.Trim(),
                        summary.ClientName.Trim(),
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
        }
        catch
        {
            // User enrichment is best-effort. The rest of the synchronized
            // client profile still opens if an older installer is present.
            clientUsers = [];
        }

        return new ClientInfoProfileViewModel(
            summary,
            _repository.RevealFireDrillCredential,
            whdMatch,
            equipment,
            clientUsers,
            _repository.RevealClientUser);
    }

    private void RefreshFireDrillCredentials()
    {
        if (!CanAccessFireDrill) return;
        var selectedId = SelectedFireDrillCredential?.CredentialId;
        FireDrillCredentials.Clear();
        foreach (var item in _repository.SearchFireDrillCredentials(FireDrillSearchText)
                     .Where(item => item.Fields.Any(IsFieldVisibleInCurrentCredentialSection)))
            FireDrillCredentials.Add(item);
        SelectedFireDrillCredential = selectedId.HasValue
            ? FireDrillCredentials.FirstOrDefault(item => item.CredentialId == selectedId.Value)
            : null;
        OnPropertyChanged(nameof(HasFireDrillCredentials));
        StatusMessage = $"Showing {FireDrillCredentials.Count} {CredentialWorkspaceTitle} record(s).";
    }

    private void ClearFireDrillSearch()
    {
        FireDrillSearchText = string.Empty;
        RefreshFireDrillCredentials();
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

        if (IsClientWifiSection)
        {
            var wirelessGroup =
                CredentialFieldGrouper.CreateWirelessSectionGroup(fields);
            if (wirelessGroup is null)
                return;

            foreach (var field in wirelessGroup.Fields)
                FireDrillCredentialFields.Add(field);
            FireDrillCredentialGroups.Add(wirelessGroup);
            return;
        }

        var visibleFields = fields.Where(IsFieldVisibleInCurrentCredentialSection);
        foreach (var field in visibleFields)
            FireDrillCredentialFields.Add(field);

        foreach (var group in CredentialFieldGrouper.Group(FireDrillCredentialFields))
            FireDrillCredentialGroups.Add(group);
    }

    private bool IsFieldVisibleInCurrentCredentialSection(
        FireDrillCredentialField field) => CurrentSection switch
    {
        "Client WiFi" => CredentialFieldGrouper.IsWirelessField(field),
        "Domain/AD" => CredentialFieldGrouper.IsDomainOrAdField(field),
        "Connection" => CredentialFieldGrouper.IsConnectionField(field),
        "Misc Info" => CredentialFieldGrouper.IsMiscInfoField(field),
        _ => true
    };

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
