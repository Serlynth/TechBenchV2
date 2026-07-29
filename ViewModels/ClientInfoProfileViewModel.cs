using System.Collections.ObjectModel;
using TechBench.Models;
using TechBench.Services;

namespace TechBench.ViewModels;

internal sealed class ClientInfoProfileViewModel : ObservableObject
{
    private readonly FireDrillCredentialSummary _summary;
    private readonly Func<long, FireDrillCredential?> _revealCredential;
    private readonly Func<long, ClientUserSummary?> _revealClientUser;
    private bool _isRevealed;
    private bool _isCopying;
    private bool _isCopyingClientUserField;
    private EquipmentItem? _selectedEquipment;
    private ClientUserSummary? _selectedClientUser;
    private ClientUserSummary? _revealedClientUser;
    private string _statusMessage;

    public ClientInfoProfileViewModel(
        FireDrillCredentialSummary summary,
        Func<long, FireDrillCredential?> revealCredential,
        Client? whdClient = null,
        IReadOnlyList<EquipmentItem>? equipment = null,
        IReadOnlyList<ClientUserSummary>? clientUsers = null,
        Func<long, ClientUserSummary?>? revealClientUser = null)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(revealCredential);
        _summary = summary;
        _revealCredential = revealCredential;
        _revealClientUser = revealClientUser ?? (_ => null);
        WhdClient = whdClient;
        _statusMessage = "Values are hidden. Click Reveal All to view this client's complete information.";
        RevealCommand = new RelayCommand(_ => Reveal(), _ => !IsRevealed);
        HideCommand = new RelayCommand(_ => Hide(), _ => IsRevealed);
        CopyFieldCommand = new RelayCommand(
            CopyField,
            parameter => IsRevealed && !_isCopying && parameter is FireDrillCredentialField);
        RevealClientUserCommand = new RelayCommand(
            _ => RevealClientUser(),
            _ => SelectedClientUser is not null && !IsClientUserRevealed);
        HideClientUserCommand = new RelayCommand(
            _ => HideClientUser(),
            _ => IsClientUserRevealed);
        CopyClientUserFieldCommand = new RelayCommand(
            CopyClientUserField,
            parameter => IsClientUserRevealed
                         && !_isCopyingClientUserField
                         && parameter is FireDrillCredentialField);
        OpenEquipmentDetailsCommand = new RelayCommand(
            OpenEquipmentDetails,
            parameter => parameter is EquipmentItem);
        CloseEquipmentDetailsCommand = new RelayCommand(
            _ => CloseEquipmentDetails(),
            _ => SelectedEquipment is not null);
        LaunchAnyDeskCommand = new RelayCommand(
            LaunchAnyDesk,
            parameter => parameter is EquipmentItem equipment
                         && !string.IsNullOrWhiteSpace(
                             equipment.AnyDeskNumber));
        foreach (var item in equipment ?? [])
            Equipment.Add(item);
        foreach (var user in clientUsers ?? [])
            ClientUsers.Add(user);
        PopulateGroups(Mask(summary.Fields));
    }

    public ObservableCollection<FireDrillCredentialFieldGroup> CredentialGroups { get; } = new();
    public ObservableCollection<EquipmentItem> Equipment { get; } = new();
    public ObservableCollection<ClientUserSummary> ClientUsers { get; } = new();
    public ObservableCollection<ClientUserAccountGroup> ClientUserAccountGroups { get; } = new();
    public RelayCommand RevealCommand { get; }
    public RelayCommand HideCommand { get; }
    public RelayCommand CopyFieldCommand { get; }
    public RelayCommand RevealClientUserCommand { get; }
    public RelayCommand HideClientUserCommand { get; }
    public RelayCommand CopyClientUserFieldCommand { get; }
    public RelayCommand OpenEquipmentDetailsCommand { get; }
    public RelayCommand CloseEquipmentDetailsCommand { get; }
    public RelayCommand LaunchAnyDeskCommand { get; }
    public string ClientName => _summary.ClientName;
    public Client? WhdClient { get; }
    public bool HasWhdMatch => WhdClient is not null;
    public bool HasWhdContactName => !string.IsNullOrWhiteSpace(WhdClient?.WhdContactName);
    public bool HasWhdContactEmail => !string.IsNullOrWhiteSpace(WhdClient?.WhdContactEmail);
    public bool HasWhdPhone => !string.IsNullOrWhiteSpace(WhdClient?.WhdPhone);
    public bool HasWhdAddress => !string.IsNullOrWhiteSpace(WhdClient?.WhdAddress);
    public string WhdMatchLabel => HasWhdMatch
        ? $"WHD · {WhdClient!.WhdLocationName ?? WhdClient.Name}"
        : "No confident WHD client match";
    public string WhdContactName => WhdClient?.WhdContactName ?? string.Empty;
    public string WhdContactEmail => WhdClient?.WhdContactEmail ?? string.Empty;
    public string WhdPhone => WhdClient?.WhdPhone ?? string.Empty;
    public string WhdAddress => WhdClient?.WhdAddress ?? string.Empty;
    public string WindowTitle => $"{ClientName} - Client Info";
    public string ClientInitials => BuildInitials(ClientName);
    public string FieldCountLabel => $"{_summary.Fields.Count} synchronized field{(_summary.Fields.Count == 1 ? string.Empty : "s")}";
    public string GroupCountLabel => $"{CredentialGroups.Count} categor{(CredentialGroups.Count == 1 ? "y" : "ies")}";
    public string LastSyncedLabel => _summary.LastSyncedAtUtc == DateTime.MinValue
        ? "Not yet synchronized"
        : $"Synchronized {_summary.LastSyncedAtUtc.ToLocalTime():g}";
    public string VisibilityLabel => IsRevealed ? "Values revealed" : "Values protected";
    public bool HasFields => CredentialGroups.Count > 0;
    public bool HasEquipment => Equipment.Count > 0;
    public bool HasClientUsers => ClientUsers.Count > 0;
    public bool HasSelectedClientUser => SelectedClientUser is not null;
    public bool IsClientUserRevealed => RevealedClientUser is not null;
    public bool IsEquipmentDetailsVisible => SelectedEquipment is not null;
    public bool HasProfileContent => HasFields || HasEquipment || HasClientUsers;
    public string EquipmentCountLabel =>
        $"{Equipment.Count} inventory item{(Equipment.Count == 1 ? string.Empty : "s")}";
    public string ClientUserCountLabel =>
        $"{ClientUsers.Count} synchronized user{(ClientUsers.Count == 1 ? string.Empty : "s")}";

    public EquipmentItem? SelectedEquipment
    {
        get => _selectedEquipment;
        private set
        {
            if (!SetProperty(ref _selectedEquipment, value))
                return;

            OnPropertyChanged(nameof(IsEquipmentDetailsVisible));
            CloseEquipmentDetailsCommand.RaiseCanExecuteChanged();
        }
    }

    public ClientUserSummary? SelectedClientUser
    {
        get => _selectedClientUser;
        set
        {
            if (!SetProperty(ref _selectedClientUser, value))
                return;

            RevealedClientUser = null;
            PopulateClientUserGroups(value?.Accounts, masked: true);
            OnPropertyChanged(nameof(HasSelectedClientUser));
            RevealClientUserCommand.RaiseCanExecuteChanged();
        }
    }

    public ClientUserSummary? RevealedClientUser
    {
        get => _revealedClientUser;
        private set
        {
            if (!SetProperty(ref _revealedClientUser, value))
                return;

            OnPropertyChanged(nameof(IsClientUserRevealed));
            RevealClientUserCommand.RaiseCanExecuteChanged();
            HideClientUserCommand.RaiseCanExecuteChanged();
            CopyClientUserFieldCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsRevealed
    {
        get => _isRevealed;
        private set
        {
            if (!SetProperty(ref _isRevealed, value))
                return;

            OnPropertyChanged(nameof(VisibilityLabel));
            RevealCommand.RaiseCanExecuteChanged();
            HideCommand.RaiseCanExecuteChanged();
            CopyFieldCommand.RaiseCanExecuteChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    private void OpenEquipmentDetails(object? parameter)
    {
        if (parameter is not EquipmentItem equipment)
            return;

        SelectedEquipment = equipment;
        StatusMessage =
            $"Showing {equipment.Name} inside the {ClientName} client profile.";
    }

    private void CloseEquipmentDetails()
    {
        var equipmentName = SelectedEquipment?.Name;
        SelectedEquipment = null;
        if (!string.IsNullOrWhiteSpace(equipmentName))
        {
            StatusMessage =
                $"Closed {equipmentName} details. The {ClientName} profile remains open.";
        }
    }

    private void LaunchAnyDesk(object? parameter)
    {
        if (parameter is not EquipmentItem equipment)
            return;

        var result = AnyDeskLauncher.Launch(
            equipment.AnyDeskNumber,
            equipment.AnyDeskPassword);
        if (!result.Succeeded)
        {
            StatusMessage =
                $"AnyDesk launch failed: {result.ErrorMessage ?? "AnyDesk could not be started."}";
            return;
        }

        StatusMessage = result.PasswordSubmitted
            ? $"Opening AnyDesk for {equipment.Name} and submitting the unattended-access password."
            : $"Opening AnyDesk for {equipment.Name}. No unattended-access password was stored.";
    }

    private void Reveal()
    {
        try
        {
            var credential = _revealCredential(_summary.CredentialId);
            if (credential is null)
            {
                StatusMessage = "This client is no longer available. Close the profile and refresh Client Info.";
                return;
            }

            PopulateGroups(credential.Fields);
            IsRevealed = true;
            StatusMessage = $"Showing the complete synchronized profile for {ClientName}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Client information could not be revealed: {ex.Message}";
        }
    }

    private void Hide()
    {
        PopulateGroups(Mask(_summary.Fields));
        IsRevealed = false;
        StatusMessage = $"Protected values for {ClientName} are hidden.";
    }

    private void PopulateGroups(IEnumerable<FireDrillCredentialField> fields)
    {
        CredentialGroups.Clear();
        foreach (var group in CredentialFieldGrouper.Group(fields))
            CredentialGroups.Add(group);
        OnPropertyChanged(nameof(GroupCountLabel));
        OnPropertyChanged(nameof(HasFields));
        OnPropertyChanged(nameof(HasProfileContent));
    }

    private void RevealClientUser()
    {
        if (SelectedClientUser is null)
            return;

        try
        {
            var revealed = _revealClientUser(SelectedClientUser.ClientUserId);
            if (revealed is null)
            {
                StatusMessage =
                    "This user is no longer available. Close the profile and refresh Client Info.";
                return;
            }

            RevealedClientUser = revealed;
            PopulateClientUserGroups(revealed.Accounts, masked: false);
            StatusMessage = $"Showing synchronized account details for {revealed.DisplayName}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Client user account details could not be revealed: {ex.Message}";
        }
    }

    private void HideClientUser()
    {
        RevealedClientUser = null;
        PopulateClientUserGroups(SelectedClientUser?.Accounts, masked: true);
        if (SelectedClientUser is not null)
            StatusMessage = $"Protected account values for {SelectedClientUser.DisplayName} are hidden.";
    }

    private void PopulateClientUserGroups(
        IEnumerable<ClientUserAccountGroup>? groups,
        bool masked)
    {
        ClientUserAccountGroups.Clear();
        if (groups is null)
            return;

        foreach (var group in groups)
        {
            ClientUserAccountGroups.Add(masked
                ? group with
                {
                    Fields = group.Fields
                        .Select(field => field with { Value = "***" })
                        .ToArray()
                }
                : group);
        }
    }

    private async void CopyField(object? parameter)
    {
        if (!IsRevealed || parameter is not FireDrillCredentialField field)
            return;
        if (string.IsNullOrEmpty(field.Value))
        {
            StatusMessage = $"{field.Label} is blank.";
            return;
        }

        _isCopying = true;
        CopyFieldCommand.RaiseCanExecuteChanged();
        StatusMessage = $"Copying {field.Label}...";
        try
        {
            StatusMessage = await ClipboardService.TrySetTextAsync(field.Value)
                ? $"Copied {field.Label} for {ClientName}."
                : "Windows could not access the clipboard. Try Copy again.";
        }
        catch (Exception)
        {
            StatusMessage = "Windows could not access the clipboard. Try Copy again.";
        }
        finally
        {
            _isCopying = false;
            CopyFieldCommand.RaiseCanExecuteChanged();
        }
    }

    private async void CopyClientUserField(object? parameter)
    {
        if (!IsClientUserRevealed
            || parameter is not FireDrillCredentialField field
            || string.IsNullOrEmpty(field.Value))
        {
            StatusMessage = "Reveal a non-blank user account field before copying it.";
            return;
        }

        _isCopyingClientUserField = true;
        CopyClientUserFieldCommand.RaiseCanExecuteChanged();
        StatusMessage = $"Copying {field.Label}...";
        try
        {
            StatusMessage = await ClipboardService.TrySetTextAsync(field.Value)
                ? $"Copied {field.Label} for {SelectedClientUser?.DisplayName}."
                : "Windows could not access the clipboard. Try Copy again.";
        }
        catch
        {
            StatusMessage = "Windows could not access the clipboard. Try Copy again.";
        }
        finally
        {
            _isCopyingClientUserField = false;
            CopyClientUserFieldCommand.RaiseCanExecuteChanged();
        }
    }

    private static IEnumerable<FireDrillCredentialField> Mask(
        IEnumerable<FireDrillCredentialField> fields) =>
        fields.Select(field => field with { Value = "***" });

    private static string BuildInitials(string clientName)
    {
        var parts = clientName
            .Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return "CI";
        return string.Concat(parts.Take(2).Select(part => char.ToUpperInvariant(part[0])));
    }
}
