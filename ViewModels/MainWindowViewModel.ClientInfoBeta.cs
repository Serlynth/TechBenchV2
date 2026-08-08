using System.Collections.ObjectModel;
using TechBench.Models;

namespace TechBench.ViewModels;

public sealed partial class MainWindowViewModel
{
    public const string ClientInfoWorkspaceSection = "Client Database";
    public const string ClientInfoImportWorkspaceSection = "Workbook Imports";

    private string _clientInfoSearchText = string.Empty;
    private string _clientInfoStatusFilter = "All statuses";
    private string _clientInfoSort = "Name A-Z";
    private ClientInfoClientSummary? _selectedClientInfoClient;

    public ObservableCollection<ClientInfoClientSummary> ClientInfoClients { get; } = [];
    public IReadOnlyList<string> ClientInfoStatusFilterOptions { get; } =
        [
            "All statuses", "Live", "Not started", "Staging", "Ready",
            "Frozen", "Hypercare", "Complete", "Rolled back"
        ];
    public IReadOnlyList<string> ClientInfoSortOptions { get; } =
        ["Name A-Z", "Name Z-A", "Status", "Recently updated", "Internal ID"];

    public RelayCommand SearchClientInfoClientsCommand { get; private set; } = null!;
    public RelayCommand ClearClientInfoSearchCommand { get; private set; } = null!;

    public bool ClientInfoWorkspaceAvailable =>
        _repository.ClientInfoBetaAvailable;

    public bool ShowCreateClientInfoClient => _currentUser.CanManageClients;

    public bool CanCreateClientInfoClient =>
        ShowCreateClientInfoClient
        && _repository.ManualClientInfoCreationAvailable;

    public string CreateClientInfoClientToolTip =>
        CanCreateClientInfoClient
            ? "Create a live client for manual entry. Sage and WHD can be matched later."
            : "Install the current stable TechBench Server package to create live clients manually.";

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
        : "The Client Information SQL extension is not available. Apply the current TechBench SQL package and restart TechBench.";

    public string ClientInfoSearchText
    {
        get => _clientInfoSearchText;
        set => SetProperty(ref _clientInfoSearchText, value);
    }

    public string ClientInfoStatusFilter
    {
        get => _clientInfoStatusFilter;
        set
        {
            if (SetProperty(ref _clientInfoStatusFilter, value))
            {
                RefreshClientInfoClients();
            }
        }
    }

    public string ClientInfoSort
    {
        get => _clientInfoSort;
        set
        {
            if (SetProperty(ref _clientInfoSort, value))
            {
                RefreshClientInfoClients();
            }
        }
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
            var results = new List<ClientInfoClientSummary>();
            var isClientDatabase = CurrentSection.Equals(
                ClientInfoWorkspaceSection,
                StringComparison.Ordinal);
            if (isClientDatabase
                && (string.IsNullOrWhiteSpace(ClientInfoSearchText)
                    || "Demo Client".Contains(
                        ClientInfoSearchText.Trim(),
                        StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(ClientInfoDemoData.Summary);
            }

            var isImportWorkspace = CurrentSection.Equals(
                ClientInfoImportWorkspaceSection,
                StringComparison.Ordinal);
            foreach (var client in _repository.SearchClientInfoClients(
                         ClientInfoSearchText))
            {
                if (!isImportWorkspace || !client.IsLive)
                {
                    results.Add(client);
                }
            }

            IEnumerable<ClientInfoClientSummary> filtered = results;
            if (isClientDatabase && !ClientInfoStatusFilter.Equals(
                    "All statuses",
                    StringComparison.Ordinal))
            {
                filtered = filtered.Where(MatchesClientInfoStatusFilter);
            }

            filtered = ClientInfoSort switch
            {
                "Name Z-A" => filtered.OrderByDescending(
                    client => client.ClientName,
                    StringComparer.OrdinalIgnoreCase),
                "Status" => filtered
                    .OrderBy(client => client.DisplayStatus,
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(client => client.ClientName,
                        StringComparer.OrdinalIgnoreCase),
                "Recently updated" => filtered
                    .OrderByDescending(client => client.UpdatedAtUtc)
                    .ThenBy(client => client.ClientName,
                        StringComparer.OrdinalIgnoreCase),
                "Internal ID" => filtered.OrderBy(client => client.ClientId),
                _ => filtered.OrderBy(
                    client => client.ClientName,
                    StringComparer.OrdinalIgnoreCase)
            };
            foreach (var client in filtered)
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
        _clientInfoStatusFilter = "All statuses";
        _clientInfoSort = "Name A-Z";
        OnPropertyChanged(nameof(ClientInfoStatusFilter));
        OnPropertyChanged(nameof(ClientInfoSort));
        RefreshClientInfoClients();
    }

    private bool MatchesClientInfoStatusFilter(ClientInfoClientSummary client) =>
        ClientInfoStatusFilter switch
        {
            "Live" => client.IsLive,
            "Not started" => !client.IsLive
                && client.CutoverState.Equals(
                    "NotStarted",
                    StringComparison.OrdinalIgnoreCase),
            "Staging" => !client.IsLive
                && client.CutoverState.Equals(
                    "Staging",
                    StringComparison.OrdinalIgnoreCase),
            "Ready" => !client.IsLive
                && client.CutoverState.Equals(
                    "Ready",
                    StringComparison.OrdinalIgnoreCase),
            "Frozen" => !client.IsLive
                && client.CutoverState.Equals(
                    "Frozen",
                    StringComparison.OrdinalIgnoreCase),
            "Hypercare" => !client.IsLive
                && client.CutoverState.Equals(
                    "Hypercare",
                    StringComparison.OrdinalIgnoreCase),
            "Complete" => !client.IsLive
                && client.CutoverState.Equals(
                    "Complete",
                    StringComparison.OrdinalIgnoreCase),
            "Rolled back" => !client.IsLive
                && client.CutoverState.Equals(
                    "RolledBack",
                    StringComparison.OrdinalIgnoreCase),
            _ => true
        };

    internal void RefreshClientInfoWorkspace() =>
        RefreshClientInfoClients();

    internal ClientInfoClientSummary CreateManualClientInfoClient(
        string clientName)
    {
        if (!_currentUser.CanManageClients)
        {
            throw new InvalidOperationException(
                "Only a TechBench Admin may create a shared client.");
        }

        if (!_repository.ManualClientInfoCreationAvailable)
        {
            throw new InvalidOperationException(
                "Install the current stable TechBench Server package, restart TechBench, and try again.");
        }

        var normalizedName = clientName.Trim();
        if (normalizedName.Length == 0)
        {
            throw new InvalidOperationException("Client name is required.");
        }

        var created = _repository.CreateManualClientInfoClient(normalizedName);
        ClientInfoSearchText = created.ClientName;
        RefreshClientInfoClients();
        SelectedClientInfoClient = ClientInfoClients.FirstOrDefault(
            client => client.ClientId == created.ClientId) ?? created;
        StatusMessage =
            $"Created {created.ClientName} as a live Client Information record.";
        return SelectedClientInfoClient;
    }

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
            _dialogService,
            summary.IsDemo ? ClientInfoDemoData.Create() : null);
    }
}
