using System.Collections.ObjectModel;
using Microsoft.Data.SqlClient;
using TechBench.Models;

namespace TechBench.ViewModels;

public sealed partial class MainWindowViewModel
{
    private OrganizationTag? _selectedOrganizationTag;
    private string _newOrganizationTag = string.Empty;
    private WhdUserMapping? _selectedWhdUserMapping;
    private WhdTechnician? _selectedWhdTechnician;

    public ObservableCollection<OrganizationTag> ManagedOrganizationTags { get; } = new();
    public ObservableCollection<WhdUserMapping> WhdUserMappings { get; } = new();
    public ObservableCollection<WhdTechnician> WhdTechnicians { get; } = new();

    public RelayCommand AddOrganizationTagCommand { get; private set; } = null!;

    public RelayCommand DeleteOrganizationTagCommand { get; private set; } = null!;
    public RelayCommand SaveWhdUserMappingCommand { get; private set; } = null!;
    public RelayCommand RefreshWhdAdministrationCommand { get; private set; } = null!;

    public WhdUserMapping? SelectedWhdUserMapping
    {
        get => _selectedWhdUserMapping;
        set
        {
            if (SetProperty(ref _selectedWhdUserMapping, value))
            {
                var technicianExternalId = value?.WhdTechnicianExternalId ?? string.Empty;
                SelectedWhdTechnician = WhdTechnicians.FirstOrDefault(
                    technician => technician.ExternalId == technicianExternalId);
                SaveWhdUserMappingCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public WhdTechnician? SelectedWhdTechnician
    {
        get => _selectedWhdTechnician;
        set
        {
            if (SetProperty(ref _selectedWhdTechnician, value))
            {
                SaveWhdUserMappingCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public OrganizationTag? SelectedOrganizationTag
    {
        get => _selectedOrganizationTag;
        set
        {
            if (SetProperty(ref _selectedOrganizationTag, value))
            {
                DeleteOrganizationTagCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string NewOrganizationTag
    {
        get => _newOrganizationTag;
        set
        {
            if (SetProperty(ref _newOrganizationTag, value))
            {
                AddOrganizationTagCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private void InitializeAdminFeatures()
    {
        AddOrganizationTagCommand = new RelayCommand(
            _ => AddOrganizationTag(),
            _ => CanAddOrganizationTag());
        DeleteOrganizationTagCommand = new RelayCommand(
            _ => DeleteOrganizationTag(),
            _ => _currentUser.CanManageSharedConfiguration
                && SelectedOrganizationTag is { Id: > 0 });
        SaveWhdUserMappingCommand = new RelayCommand(
            _ => SaveWhdUserMapping(),
            _ => _currentUser.CanManageSharedConfiguration
                && SelectedWhdUserMapping is not null
                && SelectedWhdTechnician is not null);
        RefreshWhdAdministrationCommand = new RelayCommand(
            _ => RefreshWhdAdministration(),
            _ => _currentUser.CanManageSharedConfiguration);

        RefreshOrganizationTags();
        RefreshWhdUserMappings();
    }

    private bool CanAddOrganizationTag()
    {
        var tag = NewOrganizationTag.Trim();
        return _currentUser.CanManageSharedConfiguration
            && tag.Length > 0
            && tag.Length <= 1000
            && !tag.Contains(',', StringComparison.Ordinal)
            && !ManagedOrganizationTags.Any(existing =>
                existing.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));
    }

    private void AddOrganizationTag()
    {
        if (!CanAddOrganizationTag())
        {
            return;
        }

        var tag = new OrganizationTag { Tag = NewOrganizationTag.Trim() };
        try
        {
            _repository.SaveOrganizationTag(tag);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            StatusMessage = $"Could not add the shared tag: {ex.Message}";
            _dialogService.Error("Common Tags", StatusMessage);
            return;
        }

        NewOrganizationTag = string.Empty;
        RefreshOrganizationTags(tag.Id);
        RefreshTagSuggestions();
        StatusMessage = $"Added shared tag: {tag.Tag}.";
    }

    private void DeleteOrganizationTag()
    {
        if (!_currentUser.CanManageSharedConfiguration
            || SelectedOrganizationTag is not { Id: > 0 } tag)
        {
            return;
        }

        if (!_dialogService.Confirm(
                "Remove shared tag",
                $"Remove '{tag.Tag}' from the shared tag suggestions? Existing notes will keep their text.",
                "Remove",
                "Keep"))
        {
            return;
        }

        try
        {
            _repository.DeleteOrganizationTag(tag);
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            StatusMessage = $"Could not remove the shared tag: {ex.Message}";
            _dialogService.Error("Common Tags", StatusMessage);
            return;
        }

        RefreshOrganizationTags();
        RefreshTagSuggestions();
        StatusMessage = $"Removed shared tag: {tag.Tag}.";
    }

    private void RefreshOrganizationTags(int? selectedId = null)
    {
        if (!_currentUser.CanManageSharedConfiguration)
        {
            ManagedOrganizationTags.Clear();
            SelectedOrganizationTag = null;
            return;
        }

        selectedId ??= SelectedOrganizationTag?.Id;
        var tags = _repository.GetOrganizationTags();
        ManagedOrganizationTags.Clear();
        foreach (var tag in tags)
        {
            ManagedOrganizationTags.Add(tag);
        }

        SelectedOrganizationTag = selectedId.HasValue
            ? ManagedOrganizationTags.FirstOrDefault(tag => tag.Id == selectedId.Value)
            : null;
        AddOrganizationTagCommand.RaiseCanExecuteChanged();
    }

    private void RefreshWhdUserMappings()
    {
        if (!_currentUser.CanManageSharedConfiguration)
        {
            WhdUserMappings.Clear();
            WhdTechnicians.Clear();
            SelectedWhdUserMapping = null;
            return;
        }

        var selectedLoginName = SelectedWhdUserMapping?.LoginName;
        var selectedTechnicianExternalId = SelectedWhdTechnician?.ExternalId;
        var technicians = _repository.GetWhdTechnicians()
            .Where(technician => technician.IsActive)
            .ToArray();
        var mappings = _repository.GetWhdUserMappings();

        WhdTechnicians.Clear();
        WhdTechnicians.Add(new WhdTechnician
        {
            ExternalId = string.Empty,
            Name = "No WHD technician (remove mapping)",
            IsActive = true
        });
        foreach (var technician in technicians)
        {
            WhdTechnicians.Add(technician);
        }

        WhdUserMappings.Clear();
        foreach (var mapping in mappings)
        {
            WhdUserMappings.Add(mapping);
        }

        SelectedWhdUserMapping = selectedLoginName is null
            ? WhdUserMappings.FirstOrDefault()
            : WhdUserMappings.FirstOrDefault(mapping => mapping.LoginName == selectedLoginName);
        if (selectedTechnicianExternalId is not null)
        {
            SelectedWhdTechnician = WhdTechnicians.FirstOrDefault(
                technician => technician.ExternalId == selectedTechnicianExternalId)
                ?? SelectedWhdTechnician;
        }
    }

    private void RefreshWhdAdministration()
    {
        if (!_currentUser.CanManageSharedConfiguration)
        {
            return;
        }

        RefreshWhdSyncServiceStatus();
        RefreshSageSyncServiceStatus();
        try
        {
            RefreshWhdUserMappings();
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or TimeoutException)
        {
            StatusMessage = $"WHD administration refresh will retry later: {ex.Message}";
        }
    }

    private void SaveWhdUserMapping()
    {
        if (!_currentUser.CanManageSharedConfiguration
            || SelectedWhdUserMapping is not { } mapping
            || SelectedWhdTechnician is not { } technician)
        {
            return;
        }

        try
        {
            mapping.WhdTechnicianExternalId = string.IsNullOrWhiteSpace(technician.ExternalId)
                ? null
                : technician.ExternalId;
            _repository.SaveWhdUserMapping(mapping);
            RefreshWhdUserMappings();
            StatusMessage = mapping.WhdTechnicianExternalId is null
                ? $"Removed the WHD technician mapping for {mapping.UserLabel}."
                : $"Saved the WHD technician mapping for {mapping.UserLabel}.";
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            StatusMessage = $"Could not save the WHD technician mapping: {ex.Message}";
            _dialogService.Error("WHD user mapping", StatusMessage);
        }
    }
}
