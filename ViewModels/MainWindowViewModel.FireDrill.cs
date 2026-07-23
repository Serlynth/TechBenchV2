using System.Collections.ObjectModel;
using TechBench.Models;

namespace TechBench.ViewModels;

public sealed partial class MainWindowViewModel
{
    private string _fireDrillSearchText = string.Empty;
    private FireDrillCredentialSummary? _selectedFireDrillCredential;
    private FireDrillCredential? _revealedFireDrillCredential;

    public ObservableCollection<FireDrillCredentialSummary> FireDrillCredentials { get; } = new();
    public ObservableCollection<FireDrillCredentialField> FireDrillCredentialFields { get; } = new();
    public RelayCommand SearchFireDrillCommand { get; private set; } = null!;
    public RelayCommand RevealFireDrillCommand { get; private set; } = null!;
    public RelayCommand CopyFireDrillFieldCommand { get; private set; } = null!;
    public RelayCommand HideFireDrillCommand { get; private set; } = null!;

    public bool CanAccessFireDrill => !_currentUser.IsReadOnlyPreview;
    public bool HasFireDrillCredentials => FireDrillCredentials.Count > 0;
    public bool HasSelectedFireDrillCredential => SelectedFireDrillCredential is not null;
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
        CopyFireDrillFieldCommand = new RelayCommand(CopyFireDrillField, _ => RevealedFireDrillCredential is not null);
        HideFireDrillCommand = new RelayCommand(_ => HideFireDrillCredential(), _ => RevealedFireDrillCredential is not null);
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
        StatusMessage = $"Showing {FireDrillCredentials.Count} client credential record(s).";
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
        FireDrillCredentialFields.Clear();
        if (SelectedFireDrillCredential is null) return;
        foreach (var field in SelectedFireDrillCredential.Fields)
            FireDrillCredentialFields.Add(field with { Value = "***" });
    }

    private void PopulateRevealedFireDrillFields(FireDrillCredential credential)
    {
        FireDrillCredentialFields.Clear();
        foreach (var field in credential.Fields)
            FireDrillCredentialFields.Add(field);
    }

    private void CopyFireDrillField(object? parameter)
    {
        if (RevealedFireDrillCredential is null || parameter is not string field) return;
        var selectedField = RevealedFireDrillCredential.Fields.FirstOrDefault(candidate =>
            candidate.FieldName.Equals(field, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected credential field is invalid.");
        var value = selectedField.Value;
        if (string.IsNullOrEmpty(value))
        {
            StatusMessage = "That credential field is blank.";
            return;
        }
        System.Windows.Clipboard.SetText(value);
        StatusMessage = $"Copied {selectedField.Label} for {RevealedFireDrillCredential.ClientName}.";
    }

    private void ClearRevealedFireDrillCredential()
    {
        FireDrillCredentialFields.Clear();
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
