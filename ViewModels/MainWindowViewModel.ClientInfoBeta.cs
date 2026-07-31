using System.Collections.ObjectModel;
using TechBench.Models;

namespace TechBench.ViewModels;

public sealed partial class MainWindowViewModel
{
    public const string ClientInfoWorkspaceSection = "Client Database";
    public const string ClientInfoImportWorkspaceSection = "Workbook Imports";

    private string _clientInfoSearchText = string.Empty;
    private ClientInfoClientSummary? _selectedClientInfoClient;

    public ObservableCollection<ClientInfoClientSummary> ClientInfoClients { get; } = [];

    public RelayCommand SearchClientInfoClientsCommand { get; private set; } = null!;
    public RelayCommand ClearClientInfoSearchCommand { get; private set; } = null!;

#if TECHBENCH_CLIENT_INFO_BETA
    public bool IsClientInfoBetaBuild => true;
#else
    public bool IsClientInfoBetaBuild => false;
#endif

    public bool ClientInfoWorkspaceAvailable =>
        IsClientInfoBetaBuild && _repository.ClientInfoBetaAvailable;

    public bool HasClientInfoClients => ClientInfoClients.Count > 0;

    public bool IsClientInfoResultsEmpty =>
        ClientInfoWorkspaceAvailable && !HasClientInfoClients;

    public string ClientInfoWorkspaceDescription =>
        "Search client information stored in TechBench SQL. Open a client to view "
        + "or edit its contacts, equipment, infrastructure, network, applications, security, passwords, and other information.";

    public string ClientInfoImportWorkspaceDescription =>
        "Choose a client, create its prefilled migration workbook, copy in the "
        + "cleaned information, then import and review it before it becomes Client Information.";

    public string ClientInfoEmptyText => ClientInfoWorkspaceAvailable
        ? "No clients match this search."
        : "The Client Info SQL extension is not available. Apply the current TechBench SQL package and restart the beta client.";

    public string ClientInfoSearchText
    {
        get => _clientInfoSearchText;
        set => SetProperty(ref _clientInfoSearchText, value);
    }

    public ClientInfoClientSummary? SelectedClientInfoClient
    {
        get => _selectedClientInfoClient;
        set
        {
            if (SetProperty(ref _selectedClientInfoClient, value))
            {
                OnPropertyChanged(nameof(HasSelectedClientInfoClient));
            }
        }
    }

    public bool HasSelectedClientInfoClient => SelectedClientInfoClient is not null;

    private void InitializeClientInfoBeta()
    {
        SearchClientInfoClientsCommand = new RelayCommand(
            _ => RefreshClientInfoClients());
        ClearClientInfoSearchCommand = new RelayCommand(
            _ => ClearClientInfoSearch());
    }

    private void RefreshClientInfoClients()
    {
        var selectedClientId = SelectedClientInfoClient?.ClientId;
        ClientInfoClients.Clear();

        if (!ClientInfoWorkspaceAvailable)
        {
            SelectedClientInfoClient = null;
            OnPropertyChanged(nameof(HasClientInfoClients));
            OnPropertyChanged(nameof(IsClientInfoResultsEmpty));
            OnPropertyChanged(nameof(ClientInfoEmptyText));
            StatusMessage = ClientInfoEmptyText;
            return;
        }

        try
        {
            foreach (var client in _repository.SearchClientInfoClients(
                         ClientInfoSearchText))
            {
                ClientInfoClients.Add(client);
            }

            SelectedClientInfoClient = selectedClientId.HasValue
                ? ClientInfoClients.FirstOrDefault(client =>
                    client.ClientId == selectedClientId.Value)
                : null;
            OnPropertyChanged(nameof(HasClientInfoClients));
            OnPropertyChanged(nameof(IsClientInfoResultsEmpty));
            OnPropertyChanged(nameof(ClientInfoEmptyText));
            StatusMessage = CurrentSection.Equals(
                ClientInfoImportWorkspaceSection,
                StringComparison.Ordinal)
                ? $"Showing {ClientInfoClients.Count} client(s) available for workbook import."
                : $"Showing {ClientInfoClients.Count} Client Information record(s).";
        }
        catch (Exception exception)
        {
            SelectedClientInfoClient = null;
            OnPropertyChanged(nameof(HasClientInfoClients));
            OnPropertyChanged(nameof(IsClientInfoResultsEmpty));
            StatusMessage = $"Client Info could not be loaded: {exception.Message}";
            _dialogService.Error(
                "Client Info could not be loaded",
                exception.Message);
        }
    }

    private void ClearClientInfoSearch()
    {
        ClientInfoSearchText = string.Empty;
        RefreshClientInfoClients();
    }

    internal void RefreshClientInfoWorkspace() =>
        RefreshClientInfoClients();

    internal ClientInfoBetaViewModel CreateCanonicalClientInfoProfile(
        ClientInfoClientSummary summary)
    {
        if (!ClientInfoWorkspaceAvailable)
        {
            throw new InvalidOperationException(ClientInfoEmptyText);
        }

        return new ClientInfoBetaViewModel(
            summary.ClientId,
            _repository,
            _currentUser,
            _dialogService);
    }
}
