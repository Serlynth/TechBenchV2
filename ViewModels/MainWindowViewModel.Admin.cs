using System.Collections.ObjectModel;
using Microsoft.Data.SqlClient;
using TechBench.Models;

namespace TechBench.ViewModels;

public sealed partial class MainWindowViewModel
{
    private OrganizationTag? _selectedOrganizationTag;
    private string _newOrganizationTag = string.Empty;

    public ObservableCollection<OrganizationTag> ManagedOrganizationTags { get; } = new();

    public RelayCommand AddOrganizationTagCommand { get; private set; } = null!;

    public RelayCommand DeleteOrganizationTagCommand { get; private set; } = null!;

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
        RefreshOrganizationTags();
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

}
