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
    public ObservableCollection<FireDrillWorkspaceSection> FireDrillWorkspaceSections { get; } = new();
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
        CurrentSection.Equals("Client Info", StringComparison.Ordinal)
        || CurrentFireDrillWorkspaceSection is not null;
    public bool IsClientWifiSection =>
        CurrentFireDrillWorkspaceSection?.GroupName.Equals(
            "Wireless",
            StringComparison.OrdinalIgnoreCase) == true;
    public bool IsDomainAdSection =>
        CurrentFireDrillWorkspaceSection?.GroupName.Equals(
            "Active Directory",
            StringComparison.OrdinalIgnoreCase) == true;
    public bool IsConnectionSection =>
        CurrentFireDrillWorkspaceSection?.GroupName.Equals(
            "WatchGuard",
            StringComparison.OrdinalIgnoreCase) == true;
    public bool IsVeeamSection =>
        CurrentFireDrillWorkspaceSection?.GroupName.Equals(
            "Veeam",
            StringComparison.OrdinalIgnoreCase) == true;
    public bool IsMiscInfoSection =>
        CurrentFireDrillWorkspaceSection?.GroupName.Equals(
            "Other",
            StringComparison.OrdinalIgnoreCase) == true;
    public string CredentialWorkspaceTitle =>
        CurrentFireDrillWorkspaceSection?.DisplayName ?? "FireDrill";
    public string CredentialWorkspaceDescription =>
        CurrentFireDrillWorkspaceSection is { } section
            ? $"Search synchronized {section.DisplayName} information. Values remain hidden until you explicitly reveal a client."
            : "Search all synchronized FireDrill client information. Values remain hidden until you explicitly reveal a client.";
    public string CredentialEmptyText =>
        CurrentFireDrillWorkspaceSection is { } section
            ? $"No matching clients have {section.DisplayName} fields."
            : "No matching FireDrill client information.";
    public string CredentialRevealButtonLabel =>
        CurrentFireDrillWorkspaceSection is { } section
            ? $"Reveal {section.DisplayName}"
            : "Reveal FireDrill";
    public string CredentialSelectionPrompt =>
        CurrentFireDrillWorkspaceSection is { } section
            ? $"Select a client to view its {section.DisplayName} fields."
            : "Select a client to view all synchronized FireDrill information.";

    private FireDrillWorkspaceSection? CurrentFireDrillWorkspaceSection =>
        FireDrillWorkspaceSections.FirstOrDefault(section =>
            section.SectionKey.Equals(
                CurrentSection,
                StringComparison.Ordinal));

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
            equipment = EquipmentDeploymentState.Apply(
                equipment,
                EquipmentDeploymentState.ReadFromSettings(
                    _repository.GetSettings()));
        }
        catch
        {
            // Lifecycle enrichment is best-effort. Equipment details remain
            // available even if shared settings are temporarily unavailable.
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
        var allCredentials =
            _repository.SearchFireDrillCredentials();
        RefreshFireDrillWorkspaceSections(
            allCredentials.SelectMany(item => item.Fields));
        var matchingCredentials =
            string.IsNullOrWhiteSpace(FireDrillSearchText)
                ? allCredentials
                : _repository.SearchFireDrillCredentials(
                    FireDrillSearchText);
        FireDrillCredentials.Clear();
        foreach (var item in matchingCredentials
                     .Where(item => item.Fields.Any(IsFieldVisibleInCurrentCredentialSection)))
            FireDrillCredentials.Add(item);
        SelectedFireDrillCredential = selectedId.HasValue
            ? FireDrillCredentials.FirstOrDefault(item => item.CredentialId == selectedId.Value)
            : null;
        OnPropertyChanged(nameof(HasFireDrillCredentials));
        StatusMessage = $"Showing {FireDrillCredentials.Count} {CredentialWorkspaceTitle} record(s).";
    }

    private void RefreshFireDrillWorkspaceSections(
        IEnumerable<FireDrillCredentialField> fields)
    {
        var sections =
            CredentialFieldGrouper.DiscoverWorkspaceSections(fields);
        FireDrillWorkspaceSections.Clear();
        foreach (var section in sections)
            FireDrillWorkspaceSections.Add(section);

        if (CredentialFieldGrouper.IsWorkspaceSectionKey(CurrentSection)
            && CurrentFireDrillWorkspaceSection is null)
        {
            CurrentSection = "Client Info";
        }

        OnPropertyChanged(nameof(IsCredentialWorkspaceSection));
        OnPropertyChanged(nameof(IsClientWifiSection));
        OnPropertyChanged(nameof(IsDomainAdSection));
        OnPropertyChanged(nameof(IsConnectionSection));
        OnPropertyChanged(nameof(IsVeeamSection));
        OnPropertyChanged(nameof(IsMiscInfoSection));
        OnPropertyChanged(nameof(CredentialWorkspaceTitle));
        OnPropertyChanged(nameof(CredentialWorkspaceDescription));
        OnPropertyChanged(nameof(CredentialEmptyText));
        OnPropertyChanged(nameof(CredentialRevealButtonLabel));
        OnPropertyChanged(nameof(CredentialSelectionPrompt));
        OnPropertyChanged(nameof(WorkspaceHeaderTitle));
        OnPropertyChanged(nameof(WindowTitle));
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
                CredentialFieldGrouper.CreateWirelessSectionGroup(
                    fields.Where(
                        IsFieldVisibleInCurrentCredentialSection));
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
        FireDrillCredentialField field) =>
        CurrentFireDrillWorkspaceSection is not { } section
        || CredentialFieldGrouper.IsFieldInWorkspaceSection(
            field,
            section);

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
