using System.Collections.ObjectModel;
using TechBench.Models;
using TechBench.Services;

namespace TechBench.ViewModels;

public sealed partial class MainWindowViewModel
{
    private string _clientUserSearchText = string.Empty;
    private IReadOnlyList<ClientUserSummary> _allClientUsers = [];
    private ClientUserClientSummary? _selectedClientUserClient;
    private ClientUserSummary? _selectedClientUser;
    private ClientUserSummary? _revealedClientUser;
    private bool _isCopyingClientUserField;

    public ObservableCollection<ClientUserClientSummary> ClientUserClients { get; } = new();
    public ObservableCollection<ClientUserSummary> ClientUsers { get; } = new();
    public ObservableCollection<EquipmentItem> SelectedClientUserEquipment { get; } = new();
    public ObservableCollection<ClientUserAccountGroup> ClientUserAccountGroups { get; } = new();
    public RelayCommand SearchClientUsersCommand { get; private set; } = null!;
    public RelayCommand ClearClientUserSearchCommand { get; private set; } = null!;
    public RelayCommand CloseClientUserDetailsCommand { get; private set; } = null!;
    public RelayCommand RevealClientUserCommand { get; private set; } = null!;
    public RelayCommand HideClientUserCommand { get; private set; } = null!;
    public RelayCommand CopyClientUserFieldCommand { get; private set; } = null!;

    public bool CanAccessClientUsers => !_currentUser.IsReadOnlyPreview;
    public bool HasClientUserClients => ClientUserClients.Count > 0;
    public bool HasClientUsers => ClientUsers.Count > 0;
    public bool HasSelectedClientUserClient => SelectedClientUserClient is not null;
    public bool HasSelectedClientUser => SelectedClientUser is not null;
    public bool HasSelectedClientUserEquipment => SelectedClientUserEquipment.Count > 0;
    public bool IsClientUserRevealed => RevealedClientUser is not null;
    public string SelectedClientUserEquipmentLabel =>
        $"{SelectedClientUserEquipment.Count} assigned device{(SelectedClientUserEquipment.Count == 1 ? string.Empty : "s")}";

    public string ClientUserSearchText
    {
        get => _clientUserSearchText;
        set => SetProperty(ref _clientUserSearchText, value);
    }

    public ClientUserClientSummary? SelectedClientUserClient
    {
        get => _selectedClientUserClient;
        set
        {
            if (!SetProperty(ref _selectedClientUserClient, value)) return;
            PopulateClientUsers(value?.ClientId);
            OnPropertyChanged(nameof(HasSelectedClientUserClient));
        }
    }

    public ClientUserSummary? SelectedClientUser
    {
        get => _selectedClientUser;
        set
        {
            if (!SetProperty(ref _selectedClientUser, value)) return;
            RevealedClientUser = null;
            PopulateClientUserGroups(value?.Accounts, masked: true);
            PopulateSelectedClientUserEquipment(value?.ClientUserId);
            OnPropertyChanged(nameof(HasSelectedClientUser));
            CloseClientUserDetailsCommand.RaiseCanExecuteChanged();
            RevealClientUserCommand.RaiseCanExecuteChanged();
        }
    }

    public ClientUserSummary? RevealedClientUser
    {
        get => _revealedClientUser;
        private set
        {
            if (!SetProperty(ref _revealedClientUser, value)) return;
            OnPropertyChanged(nameof(IsClientUserRevealed));
            HideClientUserCommand.RaiseCanExecuteChanged();
            CopyClientUserFieldCommand.RaiseCanExecuteChanged();
        }
    }

    private void InitializeClientUsers()
    {
        SearchClientUsersCommand = new RelayCommand(_ => RefreshClientUsers());
        ClearClientUserSearchCommand = new RelayCommand(_ => ClearClientUserSearch());
        CloseClientUserDetailsCommand = new RelayCommand(
            _ => CloseClientUserDetails(),
            _ => SelectedClientUser is not null);
        RevealClientUserCommand = new RelayCommand(
            _ => RevealClientUser(),
            _ => SelectedClientUser is not null && CanAccessClientUsers);
        HideClientUserCommand = new RelayCommand(
            _ => HideClientUser(),
            _ => RevealedClientUser is not null);
        CopyClientUserFieldCommand = new RelayCommand(
            CopyClientUserField,
            _ => RevealedClientUser is not null && !_isCopyingClientUserField);
    }

    private void RefreshClientUsers()
    {
        if (!CanAccessClientUsers) return;
        var selectedClientId = SelectedClientUserClient?.ClientId;
        var search = ClientUserSearchText.Trim();
        SelectedClientUserClient = null;
        _allClientUsers = _repository.SearchClientUsers();
        var matchingClientIds = string.IsNullOrWhiteSpace(search)
            ? _allClientUsers.Select(user => user.ClientId).ToHashSet()
            : _repository.SearchClientUsers(searchTerm: search)
                .Select(user => user.ClientId)
                .ToHashSet();

        ClientUserClients.Clear();
        foreach (var client in _allClientUsers
                     .Where(user => matchingClientIds.Contains(user.ClientId))
                     .GroupBy(user => new { user.ClientId, user.ClientName })
                     .Select(group => new ClientUserClientSummary(
                         group.Key.ClientId,
                         group.Key.ClientName,
                         group.Count()))
                     .OrderBy(client => client.ClientName, StringComparer.OrdinalIgnoreCase))
        {
            ClientUserClients.Add(client);
        }

        SelectedClientUserClient = selectedClientId.HasValue
            ? ClientUserClients.FirstOrDefault(client => client.ClientId == selectedClientId.Value)
              ?? ClientUserClients.FirstOrDefault()
            : ClientUserClients.FirstOrDefault();
        OnPropertyChanged(nameof(HasClientUserClients));
        StatusMessage =
            $"Showing {ClientUserClients.Count} client(s) with synchronized users.";
    }

    private void ClearClientUserSearch()
    {
        ClientUserSearchText = string.Empty;
        RefreshClientUsers();
    }

    private void PopulateClientUsers(int? clientId)
    {
        SelectedClientUser = null;
        ClientUsers.Clear();
        if (clientId.HasValue)
        {
            foreach (var user in _allClientUsers
                         .Where(user => user.ClientId == clientId.Value)
                         .OrderBy(user => user.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                ClientUsers.Add(user);
            }
        }

        OnPropertyChanged(nameof(HasClientUsers));
    }

    private void CloseClientUserDetails()
    {
        SelectedClientUser = null;
        ClearRevealedClientUser();
    }

    private void RevealClientUser()
    {
        if (SelectedClientUser is null || !CanAccessClientUsers) return;
        RevealedClientUser = _repository.RevealClientUser(
                                 SelectedClientUser.ClientUserId)
                             ?? throw new InvalidOperationException(
                                 "The selected client user is no longer available.");
        PopulateClientUserGroups(RevealedClientUser.Accounts, masked: false);
        StatusMessage =
            $"Revealed account details for {RevealedClientUser.DisplayName}.";
    }

    private void HideClientUser()
    {
        RevealedClientUser = null;
        PopulateClientUserGroups(SelectedClientUser?.Accounts, masked: true);
        if (SelectedClientUser is not null)
            StatusMessage = $"Hid account details for {SelectedClientUser.DisplayName}.";
    }

    private void ClearRevealedClientUser()
    {
        RevealedClientUser = null;
        ClientUserAccountGroups.Clear();
        SelectedClientUserEquipment.Clear();
        OnPropertyChanged(nameof(HasSelectedClientUserEquipment));
        OnPropertyChanged(nameof(SelectedClientUserEquipmentLabel));
    }

    private void PopulateSelectedClientUserEquipment(long? clientUserId)
    {
        SelectedClientUserEquipment.Clear();
        if (clientUserId is > 0)
        {
            try
            {
                foreach (var equipment in _repository
                             .GetEquipmentInventory(clientUserId: clientUserId)
                             .OrderBy(item => item.DeviceType, StringComparer.OrdinalIgnoreCase)
                             .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
                {
                    SelectedClientUserEquipment.Add(equipment);
                }
            }
            catch
            {
                // Inventory enrichment is best-effort so an older database
                // installer cannot prevent Client Users from opening.
            }
        }

        OnPropertyChanged(nameof(HasSelectedClientUserEquipment));
        OnPropertyChanged(nameof(SelectedClientUserEquipmentLabel));
    }

    private void PopulateClientUserGroups(
        IEnumerable<ClientUserAccountGroup>? groups,
        bool masked)
    {
        ClientUserAccountGroups.Clear();
        if (groups is null) return;
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

    private async void CopyClientUserField(object? parameter)
    {
        if (RevealedClientUser is null
            || parameter is not string fieldName
            || string.IsNullOrWhiteSpace(fieldName))
        {
            StatusMessage = "Select and reveal a client user field before copying it.";
            return;
        }

        var field = RevealedClientUser.Accounts
            .SelectMany(group => group.Fields)
            .FirstOrDefault(candidate => candidate.FieldName.Equals(
                fieldName,
                StringComparison.OrdinalIgnoreCase));
        if (field is null || string.IsNullOrEmpty(field.Value))
        {
            StatusMessage = "That client user field is blank or no longer available.";
            return;
        }

        _isCopyingClientUserField = true;
        CopyClientUserFieldCommand.RaiseCanExecuteChanged();
        try
        {
            StatusMessage = $"Copying {field.Label}...";
            StatusMessage = await ClipboardService.TrySetTextAsync(field.Value)
                ? $"Copied {field.Label} for {RevealedClientUser.DisplayName}."
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
}
