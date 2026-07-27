using System.Collections.ObjectModel;
using TechBench.Models;
using TechBench.Services;

namespace TechBench.ViewModels;

public sealed partial class MainWindowViewModel
{
    private string _clientUserSearchText = string.Empty;
    private ClientUserSummary? _selectedClientUser;
    private ClientUserSummary? _revealedClientUser;
    private bool _isCopyingClientUserField;

    public ObservableCollection<ClientUserSummary> ClientUsers { get; } = new();
    public ObservableCollection<ClientUserAccountGroup> ClientUserAccountGroups { get; } = new();
    public RelayCommand SearchClientUsersCommand { get; private set; } = null!;
    public RelayCommand ClearClientUserSearchCommand { get; private set; } = null!;
    public RelayCommand RevealClientUserCommand { get; private set; } = null!;
    public RelayCommand HideClientUserCommand { get; private set; } = null!;
    public RelayCommand CopyClientUserFieldCommand { get; private set; } = null!;

    public bool CanAccessClientUsers => !_currentUser.IsReadOnlyPreview;
    public bool HasClientUsers => ClientUsers.Count > 0;
    public bool HasSelectedClientUser => SelectedClientUser is not null;
    public bool IsClientUserRevealed => RevealedClientUser is not null;

    public string ClientUserSearchText
    {
        get => _clientUserSearchText;
        set => SetProperty(ref _clientUserSearchText, value);
    }

    public ClientUserSummary? SelectedClientUser
    {
        get => _selectedClientUser;
        set
        {
            if (!SetProperty(ref _selectedClientUser, value)) return;
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
        var selectedId = SelectedClientUser?.ClientUserId;
        ClientUsers.Clear();
        foreach (var user in _repository.SearchClientUsers(
                     searchTerm: ClientUserSearchText))
        {
            ClientUsers.Add(user);
        }

        SelectedClientUser = selectedId.HasValue
            ? ClientUsers.FirstOrDefault(user => user.ClientUserId == selectedId.Value)
            : null;
        OnPropertyChanged(nameof(HasClientUsers));
        StatusMessage = $"Showing {ClientUsers.Count} synchronized client user(s).";
    }

    private void ClearClientUserSearch()
    {
        ClientUserSearchText = string.Empty;
        RefreshClientUsers();
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
