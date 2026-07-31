using System.Collections.ObjectModel;
using TechBench.Models;

namespace TechBench.ViewModels;

public sealed partial class MainWindowViewModel
{
    public const string ClientInfoWorkspaceSection = "Client Database";

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
        "Search canonical SQL client records by name or internal client ID. "
        + "Open a client to review, import, edit, and manage its cutover independently of FireDrill.";

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
            StatusMessage =
                $"Showing {ClientInfoClients.Count} canonical SQL client record(s).";
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
