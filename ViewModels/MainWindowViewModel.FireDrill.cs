using System.Collections.ObjectModel;
using TechBench.Models;

namespace TechBench.ViewModels;

public sealed partial class MainWindowViewModel
{
    private string _fireDrillSearchText = string.Empty;
    private FireDrillCredentialSummary? _selectedFireDrillCredential;
    private FireDrillCredential? _revealedFireDrillCredential;

    public ObservableCollection<FireDrillCredentialSummary> FireDrillCredentials { get; } = new();
    public ObservableCollection<FireDrillCredentialField> FireDrillRevealedFields { get; } = new();
    public RelayCommand SearchFireDrillCommand { get; private set; } = null!;
    public RelayCommand RevealFireDrillCommand { get; private set; } = null!;
    public RelayCommand CopyFireDrillFieldCommand { get; private set; } = null!;
    public RelayCommand HideFireDrillCommand { get; private set; } = null!;

    public bool CanAccessFireDrill => !_currentUser.IsReadOnlyPreview;
    public bool HasFireDrillCredentials => FireDrillCredentials.Count > 0;
    public bool IsFireDrillCredentialRevealed => RevealedFireDrillCredential is not null;

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
        CopyFireDrillFieldCommand = new RelayCommand(CopyFireDrillField, _ => RevealedFireDrillCredential is not null);
        HideFireDrillCommand = new RelayCommand(_ => ClearRevealedFireDrillCredential(), _ => RevealedFireDrillCredential is not null);
    }

    private void RefreshFireDrillCredentials()
    {
        if (!CanAccessFireDrill) return;
        var selectedId = SelectedFireDrillCredential?.CredentialId;
        FireDrillCredentials.Clear();
        foreach (var item in _repository.SearchFireDrillCredentials(FireDrillSearchText))
            FireDrillCredentials.Add(item);
        SelectedFireDrillCredential = selectedId.HasValue
            ? FireDrillCredentials.FirstOrDefault(item => item.CredentialId == selectedId.Value)
            : null;
        OnPropertyChanged(nameof(HasFireDrillCredentials));
        StatusMessage = $"Showing {FireDrillCredentials.Count} FireDrill client credential record(s).";
    }

    private void RevealFireDrillCredential()
    {
        if (SelectedFireDrillCredential is null || !CanAccessFireDrill) return;
        RevealedFireDrillCredential = _repository.RevealFireDrillCredential(SelectedFireDrillCredential.CredentialId)
            ?? throw new InvalidOperationException("The selected FireDrill credential is no longer available.");
        FireDrillRevealedFields.Clear();
        FireDrillRevealedFields.Add(new("Admin", "Admin", RevealedFireDrillCredential.Admin));
        FireDrillRevealedFields.Add(new("csriadmin", "CsriAdmin", RevealedFireDrillCredential.CsriAdmin));
        FireDrillRevealedFields.Add(new("Firebox-DB\\csri", "FireboxDbCsri", RevealedFireDrillCredential.FireboxDbCsri));
        FireDrillRevealedFields.Add(new("AuthPoint User", "AuthpointUser", RevealedFireDrillCredential.AuthpointUser));
        FireDrillRevealedFields.Add(new("SSL VPN Password", "SslVpnPassword", RevealedFireDrillCredential.SslVpnPassword));
        FireDrillRevealedFields.Add(new("AD Auth User", "AdAuthUser", RevealedFireDrillCredential.AdAuthUser));
        FireDrillRevealedFields.Add(new("AD Password", "AdPassword", RevealedFireDrillCredential.AdPassword));
        FireDrillRevealedFields.Add(new("RustPW", "RustPassword", RevealedFireDrillCredential.RustPassword));
        StatusMessage = $"Revealed credentials for {RevealedFireDrillCredential.ClientName}; access was recorded in the SQL audit trail.";
    }

    private void CopyFireDrillField(object? parameter)
    {
        if (RevealedFireDrillCredential is null || parameter is not string field) return;
        var value = field switch
        {
            "Admin" => RevealedFireDrillCredential.Admin,
            "CsriAdmin" => RevealedFireDrillCredential.CsriAdmin,
            "FireboxDbCsri" => RevealedFireDrillCredential.FireboxDbCsri,
            "AuthpointUser" => RevealedFireDrillCredential.AuthpointUser,
            "SslVpnPassword" => RevealedFireDrillCredential.SslVpnPassword,
            "AdAuthUser" => RevealedFireDrillCredential.AdAuthUser,
            "AdPassword" => RevealedFireDrillCredential.AdPassword,
            "RustPassword" => RevealedFireDrillCredential.RustPassword,
            _ => throw new InvalidOperationException("The selected credential field is invalid.")
        };
        if (string.IsNullOrEmpty(value))
        {
            StatusMessage = "That credential field is blank.";
            return;
        }
        _repository.AuditFireDrillCredentialCopy(RevealedFireDrillCredential.CredentialId, field);
        System.Windows.Clipboard.SetText(value);
        StatusMessage = $"Copied {field} for {RevealedFireDrillCredential.ClientName}; the copy was recorded in the SQL audit trail.";
    }

    private void ClearRevealedFireDrillCredential()
    {
        FireDrillRevealedFields.Clear();
        RevealedFireDrillCredential = null;
    }
}
