using System.Collections.ObjectModel;
using TechBench.Models;
using TechBench.Services;

namespace TechBench.ViewModels;

internal sealed class ClientInfoProfileViewModel : ObservableObject
{
    private readonly FireDrillCredentialSummary _summary;
    private readonly Func<long, FireDrillCredential?> _revealCredential;
    private bool _isRevealed;
    private bool _isCopying;
    private string _statusMessage;

    public ClientInfoProfileViewModel(
        FireDrillCredentialSummary summary,
        Func<long, FireDrillCredential?> revealCredential,
        Client? whdClient = null,
        IReadOnlyList<EquipmentItem>? equipment = null)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(revealCredential);
        _summary = summary;
        _revealCredential = revealCredential;
        WhdClient = whdClient;
        _statusMessage = "Values are hidden. Click Reveal All to view this client's complete information.";
        RevealCommand = new RelayCommand(_ => Reveal(), _ => !IsRevealed);
        HideCommand = new RelayCommand(_ => Hide(), _ => IsRevealed);
        CopyFieldCommand = new RelayCommand(
            CopyField,
            parameter => IsRevealed && !_isCopying && parameter is FireDrillCredentialField);
        foreach (var item in equipment ?? [])
            Equipment.Add(item);
        PopulateGroups(Mask(summary.Fields));
    }

    public ObservableCollection<FireDrillCredentialFieldGroup> CredentialGroups { get; } = new();
    public ObservableCollection<EquipmentItem> Equipment { get; } = new();
    public RelayCommand RevealCommand { get; }
    public RelayCommand HideCommand { get; }
    public RelayCommand CopyFieldCommand { get; }
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
    public bool HasProfileContent => HasFields || HasEquipment;
    public string EquipmentCountLabel =>
        $"{Equipment.Count} inventory item{(Equipment.Count == 1 ? string.Empty : "s")}";

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
