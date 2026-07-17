using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using Microsoft.Data.SqlClient;
using TechBench.Models;
using TechBench.Services;

namespace TechBench.ViewModels;

public sealed partial class MainWindowViewModel
{
    private int _editingCommonLinkId;
    private string _editingCommonLinkScope = "Organization";
    private bool _isCommonLinkEditorOpen;
    private bool _microsoftAdminOpenInChromeIncognito;
    private string _commonLinkName = string.Empty;
    private string _commonLinkUrl = string.Empty;
    private string _commonLinkValidationMessage = string.Empty;

    public ObservableCollection<CommonLink> CommonLinks { get; } = new();
    public ICollectionView CommonLinksView { get; private set; } = null!;

    public RelayCommand NewCommonLinkCommand { get; private set; } = null!;
    public RelayCommand EditCommonLinkCommand { get; private set; } = null!;
    public RelayCommand SaveCommonLinkCommand { get; private set; } = null!;
    public RelayCommand CancelCommonLinkCommand { get; private set; } = null!;
    public RelayCommand DeleteCommonLinkCommand { get; private set; } = null!;
    public RelayCommand OpenCommonLinkCommand { get; private set; } = null!;

    public bool HasCommonLinks => CommonLinks.Count > 0;

    public bool MicrosoftAdminOpenInChromeIncognito
    {
        get => _microsoftAdminOpenInChromeIncognito;
        set
        {
            if (!SetProperty(ref _microsoftAdminOpenInChromeIncognito, value))
            {
                return;
            }

            _localPreferences.MicrosoftAdminOpenInChromeIncognito = value;
            LocalPreferenceStore.Save(_localPreferences);
            StatusMessage = value
                ? "Microsoft 365 Admin will open in Chrome Incognito."
                : "Microsoft 365 Admin will open in the default browser.";
        }
    }

    public bool IsCommonLinkEditorOpen
    {
        get => _isCommonLinkEditorOpen;
        private set
        {
            if (SetProperty(ref _isCommonLinkEditorOpen, value))
            {
                OnPropertyChanged(nameof(CommonLinkEditorTitle));
                SaveCommonLinkCommand.RaiseCanExecuteChanged();
                CancelCommonLinkCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string CommonLinkEditorTitle => _editingCommonLinkId > 0 ? "Edit Link" : "Add Link";

    public string CommonLinkName
    {
        get => _commonLinkName;
        set
        {
            if (SetProperty(ref _commonLinkName, value))
            {
                CommonLinkValidationMessage = string.Empty;
                SaveCommonLinkCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string CommonLinkUrl
    {
        get => _commonLinkUrl;
        set
        {
            if (SetProperty(ref _commonLinkUrl, value))
            {
                CommonLinkValidationMessage = string.Empty;
                SaveCommonLinkCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string CommonLinkValidationMessage
    {
        get => _commonLinkValidationMessage;
        private set => SetProperty(ref _commonLinkValidationMessage, value);
    }

    private void InitializeCommonLinks()
    {
        CommonLinksView = CollectionViewSource.GetDefaultView(CommonLinks);
        CommonLinksView.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(CommonLink.SectionName)));
        CommonLinksView.SortDescriptions.Add(
            new SortDescription(nameof(CommonLink.SectionOrder), ListSortDirection.Ascending));
        CommonLinksView.SortDescriptions.Add(
            new SortDescription(nameof(CommonLink.SortOrder), ListSortDirection.Ascending));
        CommonLinksView.SortDescriptions.Add(
            new SortDescription(nameof(CommonLink.Name), ListSortDirection.Ascending));

        _microsoftAdminOpenInChromeIncognito =
            _localPreferences.MicrosoftAdminOpenInChromeIncognito;
        NewCommonLinkCommand = new RelayCommand(
            _ => StartNewCommonLink(),
            _ => _currentUser.CanManageClients);
        EditCommonLinkCommand = new RelayCommand(
            EditCommonLink,
            parameter => parameter is CommonLink { Id: > 0, IsBuiltIn: false } link
                && CanManageCommonLink(link));
        SaveCommonLinkCommand = new RelayCommand(_ => SaveCommonLink(), _ => CanSaveCommonLink());
        CancelCommonLinkCommand = new RelayCommand(_ => CloseCommonLinkEditor(), _ => IsCommonLinkEditorOpen);
        DeleteCommonLinkCommand = new RelayCommand(
            DeleteCommonLink,
            parameter => parameter is CommonLink { Id: > 0, IsBuiltIn: false } link
                && CanManageCommonLink(link));
        OpenCommonLinkCommand = new RelayCommand(OpenCommonLink, parameter => parameter is CommonLink { Id: > 0 });
        RefreshCommonLinks();
    }

    private void RefreshCommonLinks()
    {
        ReplaceCommonLinks(CommonLinks, _repository.GetCommonLinks());

        OnPropertyChanged(nameof(HasCommonLinks));
    }

    internal static void ReplaceCommonLinks(
        ObservableCollection<CommonLink> target,
        IEnumerable<CommonLink> refreshedLinks)
    {
        target.Clear();
        foreach (var link in refreshedLinks)
        {
            target.Add(link);
        }
    }

    private void StartNewCommonLink()
    {
        _editingCommonLinkId = 0;
        _editingCommonLinkScope = "Organization";
        CommonLinkName = string.Empty;
        CommonLinkUrl = string.Empty;
        CommonLinkValidationMessage = string.Empty;
        IsCommonLinkEditorOpen = true;
        OnPropertyChanged(nameof(CommonLinkEditorTitle));
    }

    private void EditCommonLink(object? parameter)
    {
        if (parameter is not CommonLink { Id: > 0, IsBuiltIn: false } link
            || !CanManageCommonLink(link))
        {
            return;
        }

        _editingCommonLinkId = link.Id;
        _editingCommonLinkScope = link.ScopeType;
        CommonLinkName = link.Name;
        CommonLinkUrl = link.Url;
        CommonLinkValidationMessage = string.Empty;
        IsCommonLinkEditorOpen = true;
        OnPropertyChanged(nameof(CommonLinkEditorTitle));
    }

    private bool CanSaveCommonLink()
    {
        return IsCommonLinkEditorOpen
            && !string.IsNullOrWhiteSpace(CommonLinkName)
            && !string.IsNullOrWhiteSpace(CommonLinkUrl);
    }

    private void SaveCommonLink()
    {
        var name = CommonLinkName.Trim();
        if (name.Length > 80)
        {
            CommonLinkValidationMessage = "Keep the link name to 80 characters or fewer.";
            return;
        }

        if (!TryNormalizeCommonLinkUrl(CommonLinkUrl, out var normalizedUrl, out var validationMessage))
        {
            CommonLinkValidationMessage = validationMessage;
            return;
        }

        if (CommonLinks.Any(link =>
                link.Id != _editingCommonLinkId
                && link.Url.Equals(normalizedUrl, StringComparison.OrdinalIgnoreCase)))
        {
            CommonLinkValidationMessage = "That address is already in Common Links.";
            return;
        }

        try
        {
            _repository.SaveCommonLink(new CommonLink
            {
                Id = _editingCommonLinkId,
                ScopeType = _editingCommonLinkScope,
                Name = name,
                Url = normalizedUrl
            });
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException)
        {
            CommonLinkValidationMessage = $"Could not save this link: {ex.Message}";
            return;
        }

        RefreshCommonLinks();
        CloseCommonLinkEditor();
        StatusMessage = $"Saved common link: {name}.";
    }

    private void DeleteCommonLink(object? parameter)
    {
        if (parameter is not CommonLink { Id: > 0, IsBuiltIn: false } link
            || !CanManageCommonLink(link))
        {
            return;
        }

        if (!_dialogService.Confirm(
                "Remove common link",
                $"Remove {link.Name} from Common Links?",
                "Remove",
                "Keep"))
        {
            return;
        }

        _repository.DeleteCommonLink(link.Id);
        if (_editingCommonLinkId == link.Id)
        {
            CloseCommonLinkEditor();
        }

        RefreshCommonLinks();
        StatusMessage = $"Removed common link: {link.Name}.";
    }

    private bool CanManageCommonLink(CommonLink link) =>
        link.ScopeType.Equals("User", StringComparison.OrdinalIgnoreCase)
        || _currentUser.CanManageClients;

    private void OpenCommonLink(object? parameter)
    {
        if (parameter is not CommonLink link)
        {
            return;
        }

        if (!TryNormalizeCommonLinkUrl(link.Url, out var normalizedUrl, out var validationMessage))
        {
            _dialogService.Error("Open common link", validationMessage);
            return;
        }

        var launchResult = link.BuiltInKey == "microsoft-365-admin"
                           && MicrosoftAdminOpenInChromeIncognito
            ? CommonLinkLauncher.OpenChromeIncognito(normalizedUrl)
            : CommonLinkLauncher.OpenDefault(normalizedUrl);
        if (launchResult.Succeeded)
        {
            StatusMessage = link.BuiltInKey == "microsoft-365-admin"
                            && MicrosoftAdminOpenInChromeIncognito
                ? $"Opened {link.Name} in Chrome Incognito."
                : $"Opened {link.Name}.";
        }
        else
        {
            _dialogService.Error(
                "Open common link",
                $"Could not open {link.Name}: {launchResult.ErrorMessage}");
        }
    }

    private void CloseCommonLinkEditor()
    {
        _editingCommonLinkId = 0;
        _editingCommonLinkScope = "Organization";
        IsCommonLinkEditorOpen = false;
        CommonLinkName = string.Empty;
        CommonLinkUrl = string.Empty;
        CommonLinkValidationMessage = string.Empty;
        OnPropertyChanged(nameof(CommonLinkEditorTitle));
    }

    private static bool TryNormalizeCommonLinkUrl(
        string? value,
        out string normalizedUrl,
        out string validationMessage)
    {
        normalizedUrl = string.Empty;
        validationMessage = string.Empty;
        var candidate = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            validationMessage = "Enter a website address.";
            return false;
        }

        if (!candidate.Contains("://", StringComparison.Ordinal))
        {
            candidate = $"https://{candidate}";
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            validationMessage = "Enter a valid http or https website address.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            validationMessage = "Do not put a username or password in the website address.";
            return false;
        }

        normalizedUrl = uri.AbsoluteUri;
        return true;
    }
}
