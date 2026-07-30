using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using TechBench.Models;
using TechBench.Services;

namespace TechBench.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _microsoftAdminOpenInChromeIncognito;

    public ObservableCollection<CommonLink> CommonLinks { get; } = new();
    public ICollectionView CommonLinksView { get; private set; } = null!;

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
