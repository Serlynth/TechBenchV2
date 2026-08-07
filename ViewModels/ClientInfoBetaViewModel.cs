using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Data.SqlClient;
using Microsoft.Win32;
using TechBench.Data;
using TechBench.Models;
using TechBench.Services;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using WpfApplication = System.Windows.Application;
using WpfClipboard = System.Windows.Clipboard;

namespace TechBench.ViewModels;

public sealed class ClientInfoBetaViewModel : ObservableObject
{
    private sealed record ResourceAccessSlot(
        string Key,
        string Label,
        ClientInfoCredential? Credential);

    private const string UserAdCredentialCategory = "Active Directory User";
    private const string UserMicrosoft365CredentialCategory =
        "Microsoft 365 User";
    private static readonly string[] ReviewStatuses =
    [
        "Unverified", "Verified", "AcceptedUnverified", "NeedsReview"
    ];

    private static readonly string[] BooleanOptions = ["Yes", "No"];
    private static readonly string[] AttachmentCategories =
    [
        "Photos", "Documents", "Hardware", "Location", "Diagram", "Other"
    ];
    private static readonly string[] ResourceCategories =
        ClientInfoResourceCategories.All;
    private readonly ITechBenchRepository _repository;
    private readonly CurrentUserContext _currentUser;
    private readonly IUserDialogService _dialogs;
    private readonly ClientInfoWorkbookService _workbooks = new();
    private readonly ClientAttachmentStorageService _attachmentStorage;
    private readonly ClientInfoDemoSnapshotData? _demoData;
    private ClientInfoProfile _profile = new();
    private string _summary = "";
    private string _clientFolderPath = "";
    private string _legacyClientInfoSheetPath = "";
    private string _reviewStatus = "Unverified";
    private string _statusMessage = "Loading client information...";
    private ClientInfoLocation? _selectedLocation;
    private ClientInfoPerson? _selectedPerson;
    private ClientInfoResource? _selectedResource;
    private ClientInfoCredential? _selectedCredential;
    private ClientInfoFact? _selectedFact;
    private ClientInfoImportBatch? _selectedImportBatch;
    private ClientInfoAttachment? _selectedAttachment;
    private EquipmentItem? _selectedClientEquipment;
    private ImageSource? _selectedAttachmentPreview;
    private string _selectedAttachmentPreviewMessage =
        "Select an attachment to preview it.";
    private string _attachmentStorageStatus =
        "Checking attachment storage...";
    private ClientAttachmentStorageConfiguration _attachmentConfiguration = new();
    private bool _showArchivedAttachments;
    private bool _isAttachmentOperationRunning;

    public ClientInfoBetaViewModel(
        int clientId,
        ITechBenchRepository repository,
        CurrentUserContext currentUser,
        IUserDialogService dialogs,
        ClientInfoDemoSnapshotData? demoData = null)
    {
        _demoData = demoData;
        ClientId = demoData?.Summary.ClientId ?? clientId;
        _repository = repository;
        _currentUser = currentUser;
        _dialogs = dialogs;
        _attachmentStorage = new ClientAttachmentStorageService(repository);

        RefreshCommand = new RelayCommand(_ => Refresh());
        SaveProfileCommand = new RelayCommand(
            _ => SaveProfile(),
            _ => CanEdit);
        OpenClientFolderCommand = new RelayCommand(
            _ => OpenServerLink(ClientFolderPath, "client folder"),
            _ => !string.IsNullOrWhiteSpace(ClientFolderPath));
        OpenLegacyClientInfoSheetCommand = new RelayCommand(
            _ => OpenServerLink(
                LegacyClientInfoSheetPath,
                "legacy Client Info sheet"),
            _ => !string.IsNullOrWhiteSpace(LegacyClientInfoSheetPath));
        AddLocationCommand = new RelayCommand(
            _ => EditLocation(null),
            _ => CanEdit);
        EditLocationCommand = new RelayCommand(
            item => EditLocation(item as ClientInfoLocation ?? SelectedLocation),
            _ => CanEdit && SelectedLocation is not null);
        AddPersonCommand = new RelayCommand(
            _ => EditPerson(null),
            _ => CanEdit);
        EditPersonCommand = new RelayCommand(
            item => EditPerson(item as ClientInfoPerson ?? SelectedPerson),
            _ => CanEdit && SelectedPerson is not null);
        AddResourceCommand = new RelayCommand(
            category => EditResource(null, category as string),
            _ => CanEdit);
        EditResourceCommand = new RelayCommand(
            item => EditResource(item as ClientInfoResource ?? SelectedResource),
            item => CanEdit && (item is ClientInfoResource || SelectedResource is not null));
        MoveResourceCommand = new RelayCommand(
            item => MoveResource(item as ClientInfoResource ?? SelectedResource),
            item => CanEdit && (item is ClientInfoResource || SelectedResource is not null));
        ManageResourceFieldsCommand = new RelayCommand(
            item => ManageResourceFields(item as ClientInfoResource ?? SelectedResource),
            item => CanEdit && (item is ClientInfoResource || SelectedResource is not null));
        AddCredentialCommand = new RelayCommand(
            _ => EditCredential(null),
            _ => CanEdit);
        EditCredentialCommand = new RelayCommand(
            item => EditCredential(
                item as ClientInfoCredential ?? SelectedCredential),
            item => CanEdit && (item is ClientInfoCredential || SelectedCredential is not null));
        AddFactCommand = new RelayCommand(
            _ => EditFact(null),
            _ => CanEdit);
        EditFactCommand = new RelayCommand(
            item => EditFact(item as ClientInfoFact ?? SelectedFact),
            _ => CanEdit && SelectedFact is not null);
        AddSecretCommand = new RelayCommand(
            _ => EditSecret(null),
            _ => CanEdit && SelectedCredential is not null);
        ReplaceSecretCommand = new RelayCommand(
            item => EditSecret(item as ClientInfoSecretSummary),
            item => CanEdit
                && SelectedCredential is not null
                && item is ClientInfoSecretSummary);
        RevealSecretCommand = new RelayCommand(
            item => ToggleSecret(item as ClientInfoSecretSummary),
            item => CanRevealSecrets && item is ClientInfoSecretSummary);
        CopySecretCommand = new RelayCommand(
            item => CopySecret(item as ClientInfoSecretSummary),
            item => CanRevealSecrets && item is ClientInfoSecretSummary);
        CreateTemplateCommand = new RelayCommand(_ => CreateTemplate());
        ImportWorkbookCommand = new RelayCommand(
            _ => ImportWorkbook(),
            _ => CanManageImports);
        ReloadImportCommand = new RelayCommand(
            _ => ReloadSelectedImport(),
            _ => CanManageImports && SelectedImportBatch is not null);
        CompareImportCommand = new RelayCommand(
            _ => CompareSelectedImport(),
            _ => CanManageImports
                && SelectedImportBatch is
                    { State: "InReview" or "ValidationFailed" or "Validated" });
        AcceptUnverifiedImportCommand = new RelayCommand(
            _ => AcceptUnverifiedImport(),
            _ => CanManageImports
                && SelectedImportBatch is
                    { State: "InReview" or "ValidationFailed" or "Validated" }
                && SelectedImportBatch.Issues.Any(issue =>
                    !issue.IsResolved
                    && issue.IssueCode.Equals(
                        "UNVERIFIED_RECORD",
                        StringComparison.OrdinalIgnoreCase)));
        DiscardImportCommand = new RelayCommand(
            _ => DiscardImport(),
            _ => CanManageImports
                && SelectedImportBatch is
                    { State: "Draft" or "Parsed" or "Validated" or "InReview"
                        or "ValidationFailed" or "Approved" });
        ApproveImportCommand = new RelayCommand(
            _ => ApproveImport(),
            _ => CanManageImports
                && SelectedImportBatch is { State: "InReview" }
                && SelectedImportBatch.BlockingIssueCount == 0
                && !SelectedImportBatch.Issues.Any(issue =>
                    !issue.IsResolved
                    && (issue.IssueCode.Equals(
                            "UNVERIFIED_RECORD",
                            StringComparison.OrdinalIgnoreCase)
                        || issue.IssueCode.Equals(
                            "NEEDS_REVIEW_RECORD",
                            StringComparison.OrdinalIgnoreCase))));
        PromoteImportCommand = new RelayCommand(
            _ => PromoteImport(),
            _ => CanManageImports
                && SelectedImportBatch is { State: "Approved" });
        UploadAttachmentCommand = new AsyncRelayCommand(
            _ => UploadAttachmentsFromDialogAsync(),
            _ => CanUploadAttachments);
        PasteAttachmentCommand = new AsyncRelayCommand(
            _ => PasteAttachmentAsync(),
            _ => CanUploadAttachments);
        EditAttachmentCommand = new RelayCommand(
            item => EditAttachment(item as ClientInfoAttachment
                ?? SelectedAttachment),
            _ => CanEditAttachments && SelectedAttachment is not null);
        LinkAttachmentToEquipmentCommand = new RelayCommand(
            item => LinkAttachmentToEquipment(item as ClientInfoAttachment
                ?? SelectedAttachment),
            item => CanEditAttachments
                && (item is ClientInfoAttachment || SelectedAttachment is not null));
        OpenAttachmentCommand = new RelayCommand(
            item => OpenAttachment(item as ClientInfoAttachment
                ?? SelectedAttachment),
            item => item is ClientInfoAttachment || SelectedAttachment is not null);
        CopyAttachmentCommand = new RelayCommand(
            item => CopyAttachment(item as ClientInfoAttachment
                ?? SelectedAttachment),
            item => item is ClientInfoAttachment || SelectedAttachment is not null);
        DownloadAttachmentCommand = new RelayCommand(
            item => DownloadAttachment(item as ClientInfoAttachment
                ?? SelectedAttachment),
            item => item is ClientInfoAttachment || SelectedAttachment is not null);
        ArchiveAttachmentCommand = new RelayCommand(
            item => SetAttachmentArchived(
                item as ClientInfoAttachment ?? SelectedAttachment,
                true),
            _ => CanEditAttachments
                && SelectedAttachment is { IsArchived: false });
        RestoreAttachmentCommand = new RelayCommand(
            item => SetAttachmentArchived(
                item as ClientInfoAttachment ?? SelectedAttachment,
                false),
            _ => CanEditAttachments
                && SelectedAttachment is { IsArchived: true });
        RefreshAttachmentsCommand = new RelayCommand(_ => RefreshAttachments());
        Refresh();
    }

    public int ClientId { get; }
    public string WindowTitle => $"{ClientName} · Client Info Beta";
    public string ClientName => Profile.ClientName;
    public bool IsDemo => _demoData is not null;
    public string InternalClientIdLabel => IsDemo ? "Local demo · not stored" : $"Internal client ID {ClientId}";
    public string CutoverLabel => Profile.IsLive
        ? $"Canonical SQL is live · {Profile.CutoverState}"
        : $"Migration workspace · {Profile.CutoverState}";
    public string ClientInfoStatusLabel => IsDemo
        ? "Read-only example data"
        : Profile.IsLive
        ? $"Client Information is live - {Profile.CutoverState}"
        : $"Client Information draft - {Profile.CutoverState}";
    public bool CanEdit => !IsDemo && _currentUser.CanWrite;
    public bool IsProfileReadOnly => !CanEdit;
    public bool CanRevealSecrets => IsDemo || !_currentUser.IsReadOnlyPreview;
    public bool CanManageImports => !IsDemo && _currentUser.IsAdmin && _currentUser.CanWrite;
    public bool CanEditAttachments =>
        CanEdit && !_isAttachmentOperationRunning;
    public bool CanUploadAttachments =>
        CanEditAttachments && _attachmentConfiguration.IsConfigured;
    public bool HasImportBatches => ImportBatches.Count > 0;
    public bool HasSelectedImportBatch => SelectedImportBatch is not null;
    public string ImportPermissionText => CanManageImports
        ? "You can import, review, approve, and add this client's workbook to Client Information."
        : "Only a TechBench Admin can import and approve workbooks. You can still create the client template.";
    public IReadOnlyList<string> ReviewStatusOptions => ReviewStatuses;

    public ClientInfoProfile Profile
    {
        get => _profile;
        private set
        {
            if (!SetProperty(ref _profile, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ClientName));
            OnPropertyChanged(nameof(WindowTitle));
            OnPropertyChanged(nameof(InternalClientIdLabel));
            OnPropertyChanged(nameof(CutoverLabel));
            OnPropertyChanged(nameof(ClientInfoStatusLabel));
        }
    }

    public string Summary
    {
        get => _summary;
        set => SetProperty(ref _summary, value);
    }

    public string ClientFolderPath
    {
        get => _clientFolderPath;
        set
        {
            if (SetProperty(ref _clientFolderPath, value))
            {
                OpenClientFolderCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string LegacyClientInfoSheetPath
    {
        get => _legacyClientInfoSheetPath;
        set
        {
            if (SetProperty(ref _legacyClientInfoSheetPath, value))
            {
                OpenLegacyClientInfoSheetCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ReviewStatus
    {
        get => _reviewStatus;
        set => SetProperty(ref _reviewStatus, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public ObservableCollection<ClientInfoLocation> Locations { get; } = [];
    public ObservableCollection<ClientInfoPerson> People { get; } = [];
    public ObservableCollection<ClientInfoResource> Resources { get; } = [];
    public ObservableCollection<ClientInfoCategoryOverviewSection>
        QuickReferenceSections { get; } = [];
    public bool HasQuickReferenceSections => QuickReferenceSections.Count > 0;
    public string QuickReferenceCountLabel => QuickReferenceSections.Count == 1
        ? "1 quick-reference group"
        : $"{QuickReferenceSections.Count} quick-reference groups";
    public ObservableCollection<EquipmentItem> Equipment { get; } = [];
    public ClientInfoResourceGroup ServerInfrastructureGroup { get; } = new(
        ClientInfoResourceCategories.ServersInfrastructure,
        "Servers, virtual machines, hypervisors, storage, switches, network appliances, directory services, and infrastructure roles.");
    public ClientInfoResourceGroup ConnectionInternetGroup { get; } = new(
        ClientInfoResourceCategories.ConnectionInternet,
        "Firewalls, routers, ISPs, circuits, VLANs, VPNs, and public IP information.");
    public ClientInfoResourceGroup WifiGroup { get; } = new(
        ClientInfoResourceCategories.Wifi,
        "Wireless networks, access points, SSIDs, controllers, and related Wi-Fi information.");
    public ClientInfoResourceGroup ApplicationsCloudGroup { get; } = new(
        ClientInfoResourceCategories.ApplicationsCloud,
        "Microsoft 365, line-of-business applications, SaaS products, cloud services, and licensing.");
    public ClientInfoResourceGroup DomainsEmailGroup { get; } = new(
        ClientInfoResourceCategories.DomainsEmail,
        "Domains, DNS, registrars, email tenants, Exchange, and mailbox services.");
    public ClientInfoResourceGroup BackupGroup { get; } = new(
        ClientInfoResourceCategories.Backup,
        "Backup platforms, protected systems, schedules, retention, restore testing, and disaster recovery services.");
    public ClientInfoResourceGroup SecurityGroup { get; } = new(
        ClientInfoResourceCategories.Security,
        "Antivirus, EDR, MFA, email filtering, security monitoring, and other protection services.");
    public ClientInfoResourceGroup VendorsServicesGroup { get; } = new(
        ClientInfoResourceCategories.VendorsServices,
        "Support vendors, contracts, phone and copier providers, renewals, and managed services.");
    public ClientInfoResourceGroup NeedsSortingGroup { get; } = new(
        ClientInfoResourceCategories.NeedsSorting,
        "Older or unclear records that still need to be assigned to a category.");
    public ObservableCollection<ClientInfoCredential> Credentials { get; } = [];
    public ObservableCollection<ClientInfoFact> Facts { get; } = [];
    public ObservableCollection<ClientInfoImportBatch> ImportBatches { get; } = [];
    public ObservableCollection<ClientInfoImportIssue> ImportIssues { get; } = [];
    public ObservableCollection<ClientInfoAttachment> Attachments { get; } = [];
    public ObservableCollection<ClientInfoAttachment> SelectedEquipmentAttachments { get; } = [];

    public string AttachmentStorageStatus
    {
        get => _attachmentStorageStatus;
        private set => SetProperty(ref _attachmentStorageStatus, value);
    }

    public bool ShowArchivedAttachments
    {
        get => _showArchivedAttachments;
        set
        {
            if (SetProperty(ref _showArchivedAttachments, value))
            {
                RefreshAttachments();
            }
        }
    }

    public ClientInfoAttachment? SelectedAttachment
    {
        get => _selectedAttachment;
        set
        {
            if (!SetProperty(ref _selectedAttachment, value))
            {
                return;
            }

            LoadAttachmentPreview(value);
            RaiseAttachmentCommandState();
        }
    }

    public EquipmentItem? SelectedClientEquipment
    {
        get => _selectedClientEquipment;
        set
        {
            if (SetProperty(ref _selectedClientEquipment, value))
            {
                OnPropertyChanged(nameof(HasSelectedClientEquipment));
                RefreshSelectedEquipmentAttachments();
            }
        }
    }

    public bool HasSelectedClientEquipment => SelectedClientEquipment is not null;
    public bool HasSelectedEquipmentAttachments =>
        SelectedEquipmentAttachments.Count > 0;
    public string SelectedEquipmentAttachmentCountLabel =>
        SelectedEquipmentAttachments.Count == 1
            ? "1 linked attachment"
            : $"{SelectedEquipmentAttachments.Count} linked attachments";

    public ImageSource? SelectedAttachmentPreview
    {
        get => _selectedAttachmentPreview;
        private set
        {
            if (SetProperty(ref _selectedAttachmentPreview, value))
            {
                OnPropertyChanged(nameof(HasSelectedAttachmentPreview));
            }
        }
    }

    public bool HasSelectedAttachmentPreview =>
        SelectedAttachmentPreview is not null;

    public string SelectedAttachmentPreviewMessage
    {
        get => _selectedAttachmentPreviewMessage;
        private set => SetProperty(ref _selectedAttachmentPreviewMessage, value);
    }

    public string AttachmentCountLabel => Attachments.Count == 1
        ? "1 attachment"
        : $"{Attachments.Count} attachments";

    public ClientInfoLocation? SelectedLocation
    {
        get => _selectedLocation;
        set
        {
            if (SetProperty(ref _selectedLocation, value))
            {
                EditLocationCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ClientInfoPerson? SelectedPerson
    {
        get => _selectedPerson;
        set
        {
            if (SetProperty(ref _selectedPerson, value))
            {
                EditPersonCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ClientInfoResource? SelectedResource
    {
        get => _selectedResource;
        set
        {
            if (SetProperty(ref _selectedResource, value))
            {
                EditResourceCommand.RaiseCanExecuteChanged();
                MoveResourceCommand.RaiseCanExecuteChanged();
                ManageResourceFieldsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ClientInfoCredential? SelectedCredential
    {
        get => _selectedCredential;
        set
        {
            if (SetProperty(ref _selectedCredential, value))
            {
                EditCredentialCommand.RaiseCanExecuteChanged();
                AddSecretCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ClientInfoFact? SelectedFact
    {
        get => _selectedFact;
        set
        {
            if (SetProperty(ref _selectedFact, value))
            {
                EditFactCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ClientInfoImportBatch? SelectedImportBatch
    {
        get => _selectedImportBatch;
        set
        {
            if (!SetProperty(ref _selectedImportBatch, value))
            {
                return;
            }

            ImportIssues.Clear();
            if (value is not null)
            {
                foreach (var issue in value.Issues)
                {
                    ImportIssues.Add(issue);
                }
            }

            ReloadImportCommand.RaiseCanExecuteChanged();
            CompareImportCommand.RaiseCanExecuteChanged();
            AcceptUnverifiedImportCommand.RaiseCanExecuteChanged();
            DiscardImportCommand.RaiseCanExecuteChanged();
            ApproveImportCommand.RaiseCanExecuteChanged();
            PromoteImportCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(HasSelectedImportBatch));
        }
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand SaveProfileCommand { get; }
    public RelayCommand OpenClientFolderCommand { get; }
    public RelayCommand OpenLegacyClientInfoSheetCommand { get; }
    public RelayCommand AddLocationCommand { get; }
    public RelayCommand EditLocationCommand { get; }
    public RelayCommand AddPersonCommand { get; }
    public RelayCommand EditPersonCommand { get; }
    public RelayCommand AddResourceCommand { get; }
    public RelayCommand EditResourceCommand { get; }
    public RelayCommand MoveResourceCommand { get; }
    public RelayCommand ManageResourceFieldsCommand { get; }
    public RelayCommand AddCredentialCommand { get; }
    public RelayCommand EditCredentialCommand { get; }
    public RelayCommand AddFactCommand { get; }
    public RelayCommand EditFactCommand { get; }
    public RelayCommand AddSecretCommand { get; }
    public RelayCommand ReplaceSecretCommand { get; }
    public RelayCommand RevealSecretCommand { get; }
    public RelayCommand CopySecretCommand { get; }
    public RelayCommand CreateTemplateCommand { get; }
    public RelayCommand ImportWorkbookCommand { get; }
    public RelayCommand ReloadImportCommand { get; }
    public RelayCommand CompareImportCommand { get; }
    public RelayCommand AcceptUnverifiedImportCommand { get; }
    public RelayCommand DiscardImportCommand { get; }
    public RelayCommand ApproveImportCommand { get; }
    public RelayCommand PromoteImportCommand { get; }
    public AsyncRelayCommand UploadAttachmentCommand { get; }
    public AsyncRelayCommand PasteAttachmentCommand { get; }
    public RelayCommand EditAttachmentCommand { get; }
    public RelayCommand LinkAttachmentToEquipmentCommand { get; }
    public RelayCommand OpenAttachmentCommand { get; }
    public RelayCommand CopyAttachmentCommand { get; }
    public RelayCommand DownloadAttachmentCommand { get; }
    public RelayCommand ArchiveAttachmentCommand { get; }
    public RelayCommand RestoreAttachmentCommand { get; }
    public RelayCommand RefreshAttachmentsCommand { get; }

    public void Refresh()
    {
        ClearRevealedSecrets();
        try
        {
            var selectedEquipmentId = SelectedClientEquipment?.EquipmentId;
            var snapshot = _demoData?.Snapshot
                ?? _repository.GetClientInfoSnapshot(ClientId)
                ?? throw new InvalidOperationException(
                    "The selected client is no longer available.");
            Profile = snapshot.Profile;
            Summary = snapshot.Profile.Summary;
            ClientFolderPath = snapshot.Profile.ClientFolderPath;
            LegacyClientInfoSheetPath =
                snapshot.Profile.LegacyClientInfoSheetPath;
            ReviewStatus = snapshot.Profile.ReviewStatus;
            var displayCredentials = AddCredentialDisplayLinks(
                snapshot.Credentials,
                snapshot.Resources,
                snapshot.People);
            Replace(Locations, snapshot.Locations);
            Replace(
                People,
                snapshot.People.Select(person => person with
                {
                    AdCredential = FindUserAdCredential(
                        person,
                        displayCredentials),
                    Microsoft365Credential = FindUserMicrosoft365Credential(
                        person,
                        displayCredentials)
                }));
            Replace(Resources, snapshot.Resources);
            Replace(
                Equipment,
                _demoData?.Equipment
                ?? (_repository.EquipmentBoardAvailable
                    ? _repository.GetEquipmentInventory(ClientId)
                    : []));
            SelectedClientEquipment = selectedEquipmentId is > 0
                ? Equipment.FirstOrDefault(
                    item => item.EquipmentId == selectedEquipmentId.Value)
                    ?? Equipment.FirstOrDefault()
                : Equipment.FirstOrDefault();
            Replace(Credentials, displayCredentials);
            RefreshResourceGroups();
            Replace(Facts, snapshot.Facts);
            Replace(ImportBatches, snapshot.ImportBatches);
            OnPropertyChanged(nameof(HasImportBatches));
            SelectedImportBatch = null;
            if (IsDemo)
            {
                Attachments.Clear();
                RefreshSelectedEquipmentAttachments();
                AttachmentStorageStatus = "Demo Client is local and does not use attachment storage.";
                StatusMessage = "Showing fictional, read-only example data. Nothing on this page is stored in SQL.";
            }
            else
            {
                RefreshAttachments();
                StatusMessage =
                $"Loaded {Locations.Count} locations, {People.Count} users, "
                + $"{Equipment.Count} equipment records, {Resources.Count} technology records, "
                + $"{Credentials.Count} passwords, and {Attachments.Count} attachments.";
            }
        }
        catch (Exception exception)
        {
            ShowError("Client Info could not be loaded", exception);
        }
    }

    internal static IReadOnlyList<ClientInfoCredential> AddCredentialDisplayLinks(
        IReadOnlyList<ClientInfoCredential> credentials,
        IReadOnlyList<ClientInfoResource> resources,
        IReadOnlyList<ClientInfoPerson> people)
    {
        var resourceNames = resources
            .GroupBy(resource => resource.ResourceId)
            .ToDictionary(group => group.Key, group => group.First().Name);
        var personNames = people
            .GroupBy(person => person.PersonId)
            .ToDictionary(group => group.Key, group => group.First().DisplayName);
        return credentials.Select(credential => credential with
        {
            LinkedResourceName = credential.ResourceId.HasValue
                && resourceNames.TryGetValue(
                    credential.ResourceId.Value,
                    out var resourceName)
                    ? resourceName
                    : string.Empty,
            LinkedPersonName = credential.PersonId.HasValue
                && personNames.TryGetValue(
                    credential.PersonId.Value,
                    out var personName)
                    ? personName
                    : string.Empty
        }).ToArray();
    }

    private void RefreshAttachments()
    {
        var selectedId = SelectedAttachment?.AttachmentId;
        try
        {
            _attachmentConfiguration = _attachmentStorage.GetConfiguration();
            Replace(
                Attachments,
                _repository.GetClientInfoAttachments(
                    ClientId,
                    ShowArchivedAttachments));
            SelectedAttachment = selectedId.HasValue
                ? Attachments.FirstOrDefault(
                    attachment => attachment.AttachmentId == selectedId.Value)
                : null;
            AttachmentStorageStatus = _attachmentConfiguration.IsConfigured
                ? $"Automatic client-ID storage is ready. Maximum file size: "
                  + $"{_attachmentConfiguration.MaximumFileSizeMegabytes} MB."
                : "Uploads are disabled until Attachment Storage is configured in Server Manager.";
        }
        catch (SqlException exception) when (exception.Number == 2812)
        {
            _attachmentConfiguration = new ClientAttachmentStorageConfiguration();
            Attachments.Clear();
            SelectedAttachment = null;
            AttachmentStorageStatus =
                "Client attachments require the matching TechBench server/SQL update.";
        }
        catch (Exception exception)
        {
            _attachmentConfiguration = new ClientAttachmentStorageConfiguration();
            Attachments.Clear();
            SelectedAttachment = null;
            AttachmentStorageStatus =
                "Attachment metadata is temporarily unavailable: "
                + exception.Message;
        }

        OnPropertyChanged(nameof(AttachmentCountLabel));
        OnPropertyChanged(nameof(CanEditAttachments));
        OnPropertyChanged(nameof(CanUploadAttachments));
        RefreshSelectedEquipmentAttachments();
        RaiseAttachmentCommandState();
    }

    private void RefreshSelectedEquipmentAttachments()
    {
        var equipmentId = SelectedClientEquipment?.EquipmentId;
        Replace(
            SelectedEquipmentAttachments,
            equipmentId is > 0
                ? Attachments.Where(attachment =>
                    attachment.EquipmentId == equipmentId)
                : []);
        OnPropertyChanged(nameof(HasSelectedEquipmentAttachments));
        OnPropertyChanged(nameof(SelectedEquipmentAttachmentCountLabel));
    }

    private async Task UploadAttachmentsFromDialogAsync()
    {
        var allowed = ClientAttachmentStorageService.ParseAllowedExtensions(
            _attachmentConfiguration.AllowedExtensions);
        var patterns = string.Join(
            ";",
            allowed.OrderBy(extension => extension)
                .Select(extension => "*" + extension));
        var dialog = new OpenFileDialog
        {
            Title = $"Add attachments to {ClientName}",
            Multiselect = true,
            CheckFileExists = true,
            Filter = string.IsNullOrWhiteSpace(patterns)
                ? "All files (*.*)|*.*"
                : $"Allowed attachments ({patterns})|{patterns}|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(FindOwner()) == true)
        {
            await UploadAttachmentFilesAsync(dialog.FileNames);
        }
    }

    public async Task UploadAttachmentFilesAsync(IEnumerable<string> paths)
    {
        var files = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
        {
            return;
        }

        if (!CanUploadAttachments)
        {
            _dialogs.Error(
                "Attachments are not available",
                "Configure Attachment Storage in TechBench Server Manager before uploading files.");
            return;
        }

        SetAttachmentOperationRunning(true);
        var errors = new List<string>();
        var uploaded = 0;
        try
        {
            foreach (var path in files)
            {
                try
                {
                    StatusMessage = $"Uploading {Path.GetFileName(path)}...";
                    await Task.Run(() => _attachmentStorage.Upload(ClientId, path));
                    uploaded++;
                }
                catch (Exception exception)
                {
                    errors.Add($"{Path.GetFileName(path)}: {exception.Message}");
                }
            }
        }
        finally
        {
            SetAttachmentOperationRunning(false);
            RefreshAttachments();
        }

        StatusMessage = uploaded == 1
            ? "1 attachment uploaded."
            : $"{uploaded} attachments uploaded.";
        if (errors.Count > 0)
        {
            _dialogs.Error(
                "Some attachments were not uploaded",
                string.Join(Environment.NewLine, errors));
        }
    }

    private async Task PasteAttachmentAsync()
    {
        if (WpfClipboard.ContainsFileDropList())
        {
            var paths = WpfClipboard.GetFileDropList()
                .Cast<string>()
                .ToArray();
            await UploadAttachmentFilesAsync(paths);
            return;
        }

        if (!WpfClipboard.ContainsImage())
        {
            _dialogs.Info(
                "Nothing to paste",
                "Copy an image or one or more files, then choose Paste again.");
            return;
        }

        var image = WpfClipboard.GetImage();
        if (image is null)
        {
            return;
        }

        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "TechBench",
            "AttachmentPaste");
        Directory.CreateDirectory(temporaryDirectory);
        var temporaryPath = Path.Combine(
            temporaryDirectory,
            $"Clipboard-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None))
            {
                encoder.Save(stream);
            }

            await UploadAttachmentFilesAsync([temporaryPath]);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void EditAttachment(ClientInfoAttachment? attachment)
    {
        if (attachment is null)
        {
            return;
        }

        var values = ShowEditor(
            "Edit attachment",
            [
                new(
                    "category",
                    "Category",
                    attachment.Category,
                    true,
                    Options: AttachmentCategories),
                new("caption", "Caption / description", attachment.Caption)
            ]);
        if (values is null)
        {
            return;
        }

        ExecuteAttachmentSave(
            "Attachment metadata",
            () => _attachmentStorage.SaveMetadata(
                attachment,
                values["category"],
                values["caption"]));
    }

    private void LinkAttachmentToEquipment(ClientInfoAttachment? attachment)
    {
        if (attachment is null)
        {
            return;
        }

        const string notLinked = "Not linked";
        var options = new[] { notLinked }
            .Concat(Equipment.Select(FormatEquipmentLinkOption))
            .ToArray();
        var current = attachment.EquipmentId is > 0
            ? Equipment
                .Where(item => item.EquipmentId == attachment.EquipmentId)
                .Select(FormatEquipmentLinkOption)
                .FirstOrDefault() ?? notLinked
            : notLinked;
        var values = ShowEditor(
            "Link attachment to equipment",
            [
                new(
                    "equipment",
                    "Equipment",
                    current,
                    true,
                    Options: options)
            ],
            "The file remains in this client's attachment folder and will also appear with the selected equipment record.");
        if (values is null)
        {
            return;
        }

        var equipment = Equipment.FirstOrDefault(item => string.Equals(
            FormatEquipmentLinkOption(item),
            values["equipment"],
            StringComparison.Ordinal));
        ExecuteAttachmentSave(
            "Equipment link",
            () => _attachmentStorage.SetEquipmentLink(
                attachment,
                equipment?.EquipmentId));
    }

    private static string FormatEquipmentLinkOption(EquipmentItem equipment)
    {
        var identifier = !string.IsNullOrWhiteSpace(equipment.AssetTag)
            ? equipment.AssetTag
            : !string.IsNullOrWhiteSpace(equipment.SerialNumber)
                ? equipment.SerialNumber
                : $"ID {equipment.EquipmentId}";
        return $"{equipment.Name} - {identifier} [ID {equipment.EquipmentId}]";
    }

    private void OpenAttachment(ClientInfoAttachment? attachment)
    {
        if (attachment is null)
        {
            return;
        }

        try
        {
            var path = RequireAttachmentFile(attachment);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            StatusMessage = $"Opened {attachment.OriginalFileName}.";
        }
        catch (Exception exception)
        {
            ShowError("Attachment could not be opened", exception);
        }
    }

    private void CopyAttachment(ClientInfoAttachment? attachment)
    {
        if (attachment is null)
        {
            return;
        }

        try
        {
            var files = new StringCollection
            {
                RequireAttachmentFile(attachment)
            };
            WpfClipboard.SetFileDropList(files);
            StatusMessage =
                $"Copied {attachment.OriginalFileName} to the clipboard.";
        }
        catch (Exception exception)
        {
            ShowError("Attachment could not be copied", exception);
        }
    }

    private void DownloadAttachment(ClientInfoAttachment? attachment)
    {
        if (attachment is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save attachment copy",
            FileName = attachment.OriginalFileName,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(FindOwner()) != true)
        {
            return;
        }

        try
        {
            File.Copy(
                RequireAttachmentFile(attachment),
                dialog.FileName,
                overwrite: true);
            StatusMessage = $"Saved a copy of {attachment.OriginalFileName}.";
        }
        catch (Exception exception)
        {
            ShowError("Attachment copy could not be saved", exception);
        }
    }

    private void SetAttachmentArchived(
        ClientInfoAttachment? attachment,
        bool isArchived)
    {
        if (attachment is null)
        {
            return;
        }

        if (isArchived && !_dialogs.Confirm(
                "Archive attachment",
                $"Archive {attachment.OriginalFileName}? The file will be retained and can be restored later.",
                "Archive",
                "Cancel"))
        {
            return;
        }

        ExecuteAttachmentSave(
            isArchived ? "Attachment archive" : "Attachment restore",
            () => _attachmentStorage.SetArchived(attachment, isArchived));
    }

    private void ExecuteAttachmentSave(
        string operation,
        Func<ClientInfoAttachment> action)
    {
        try
        {
            var saved = action();
            RefreshAttachments();
            SelectedAttachment = Attachments.FirstOrDefault(
                attachment => attachment.AttachmentId == saved.AttachmentId);
            StatusMessage = operation + " completed.";
        }
        catch (SqlException exception) when (exception.Number == 52608)
        {
            RefreshAttachments();
            _dialogs.Error(
                "Another editor saved first",
                "The attachment changed on another workstation. The latest version has been loaded.");
        }
        catch (SqlException exception) when (exception.Number == 2812)
        {
            _dialogs.Error(
                operation + " requires the attachment-link update",
                "Install the current TechBench SQL deployment, then refresh Client Information and try again.");
        }
        catch (Exception exception)
        {
            ShowError(operation + " failed", exception);
        }
    }

    private string RequireAttachmentFile(ClientInfoAttachment attachment)
    {
        var path = _attachmentStorage.ResolvePath(attachment);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "The file is missing from the configured attachment share. "
                + "The SQL metadata was left unchanged.",
                path);
        }

        return path;
    }

    private void LoadAttachmentPreview(ClientInfoAttachment? attachment)
    {
        SelectedAttachmentPreview = null;
        if (attachment is null)
        {
            SelectedAttachmentPreviewMessage =
                "Select an attachment to preview it.";
            return;
        }

        if (!attachment.IsImage)
        {
            SelectedAttachmentPreviewMessage =
                $"{attachment.OriginalFileName} is a document. Choose Open to preview it in its normal application.";
            return;
        }

        try
        {
            var path = RequireAttachmentFile(attachment);
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 1000;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            SelectedAttachmentPreview = image;
            SelectedAttachmentPreviewMessage = string.Empty;
        }
        catch (Exception exception)
        {
            SelectedAttachmentPreviewMessage =
                "Preview unavailable: " + exception.Message;
        }
    }

    private void SetAttachmentOperationRunning(bool value)
    {
        _isAttachmentOperationRunning = value;
        OnPropertyChanged(nameof(CanEditAttachments));
        OnPropertyChanged(nameof(CanUploadAttachments));
        RaiseAttachmentCommandState();
    }

    private void RaiseAttachmentCommandState()
    {
        UploadAttachmentCommand.RaiseCanExecuteChanged();
        PasteAttachmentCommand.RaiseCanExecuteChanged();
        EditAttachmentCommand.RaiseCanExecuteChanged();
        LinkAttachmentToEquipmentCommand.RaiseCanExecuteChanged();
        OpenAttachmentCommand.RaiseCanExecuteChanged();
        CopyAttachmentCommand.RaiseCanExecuteChanged();
        DownloadAttachmentCommand.RaiseCanExecuteChanged();
        ArchiveAttachmentCommand.RaiseCanExecuteChanged();
        RestoreAttachmentCommand.RaiseCanExecuteChanged();
    }

    private void SaveProfile()
    {
        ExecuteSave(
            "profile",
            () =>
            {
                Profile = _repository.SaveClientInfoProfile(Profile with
                {
                    Summary = Summary,
                    ClientFolderPath = ClientFolderPath,
                    LegacyClientInfoSheetPath = LegacyClientInfoSheetPath,
                    ReviewStatus = ReviewStatus
                });
                StatusMessage = "Client profile saved.";
            });
    }

    private void OpenServerLink(string path, string label)
    {
        var normalizedPath = path.Trim();
        if (normalizedPath.Length == 0)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(normalizedPath)
            {
                UseShellExecute = true
            });
            StatusMessage = $"Opened the {label}.";
        }
        catch (Exception exception)
        {
            ShowError($"The {label} could not be opened", exception);
        }
    }

    private void EditLocation(ClientInfoLocation? current)
    {
        var values = ShowEditor(
            current is null ? "Add location" : "Edit location",
            [
                new("name", "Name", current?.Name ?? "", true),
                new("type", "Location type", current?.LocationType ?? ""),
                new("address1", "Address 1", current?.Address1 ?? ""),
                new("address2", "Address 2", current?.Address2 ?? ""),
                new("city", "City", current?.City ?? ""),
                new("state", "State / province", current?.StateProvince ?? ""),
                new("postal", "Postal code", current?.PostalCode ?? ""),
                new("phone", "Main phone", current?.MainPhone ?? ""),
                new("timezone", "Time zone ID", current?.TimeZoneId ?? ""),
                new("primary", "Primary location", YesNo(current?.IsPrimary ?? false),
                    Options: BooleanOptions),
                new("active", "Active", YesNo(current?.IsActive ?? true),
                    Options: BooleanOptions),
                new("review", "Review status", current?.ReviewStatus ?? "Unverified",
                    Options: ReviewStatuses)
            ]);
        if (values is null)
        {
            return;
        }

        ExecuteSave(
            "location",
            () =>
            {
                _repository.SaveClientInfoLocation((current ?? new ClientInfoLocation
                {
                    ClientId = ClientId
                }) with
                {
                    Name = values["name"],
                    LocationType = values["type"],
                    Address1 = values["address1"],
                    Address2 = values["address2"],
                    City = values["city"],
                    StateProvince = values["state"],
                    PostalCode = values["postal"],
                    MainPhone = values["phone"],
                    TimeZoneId = values["timezone"],
                    IsPrimary = IsYes(values["primary"]),
                    IsActive = IsYes(values["active"]),
                    ReviewStatus = values["review"]
                });
                Refresh();
            });
    }

    private void EditPerson(ClientInfoPerson? current)
    {
        var currentAdCredential = current?.AdCredential
            ?? (current is null ? null : FindUserAdCredential(current, Credentials));
        var currentMicrosoft365Credential = current?.Microsoft365Credential
            ?? (current is null
                ? null
                : FindUserMicrosoft365Credential(current, Credentials));
        var microsoft365UsesAdLogin =
            currentMicrosoft365Credential is null
            || !currentMicrosoft365Credential.IsActive;
        var locationOptions = new[] { "(None)" }
            .Concat(Locations.Select(item => item.Name))
            .ToArray();
        var values = ShowEditor(
            current is null ? "Add user" : "Edit user",
            [
                new("name", "Display name", current?.DisplayName ?? "", true),
                new("location", "Location", current?.LocationName ?? "(None)",
                    Options: locationOptions),
                new("role", "Role / department", current?.RoleDepartment ?? ""),
                new("adusername", "AD username",
                    !string.IsNullOrWhiteSpace(current?.AdUsername)
                        ? current.AdUsername
                        : currentAdCredential?.Username ?? "",
                    Tab: "AD login"),
                new(
                    "adpassword",
                    FindCurrentPassword(currentAdCredential) is null
                        ? "AD password"
                        : "AD password (leave blank to keep stored password)",
                    "",
                    IsSecret: true,
                    Tab: "AD login"),
                new("email", "Email", current?.Email ?? ""),
                new("has365", "Has Microsoft 365", YesNo(
                        current?.HasMicrosoft365 ?? false),
                    Options: BooleanOptions),
                new("license", "Microsoft 365 license",
                    Microsoft365LicenseCatalog.Normalize(
                        current?.Microsoft365License),
                    Options: Microsoft365LicenseCatalog.All,
                    AllowBlankSelection: true,
                    VisibleWhenKey: "has365",
                    VisibleWhenValue: "Yes"),
                new(
                    "m365sameasad",
                    "Microsoft 365 and AD use the same login",
                    YesNo(microsoft365UsesAdLogin),
                    Tab: "Microsoft 365 login",
                    IsBoolean: true),
                new(
                    "m365username",
                    "Microsoft 365 username",
                    !string.IsNullOrWhiteSpace(
                        currentMicrosoft365Credential?.Username)
                        ? currentMicrosoft365Credential.Username
                        : current?.Email ?? "",
                    Tab: "Microsoft 365 login",
                    VisibleWhenKey: "m365sameasad",
                    VisibleWhenValue: "No"),
                new(
                    "m365password",
                    FindCurrentPassword(currentMicrosoft365Credential) is null
                        ? "Microsoft 365 password"
                        : "Microsoft 365 password (leave blank to keep stored password)",
                    "",
                    IsSecret: true,
                    Tab: "Microsoft 365 login",
                    VisibleWhenKey: "m365sameasad",
                    VisibleWhenValue: "No"),
                new("pcname", "PC name", current?.PcName ?? ""),
                new("phone", "Phone", current?.Phone ?? ""),
                new("mobile", "Mobile phone", current?.MobilePhone ?? ""),
                new("type", "Contact type", current?.ContactType ?? ""),
                new("primary", "Primary contact", YesNo(current?.IsPrimary ?? false),
                    Options: BooleanOptions),
                new("active", "Active", YesNo(current?.IsActive ?? true),
                    Options: BooleanOptions),
                new("review", "Review status", current?.ReviewStatus ?? "Unverified",
                    Options: ReviewStatuses)
            ]);
        if (values is null)
        {
            return;
        }

        var location = Locations.FirstOrDefault(item => string.Equals(
            item.Name,
            values["location"],
            StringComparison.OrdinalIgnoreCase));
        ExecuteSave(
            "user",
            () =>
            {
                var saved = _repository.SaveClientInfoPerson((current ?? new ClientInfoPerson
                {
                    ClientId = ClientId
                }) with
                {
                    DisplayName = values["name"],
                    LocationId = location?.LocationId,
                    LocationName = location?.Name ?? "",
                    RoleDepartment = values["role"],
                    AdUsername = values["adusername"],
                    Email = values["email"],
                    HasMicrosoft365 = IsYes(values["has365"]),
                    Microsoft365License = IsYes(values["has365"])
                        ? values["license"]
                        : "",
                    PcName = values["pcname"],
                    Phone = values["phone"],
                    MobilePhone = values["mobile"],
                    ContactType = values["type"],
                    IsPrimary = IsYes(values["primary"]),
                    IsActive = IsYes(values["active"]),
                    ReviewStatus = values["review"]
                });
                SaveUserAdCredential(
                    saved,
                    currentAdCredential,
                    values["adpassword"]);
                SaveUserMicrosoft365Credential(
                    saved,
                    currentMicrosoft365Credential,
                    IsYes(values["m365sameasad"]),
                    values["m365username"],
                    values["m365password"]);
                Refresh();
            });
    }

    private void SaveUserAdCredential(
        ClientInfoPerson user,
        ClientInfoCredential? currentCredential,
        string password)
    {
        if (currentCredential is null
            && string.IsNullOrWhiteSpace(user.AdUsername)
            && string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var savedCredential = _repository.SaveClientInfoCredential(
            (currentCredential ?? new ClientInfoCredential
            {
                ClientId = ClientId,
                LocalKey = $"user-ad-{user.PersonId}"
            }) with
            {
                PersonId = user.PersonId,
                ResourceId = null,
                Name = $"{user.DisplayName} AD account",
                Category = UserAdCredentialCategory,
                Username = user.AdUsername,
                LoginUrl = "",
                Notes = "Active Directory sign-in for this client user.",
                IsActive = user.IsActive,
                ReviewStatus = user.ReviewStatus
            });

        if (!string.IsNullOrWhiteSpace(password))
        {
            _repository.SetClientInfoSecret(
                FindCurrentPassword(currentCredential) ?? new ClientInfoSecretSummary
                {
                    CredentialId = savedCredential.CredentialId,
                    SecretType = "Password",
                    SecretLabel = "AD password"
                },
                password,
                user.ReviewStatus.Equals(
                    "Verified",
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    private static ClientInfoCredential? FindUserAdCredential(
        ClientInfoPerson user,
        IEnumerable<ClientInfoCredential> credentials) =>
        credentials.FirstOrDefault(credential =>
            credential.PersonId == user.PersonId
            && credential.Category.Equals(
                UserAdCredentialCategory,
                StringComparison.OrdinalIgnoreCase))
        ?? credentials.FirstOrDefault(credential =>
            credential.PersonId == user.PersonId
            && credential.Category.Contains(
                "Active Directory",
                StringComparison.OrdinalIgnoreCase));

    private void SaveUserMicrosoft365Credential(
        ClientInfoPerson user,
        ClientInfoCredential? currentCredential,
        bool usesAdLogin,
        string username,
        string password)
    {
        if (!user.HasMicrosoft365 || usesAdLogin)
        {
            if (currentCredential is not null && currentCredential.IsActive)
            {
                _repository.SaveClientInfoCredential(currentCredential with
                {
                    Name = $"{user.DisplayName} Microsoft 365 account",
                    IsActive = false,
                    ReviewStatus = user.ReviewStatus
                });
            }

            return;
        }

        var savedCredential = _repository.SaveClientInfoCredential(
            (currentCredential ?? new ClientInfoCredential
            {
                ClientId = ClientId,
                LocalKey = $"user-m365-{user.PersonId}"
            }) with
            {
                PersonId = user.PersonId,
                ResourceId = null,
                Name = $"{user.DisplayName} Microsoft 365 account",
                Category = UserMicrosoft365CredentialCategory,
                Username = string.IsNullOrWhiteSpace(username)
                    ? user.Email
                    : username.Trim(),
                LoginUrl = "https://www.office.com",
                Notes = "Separate Microsoft 365 sign-in for this client user.",
                IsActive = user.IsActive,
                ReviewStatus = user.ReviewStatus
            });

        if (!string.IsNullOrWhiteSpace(password))
        {
            _repository.SetClientInfoSecret(
                FindCurrentPassword(currentCredential) ?? new ClientInfoSecretSummary
                {
                    CredentialId = savedCredential.CredentialId,
                    SecretType = "Password",
                    SecretLabel = "Microsoft 365 password"
                },
                password,
                user.ReviewStatus.Equals(
                    "Verified",
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    private static ClientInfoCredential? FindUserMicrosoft365Credential(
        ClientInfoPerson user,
        IEnumerable<ClientInfoCredential> credentials) =>
        credentials
            .Where(credential => credential.PersonId == user.PersonId)
            .Where(credential => credential.Category.Equals(
                UserMicrosoft365CredentialCategory,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(credential => credential.IsActive)
            .FirstOrDefault()
        ?? credentials
            .Where(credential => credential.PersonId == user.PersonId)
            .Where(credential => credential.Category.Contains(
                "Microsoft 365",
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(credential => credential.IsActive)
            .FirstOrDefault();

    private static ClientInfoSecretSummary? FindCurrentPassword(
        ClientInfoCredential? credential) => credential?.Secrets
        .FirstOrDefault(secret => secret.IsCurrent
            && secret.SecretType.Equals(
                "Password",
                StringComparison.OrdinalIgnoreCase))
        ?? credential?.Secrets.FirstOrDefault(secret => secret.IsCurrent);

    private void EditResource(
        ClientInfoResource? current,
        string? requestedCategory = null)
    {
        var editingCategory = current?.Category
            ?? ClientInfoResourceCategories.NormalizeCategory(requestedCategory);
        var standardFields = ClientInfoResourceFieldDefinitions.ForEditorCategory(
            editingCategory);
        var linkedCredentials = current is null
            ? Array.Empty<ClientInfoCredential>()
            : Credentials
                .Where(credential =>
                    credential.ResourceId == current.ResourceId)
                .OrderByDescending(credential => credential.IsActive)
                .ThenBy(credential => credential.Name)
                .ThenBy(credential => credential.CredentialId)
                .ToArray();
        var accessSlots = BuildResourceAccessSlots(
            editingCategory,
            linkedCredentials);
        var primaryCredentialIds = accessSlots
            .Where(slot => slot.Credential is not null)
            .Select(slot => slot.Credential!.CredentialId)
            .ToHashSet();
        var additionalCredentials = linkedCredentials
            .Where(credential => !primaryCredentialIds.Contains(
                credential.CredentialId))
            .ToArray();
        var locationOptions = new[] { "(None)" }
            .Concat(Locations.Select(item => item.Name))
            .ToArray();
        var editorFields = new List<ClientInfoEditField>
        {
            new("name", "Name", current?.Name ?? "", true),
            new(
                "type",
                "Type",
                current?.TypeLabel ?? "",
                true,
                Options: ClientInfoResourceFieldDefinitions.TypeOptionsForCategory(editingCategory),
                AllowCustomValue: true),
            new(
                "location",
                "Location",
                current?.LocationName ?? "(None)",
                Options: locationOptions),
            new("provider", "Provider", current?.Provider ?? ""),
            new(
                "address",
                ClientInfoResourceFieldDefinitions.AddressLabelForCategory(
                    editingCategory),
                current?.AddressOrUrl ?? "")
        };
        editorFields.AddRange(standardFields.Select(field =>
            new ClientInfoEditField(
                StandardFieldEditorKey(field.FieldKey),
                field.FieldLabel,
                current?.GetFieldValue(field.FieldKey) ?? "",
                IsMultiline: field.IsMultiline,
                Options: field.Options,
                AllowCustomValue: field.AllowCustomValue)));
        foreach (var slot in accessSlots)
        {
            var prefix = string.IsNullOrWhiteSpace(slot.Label)
                ? string.Empty
                : $"{slot.Label} ";
            editorFields.Add(new ClientInfoEditField(
                ResourceAccessEditorKey(slot.Key, "username"),
                $"{prefix}username",
                slot.Credential?.Username ?? ""));
            editorFields.Add(new ClientInfoEditField(
                ResourceAccessEditorKey(slot.Key, "password"),
                FindCurrentPassword(slot.Credential) is null
                    ? $"{prefix}password"
                    : $"{prefix}password (leave blank to keep stored password)",
                IsSecret: true));
        }
        editorFields.AddRange(
        [
            new("status", "Status", current?.Status ?? ""),
            new("notes", "Notes", current?.Notes ?? "", IsMultiline: true),
            new(
                "active",
                "Active",
                YesNo(current?.IsActive ?? true),
                Options: BooleanOptions),
            new(
                "review",
                "Review status",
                current?.ReviewStatus ?? "Unverified",
                Options: ReviewStatuses)
        ]);
        for (var index = 0; index < editorFields.Count; index++)
        {
            editorFields[index] = editorFields[index] with
            {
                Tab = "Details"
            };
        }

        for (var index = 0; index < additionalCredentials.Length; index++)
        {
            var credential = additionalCredentials[index];
            var label = additionalCredentials.Length == 1
                ? "Additional login"
                : $"Additional login {index + 1}";
            editorFields.AddRange(
            [
                new(
                    ResourceCredentialEditorKey(credential.CredentialId, "name"),
                    $"{label} name",
                    credential.Name,
                    IsRequired: true,
                    Tab: "Passwords"),
                new(
                    ResourceCredentialEditorKey(credential.CredentialId, "username"),
                    $"{label} username",
                    credential.Username,
                    Tab: "Passwords"),
                new(
                    ResourceCredentialEditorKey(credential.CredentialId, "login_url"),
                    $"{label} login URL",
                    credential.LoginUrl,
                    Tab: "Passwords"),
                new(
                    ResourceCredentialEditorKey(credential.CredentialId, "password"),
                    FindCurrentPassword(credential) is null
                        ? $"{label} password"
                        : $"{label} password (leave blank to keep stored password)",
                    IsSecret: true,
                    Tab: "Passwords")
            ]);
        }

        editorFields.AddRange(
        [
            new(
                "access_new_name",
                "New additional login name (optional)",
                Tab: "Passwords"),
            new(
                "access_new_username",
                "New additional login username (optional)",
                Tab: "Passwords"),
            new(
                "access_new_login_url",
                "New additional login URL (optional)",
                current?.AddressOrUrl ?? "",
                Tab: "Passwords"),
            new(
                "access_new_password",
                "New additional login password (optional)",
                IsSecret: true,
                Tab: "Passwords")
        ]);

        var values = ShowEditor(
            current is null ? $"Add {editingCategory}" : $"Edit {editingCategory}",
            editorFields,
            ClientInfoResourceFieldDefinitions.EditorDescriptionForCategory(editingCategory));
        if (values is null)
        {
            return;
        }

        var location = Locations.FirstOrDefault(item => string.Equals(
            item.Name,
            values["location"],
            StringComparison.OrdinalIgnoreCase));
        ExecuteSave(
            "technology or service",
            () =>
            {
                var savedResource = _repository.SaveClientInfoResource(
                    (current ?? new ClientInfoResource
                {
                    ClientId = ClientId
                }) with
                {
                    Name = values["name"],
                    ResourceType = ClientInfoResourceCategories.Encode(
                        editingCategory,
                        values["type"]),
                    LocationId = location?.LocationId,
                    LocationName = location?.Name ?? "",
                    Provider = values["provider"],
                    AddressOrUrl = values["address"],
                    Status = values["status"],
                    Notes = values["notes"],
                    IsActive = IsYes(values["active"]),
                    ReviewStatus = values["review"]
                });
                foreach (var definition in standardFields)
                {
                    var existing = current?.Fields.FirstOrDefault(field =>
                        string.Equals(
                            field.FieldKey,
                            definition.FieldKey,
                            StringComparison.OrdinalIgnoreCase));
                    var value = values[StandardFieldEditorKey(
                        definition.FieldKey)];
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        if (existing is not null)
                        {
                            _repository.DeleteClientInfoResourceField(existing);
                        }

                        continue;
                    }

                    _repository.SaveClientInfoResourceField(
                        (existing ?? new ClientInfoResourceField
                        {
                            ResourceId = savedResource.ResourceId,
                            FieldKey = definition.FieldKey
                        }) with
                        {
                            ResourceId = savedResource.ResourceId,
                            FieldLabel = definition.FieldLabel,
                            ValueText = value,
                            ValueType = definition.ValueType,
                            SortOrder = definition.SortOrder
                        });
                }

                SaveResourceAccessEdits(
                    savedResource,
                    editingCategory,
                    accessSlots,
                    additionalCredentials,
                    values);

                Refresh();
            });
    }

    private void SaveResourceAccessEdits(
        ClientInfoResource savedResource,
        string editingCategory,
        IReadOnlyList<ResourceAccessSlot> accessSlots,
        IReadOnlyList<ClientInfoCredential> additionalCredentials,
        IReadOnlyDictionary<string, string> values)
    {
        ClientInfoCredential? savedCredential = null;
        try
        {
            foreach (var slot in accessSlots)
            {
                var username = values[
                    ResourceAccessEditorKey(slot.Key, "username")].Trim();
                var password = values[
                    ResourceAccessEditorKey(slot.Key, "password")];
                if (slot.Credential is null
                    && string.IsNullOrWhiteSpace(username)
                    && string.IsNullOrWhiteSpace(password))
                {
                    continue;
                }

                savedCredential = _repository.SaveClientInfoCredential(
                    slot.Credential is null
                        ? new ClientInfoCredential
                        {
                            ClientId = ClientId,
                            ResourceId = savedResource.ResourceId,
                            LocalKey = $"resource-access-{slot.Key}-{savedResource.ResourceId}",
                            Name = $"{savedResource.Name.Trim()} "
                                + (slot.Key.Equals(
                                    "primary",
                                    StringComparison.OrdinalIgnoreCase)
                                    ? "login"
                                    : slot.Key),
                            Category = string.IsNullOrWhiteSpace(
                                savedResource.TypeLabel)
                                ? editingCategory
                                : savedResource.TypeLabel,
                            Username = username,
                            LoginUrl = savedResource.AddressOrUrl,
                            Notes = $"Access for {savedResource.Name}.",
                            IsActive = savedResource.IsActive,
                            ReviewStatus = savedResource.ReviewStatus
                        }
                        : slot.Credential with
                        {
                            ResourceId = savedResource.ResourceId,
                            PersonId = null,
                            Username = username,
                            IsActive = savedResource.IsActive,
                            ReviewStatus = savedResource.ReviewStatus
                        });
                if (!string.IsNullOrWhiteSpace(password))
                {
                    _repository.SetClientInfoSecret(
                        FindCurrentPassword(slot.Credential)
                        ?? new ClientInfoSecretSummary
                        {
                            CredentialId = savedCredential.CredentialId,
                            SecretType = "Password",
                            SecretLabel = "Password"
                        },
                        password,
                        savedResource.ReviewStatus.Equals(
                            "Verified",
                            StringComparison.OrdinalIgnoreCase));
                }
            }

            foreach (var credential in additionalCredentials)
            {
                var nameKey = ResourceCredentialEditorKey(
                    credential.CredentialId,
                    "name");
                var usernameKey = ResourceCredentialEditorKey(
                    credential.CredentialId,
                    "username");
                var loginUrlKey = ResourceCredentialEditorKey(
                    credential.CredentialId,
                    "login_url");
                var passwordKey = ResourceCredentialEditorKey(
                    credential.CredentialId,
                    "password");
                savedCredential = _repository.SaveClientInfoCredential(
                    credential with
                    {
                        ResourceId = savedResource.ResourceId,
                        PersonId = null,
                        Name = values[nameKey].Trim(),
                        Username = values[usernameKey].Trim(),
                        LoginUrl = values[loginUrlKey].Trim()
                    });
                var password = values[passwordKey];
                if (!string.IsNullOrWhiteSpace(password))
                {
                    _repository.SetClientInfoSecret(
                        FindCurrentPassword(credential)
                        ?? new ClientInfoSecretSummary
                        {
                            CredentialId = savedCredential.CredentialId,
                            SecretType = "Password",
                            SecretLabel = "Password"
                        },
                        password,
                        savedResource.ReviewStatus.Equals(
                            "Verified",
                            StringComparison.OrdinalIgnoreCase));
                }
            }

            var newName = values["access_new_name"].Trim();
            var newUsername = values["access_new_username"].Trim();
            var newLoginUrl = values["access_new_login_url"].Trim();
            var newPassword = values["access_new_password"];
            if (!string.IsNullOrWhiteSpace(newName)
                || !string.IsNullOrWhiteSpace(newUsername)
                || !string.IsNullOrWhiteSpace(newPassword))
            {
                savedCredential = _repository.SaveClientInfoCredential(
                    new ClientInfoCredential
                    {
                        ClientId = ClientId,
                        ResourceId = savedResource.ResourceId,
                        LocalKey = $"resource-access-{savedResource.ResourceId}-{Guid.NewGuid():N}",
                        Name = string.IsNullOrWhiteSpace(newName)
                            ? $"{savedResource.Name} login"
                            : newName,
                        Category = editingCategory,
                        Username = newUsername,
                        LoginUrl = string.IsNullOrWhiteSpace(newLoginUrl)
                            ? savedResource.AddressOrUrl
                            : newLoginUrl,
                        Notes = $"Access for {savedResource.Name}.",
                        IsActive = savedResource.IsActive,
                        ReviewStatus = savedResource.ReviewStatus
                    });
                if (!string.IsNullOrWhiteSpace(newPassword))
                {
                    _repository.SetClientInfoSecret(
                        new ClientInfoSecretSummary
                        {
                            CredentialId = savedCredential.CredentialId,
                            SecretType = "Password",
                            SecretLabel = "Password"
                        },
                        newPassword,
                        savedResource.ReviewStatus.Equals(
                            "Verified",
                            StringComparison.OrdinalIgnoreCase));
                }
            }
        }
        catch (Exception exception)
        {
            throw new ClientInfoResourceAccessSaveException(
                (savedCredential is null
                    ? "The system or service was saved, but its linked login was not. "
                    : "The system or service and login details were saved, but a password was not. ")
                + "Open the same system again or use Passwords to finish the login."
                + $"\n\nDetails: {exception.Message}",
                exception);
        }
    }

    private static ResourceAccessSlot[] BuildResourceAccessSlots(
        string editingCategory,
        IReadOnlyList<ClientInfoCredential> linkedCredentials)
    {
        if (!editingCategory.Equals(
                ClientInfoResourceCategories.ConnectionInternet,
                StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new ResourceAccessSlot(
                    "primary",
                    "",
                    linkedCredentials.FirstOrDefault())
            ];
        }

        var status = linkedCredentials.FirstOrDefault(credential =>
            ResourceCredentialContains(credential, "status"));
        var admin = linkedCredentials.FirstOrDefault(credential =>
            credential.CredentialId != status?.CredentialId
            && ResourceCredentialContains(
                credential,
                "watchguard admin",
                "firebox admin",
                "firewall admin",
                "administrator",
                "admin")
            && !ResourceCredentialContains(
                credential,
                "firebox db",
                "firebox database",
                "authpoint",
                "ssl vpn",
                "sslvpn",
                "cloud",
                "ad auth"));
        return
        [
            new ResourceAccessSlot(
                "status",
                "Status",
                status),
            new ResourceAccessSlot(
                "admin",
                "Admin",
                admin)
        ];
    }

    private static bool ResourceCredentialContains(
        ClientInfoCredential credential,
        params string[] terms)
    {
        var metadata = $"{credential.Name} {credential.Category} {credential.Username}";
        return terms.Any(term => metadata.Contains(
            term,
            StringComparison.OrdinalIgnoreCase));
    }

    private void SaveNewResourceAccess(
        ClientInfoResource savedResource,
        string editingCategory,
        string username,
        string password)
    {
        if (string.IsNullOrWhiteSpace(username)
            && string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        ClientInfoCredential? savedCredential = null;
        try
        {
            savedCredential = SaveLinkedResourceAccess(
                ClientId,
                savedResource,
                editingCategory,
                username,
                password,
                credential => savedCredential =
                    _repository.SaveClientInfoCredential(credential),
                _repository.SetClientInfoSecret);
        }
        catch (Exception exception)
        {
            throw new ClientInfoResourceAccessSaveException(
                (savedCredential is null
                    ? "The system or service was saved, but its username and password were not. "
                      + "Open it from Passwords to finish linking the login."
                    : "The system or service and linked login were saved, but its password was not. "
                      + "Open the linked login from Passwords to finish saving it.")
                + $"\n\nDetails: {exception.Message}",
                exception);
        }
    }

    internal static ClientInfoCredential? SaveLinkedResourceAccess(
        int clientId,
        ClientInfoResource savedResource,
        string editingCategory,
        string username,
        string password,
        Func<ClientInfoCredential, ClientInfoCredential> saveCredential,
        Func<ClientInfoSecretSummary, string, bool, ClientInfoSecretSummary>
            saveSecret)
    {
        ArgumentNullException.ThrowIfNull(savedResource);
        ArgumentNullException.ThrowIfNull(saveCredential);
        ArgumentNullException.ThrowIfNull(saveSecret);
        if (string.IsNullOrWhiteSpace(username)
            && string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        var savedCredential = saveCredential(BuildLinkedResourceCredential(
            clientId,
            savedResource,
            editingCategory,
            username));
        if (!string.IsNullOrWhiteSpace(password))
        {
            saveSecret(
                new ClientInfoSecretSummary
                {
                    CredentialId = savedCredential.CredentialId,
                    SecretType = "Password",
                    SecretLabel = "Password"
                },
                password,
                savedResource.ReviewStatus.Equals(
                    "Verified",
                    StringComparison.OrdinalIgnoreCase));
        }

        return savedCredential;
    }

    internal static ClientInfoCredential BuildLinkedResourceCredential(
        int clientId,
        ClientInfoResource savedResource,
        string editingCategory,
        string username)
    {
        ArgumentNullException.ThrowIfNull(savedResource);
        var typeLabel = savedResource.TypeLabel.Trim();
        return new ClientInfoCredential
        {
            ClientId = clientId,
            ResourceId = savedResource.ResourceId,
            LocalKey = $"resource-primary-access-{savedResource.ResourceId}",
            Name = $"{savedResource.Name.Trim()} login",
            Category = string.IsNullOrWhiteSpace(typeLabel)
                ? editingCategory.Trim()
                : typeLabel,
            Username = username.Trim(),
            LoginUrl = savedResource.AddressOrUrl.Trim(),
            IsActive = savedResource.IsActive,
            ReviewStatus = savedResource.ReviewStatus
        };
    }

    private void MoveResource(ClientInfoResource? resource)
    {
        if (resource is null)
        {
            return;
        }

        var values = ShowEditor(
            "Move technology or service",
            [
                new(
                    "category",
                    "Move to section",
                    resource.Category,
                    true,
                    Options: ResourceCategories)
            ],
            $"Move {resource.Name} without changing its details or custom fields.");
        if (values is null
            || string.Equals(values["category"], resource.Category, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ExecuteSave(
            "technology or service",
            () =>
            {
                _repository.SaveClientInfoResource(resource with
                {
                    ResourceType = ClientInfoResourceCategories.Encode(
                        values["category"],
                        resource.TypeLabel)
                });
                Refresh();
            });
    }

    private void ManageResourceFields(ClientInfoResource? resource)
    {
        if (resource is null)
        {
            return;
        }

        var editor = new ClientInfoResourceFieldsWindow(
            resource,
            _repository,
            _dialogs)
        {
            Owner = FindOwner()
        };
        editor.ShowDialog();
        Refresh();
        StatusMessage = "Custom resource fields refreshed.";
    }

    private void RefreshResourceGroups()
    {
        var allResourceIds = Resources
            .Select(resource => resource.ResourceId)
            .ToHashSet();
        var groups = new[]
        {
            ServerInfrastructureGroup,
            ConnectionInternetGroup,
            WifiGroup,
            ApplicationsCloudGroup,
            DomainsEmailGroup,
            BackupGroup,
            SecurityGroup,
            VendorsServicesGroup,
            NeedsSortingGroup
        };
        foreach (var group in groups)
        {
            var resources = Resources.Where(resource => string.Equals(
                    resource.Category,
                    group.CategoryName,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var standaloneCredentials = Credentials.Where(credential =>
                    (!credential.ResourceId.HasValue
                     || !allResourceIds.Contains(credential.ResourceId.Value))
                    && !credential.PersonId.HasValue
                    && string.Equals(
                        ClientInfoResourceCategories.ClassifyCredential(credential),
                        group.CategoryName,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
            group.Replace(resources, Credentials, standaloneCredentials);
        }

        var categorySections = groups
            .Where(group => !ReferenceEquals(group, NeedsSortingGroup))
            .SelectMany(group => group.AllOverviewSections)
            .Where(section => section.Title is not
                ("Microsoft 365"
                or "ESET"
                or "Barracuda"
                or "Email Security"
                or "Core infrastructure"
                or "Other backup"
                or "Other security"
                or "Remote Access"
                or "Veeam"
                or "Vendors & services"));
        Replace(
            QuickReferenceSections,
            categorySections
                .Append(ClientInfoCategoryOverviewBuilder.BuildCloudAccounts(
                    Credentials.ToArray()))
                .OrderBy(section => QuickReferencePriority(section.Title))
                .ThenBy(section => section.Title));
        OnPropertyChanged(nameof(HasQuickReferenceSections));
        OnPropertyChanged(nameof(QuickReferenceCountLabel));
    }

    private static int QuickReferencePriority(string title) => title switch
    {
        "WiFi" => 10,
        "Connection" => 20,
        "WatchGuard" => 20,
        "Domain & AD" => 30,
        "Cloud Accounts" => 40,
        "ILO" => 70,
        "UPS" => 80,
        "Internet & circuits" => 90,
        _ => 200
    };

    private void EditCredential(ClientInfoCredential? current)
    {
        var resourceOptions = new[] { "(None)" }
            .Concat(Resources.Select(item => item.Name))
            .ToArray();
        var personOptions = new[] { "(None)" }
            .Concat(People.Select(item => item.DisplayName))
            .ToArray();
        var currentResource = Resources.FirstOrDefault(item =>
            item.ResourceId == current?.ResourceId)?.Name ?? "(None)";
        var currentPerson = People.FirstOrDefault(item =>
            item.PersonId == current?.PersonId)?.DisplayName ?? "(None)";
        var values = ShowEditor(
            current is null ? "Add credential" : "Edit credential",
            [
                new("name", "Name", current?.Name ?? "", true),
                new("category", "Category", current?.Category ?? ""),
                new("resource", "System / service", currentResource,
                    Options: resourceOptions),
                new("person", "User", currentPerson, Options: personOptions),
                new("username", "Username", current?.Username ?? ""),
                new("url", "Login URL", current?.LoginUrl ?? ""),
                new("notes", "Notes", current?.Notes ?? "", IsMultiline: true),
                new("active", "Active", YesNo(current?.IsActive ?? true),
                    Options: BooleanOptions),
                new("review", "Review status", current?.ReviewStatus ?? "Unverified",
                    Options: ReviewStatuses)
            ]);
        if (values is null)
        {
            return;
        }

        var resource = Resources.FirstOrDefault(item => string.Equals(
            item.Name,
            values["resource"],
            StringComparison.OrdinalIgnoreCase));
        var person = People.FirstOrDefault(item => string.Equals(
            item.DisplayName,
            values["person"],
            StringComparison.OrdinalIgnoreCase));
        ExecuteSave(
            "credential",
            () =>
            {
                _repository.SaveClientInfoCredential((current ?? new ClientInfoCredential
                {
                    ClientId = ClientId
                }) with
                {
                    Name = values["name"],
                    Category = values["category"],
                    ResourceId = resource?.ResourceId,
                    PersonId = person?.PersonId,
                    Username = values["username"],
                    LoginUrl = values["url"],
                    Notes = values["notes"],
                    IsActive = IsYes(values["active"]),
                    ReviewStatus = values["review"]
                });
                Refresh();
            });
    }

    private void EditFact(ClientInfoFact? current)
    {
        var values = ShowEditor(
            current is null ? "Add other information" : "Edit other information",
            [
                new("section", "Section", current?.SectionName ?? "Other", true),
                new("label", "Field label", current?.FieldLabel ?? "", true),
                new("value", "Value", current?.ValueText ?? "", IsMultiline: true),
                new("type", "Value type", current?.ValueType ?? "Text"),
                new("sort", "Sort order", (current?.SortOrder ?? 0).ToString()),
                new("active", "Active", YesNo(current?.IsActive ?? true),
                    Options: BooleanOptions),
                new("review", "Review status", current?.ReviewStatus ?? "Unverified",
                    Options: ReviewStatuses)
            ]);
        if (values is null)
        {
            return;
        }

        _ = int.TryParse(values["sort"], out var sortOrder);
        ExecuteSave(
            "other information",
            () =>
            {
                _repository.SaveClientInfoFact((current ?? new ClientInfoFact
                {
                    ClientId = ClientId
                }) with
                {
                    SectionName = values["section"],
                    FieldLabel = values["label"],
                    ValueText = values["value"],
                    ValueType = values["type"],
                    SortOrder = sortOrder,
                    IsActive = IsYes(values["active"]),
                    ReviewStatus = values["review"]
                });
                Refresh();
            });
    }

    private void EditSecret(ClientInfoSecretSummary? current)
    {
        if (SelectedCredential is null)
        {
            return;
        }

        var values = ShowEditor(
            current is null ? "Add password or secret" : "Replace password or secret",
            [
                new("type", "Secret type", current?.SecretType ?? "Password", true),
                new("label", "Label", current?.SecretLabel ?? "Password", true),
                new("value", "Password / secret", "", true, IsSecret: true),
                new("verified", "Verified now", "No", Options: BooleanOptions)
            ]);
        if (values is null)
        {
            return;
        }

        ExecuteSave(
            "secret",
            () =>
            {
                _repository.SetClientInfoSecret(
                    current ?? new ClientInfoSecretSummary
                    {
                        CredentialId = SelectedCredential.CredentialId
                    },
                    values["value"],
                    IsYes(values["verified"]));
                Refresh();
            });
    }

    private void ToggleSecret(ClientInfoSecretSummary? secret)
    {
        if (secret is null)
        {
            return;
        }

        if (secret.IsRevealedInline)
        {
            secret.HideInline();
            StatusMessage = $"Hidden {secret.SecretLabel}.";
            return;
        }

        try
        {
            var revealed = ResolveSecret(secret, forClipboard: false)
                ?? throw new InvalidOperationException(
                    "The secret is no longer available.");
            secret.RevealInline(revealed.SecretValue);
            StatusMessage = IsDemo
                ? $"Showing sample {secret.SecretLabel} inline; no real credential was accessed."
                : $"Showing {secret.SecretLabel} inline; access was audited.";
        }
        catch (Exception exception)
        {
            ShowError("Secret could not be revealed", exception);
        }
    }

    private void CopySecret(ClientInfoSecretSummary? secret)
    {
        if (secret is null)
        {
            return;
        }

        try
        {
            var revealed = ResolveSecret(secret, forClipboard: true)
                ?? throw new InvalidOperationException(
                    "The secret is no longer available.");
            WpfClipboard.SetText(revealed.SecretValue);
            StatusMessage = IsDemo
                ? $"Copied sample {secret.SecretLabel}; no real credential was accessed."
                : $"Copied {secret.SecretLabel}; clipboard access was audited.";
        }
        catch (Exception exception)
        {
            ShowError("Secret could not be copied", exception);
        }
    }

    public void ClearRevealedSecrets()
    {
        foreach (var secret in Credentials.SelectMany(credential => credential.Secrets))
        {
            secret.HideInline();
        }
    }

    private RevealedClientInfoSecret? ResolveSecret(
        ClientInfoSecretSummary secret,
        bool forClipboard)
    {
        if (_demoData is null)
        {
            return _repository.RevealClientInfoSecret(
                secret.SecretId,
                forClipboard);
        }

        if (!_demoData.SecretValues.TryGetValue(
                secret.SecretId,
                out var secretValue))
        {
            return null;
        }

        var credential = _demoData.Snapshot.Credentials.FirstOrDefault(item =>
            item.CredentialId == secret.CredentialId);
        return new RevealedClientInfoSecret
        {
            SecretId = secret.SecretId,
            CredentialId = secret.CredentialId,
            ClientId = ClientId,
            CredentialName = credential?.Name ?? "Demo credential",
            SecretType = secret.SecretType,
            SecretLabel = secret.SecretLabel,
            SecretValue = secretValue,
            RowVersion = secret.RowVersion
        };
    }

    private void CreateTemplate()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save this client's migration workbook",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            FileName = SafeFileName($"{ClientName} - Migration Workbook.xlsx"),
            AddExtension = true,
            DefaultExt = ".xlsx"
        };
        if (dialog.ShowDialog(FindOwner()) != true)
        {
            return;
        }

        try
        {
            var fireDrillFieldLabels = FindFireDrillFieldLabels();
            _workbooks.CreateTemplate(
                dialog.FileName,
                ClientId,
                ClientName,
                fireDrillFieldLabels);
            StatusMessage =
                $"Created the migration workbook for internal client ID {ClientId}. The FireDrill tab contains {(fireDrillFieldLabels.Count > 0 ? fireDrillFieldLabels.Count : 10)} matching credential column(s). Copy the cleaned client information into it, then return here to import it.";
        }
        catch (Exception exception)
        {
            ShowError("Template could not be created", exception);
        }
    }

    private IReadOnlyList<string> FindFireDrillFieldLabels()
    {
        try
        {
            var matches = _repository.SearchFireDrillCredentials(ClientName);
            var match = matches.FirstOrDefault(item => string.Equals(
                            item.ClientName.Trim(),
                            ClientName.Trim(),
                            StringComparison.OrdinalIgnoreCase))
                        ?? (matches.Count == 1 ? matches[0] : null);
            return match?.Fields
                       .OrderBy(field => field.SortOrder)
                       .Select(field => field.Label)
                       .Where(label => !string.IsNullOrWhiteSpace(label))
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .ToArray()
                   ?? [];
        }
        catch
        {
            // Workbook creation remains available when FireDrill is unavailable;
            // the service supplies the exact legacy FireDrill columns instead.
            return [];
        }
    }

    private void ImportWorkbook()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the completed client migration workbook",
            Filter = "Excel workbooks (*.xlsx;*.xls)|*.xlsx;*.xls",
            Multiselect = false
        };
        if (dialog.ShowDialog(FindOwner()) != true)
        {
            return;
        }

        try
        {
            var package = _workbooks.Read(dialog.FileName);
            if (package.ClientId != ClientId)
            {
                throw new InvalidDataException(
                    $"This workbook belongs to internal client ID {package.ClientId}, "
                    + $"not the open client ID {ClientId}.");
            }

            if (!string.Equals(
                    package.ClientName.Trim(),
                    ClientName.Trim(),
                    StringComparison.OrdinalIgnoreCase)
                && !_dialogs.Confirm(
                    "Client name differs",
                    $"The workbook says '{package.ClientName}', while TechBench says "
                    + $"'{ClientName}'. The internal ID matches. Continue with the import?",
                    "Continue",
                    "Cancel"))
            {
                return;
            }

            var batch = _repository.ImportClientInfoWorkbook(package);
            Refresh();
            SelectedImportBatch = _repository.GetClientInfoImportBatch(
                batch.BatchId);
            StatusMessage =
                $"Imported a review copy containing {batch.RecordCount} records and {batch.SecretCount} passwords or secrets. "
                + "Client Information has not changed yet.";
        }
        catch (Exception exception)
        {
            ShowError("Workbook could not be staged", exception);
        }
    }

    private void ReloadSelectedImport()
    {
        if (SelectedImportBatch is null)
        {
            return;
        }

        try
        {
            if (SelectedImportBatch.State is
                "Draft" or "Parsed" or "Validated" or "InReview" or "ValidationFailed")
            {
                var validated = _repository.ValidateClientInfoImport(
                    SelectedImportBatch.BatchId);
                SelectedImportBatch = validated.State is
                    "InReview" or "ValidationFailed" or "Validated"
                    ? _repository.CompareClientInfoImportToFireDrill(
                        validated.BatchId)
                    : validated;
                StatusMessage = "Workbook validation and comparison checks refreshed.";
            }
            else
            {
                SelectedImportBatch = _repository.GetClientInfoImportBatch(
                    SelectedImportBatch.BatchId);
                StatusMessage = "Import review refreshed.";
            }
        }
        catch (Exception exception)
        {
            ShowError("Import review could not be refreshed", exception);
        }
    }

    private void CompareSelectedImport()
    {
        if (SelectedImportBatch is null)
        {
            return;
        }

        try
        {
            SelectedImportBatch =
                _repository.CompareClientInfoImportToFireDrill(
                    SelectedImportBatch.BatchId);
            StatusMessage =
                $"FireDrill comparison: {SelectedImportBatch.SecretMatchCount} match, "
                + $"{SelectedImportBatch.SecretMismatchCount} mismatch, "
                + $"{SelectedImportBatch.SecretWorkbookOnlyCount} workbook-only.";
        }
        catch (Exception exception)
        {
            ShowError("FireDrill comparison could not be completed", exception);
        }
    }

    private void AcceptUnverifiedImport()
    {
        if (SelectedImportBatch is null)
        {
            return;
        }

        var count = SelectedImportBatch.Issues.Count(issue =>
            !issue.IsResolved
            && issue.IssueCode.Equals(
                "UNVERIFIED_RECORD",
                StringComparison.OrdinalIgnoreCase));
        if (count == 0
            || !_dialogs.Confirm(
                "Accept remaining rows",
                $"Mark {count} remaining unverified record(s) as Keep as-is? "
                + "They will be accepted for import without being marked as independently verified.",
                "Accept as Keep as-is",
                "Cancel"))
        {
            return;
        }

        try
        {
            _repository.AcceptClientInfoImportUnverified(SelectedImportBatch);
            var validated = _repository.ValidateClientInfoImport(
                SelectedImportBatch.BatchId);
            SelectedImportBatch = validated.State is
                "InReview" or "ValidationFailed" or "Validated"
                ? _repository.CompareClientInfoImportToFireDrill(
                    validated.BatchId)
                : validated;
            StatusMessage =
                $"Accepted {count} remaining record(s) as Keep as-is. "
                + "The workbook can now be approved if no other review warnings remain.";
        }
        catch (Exception exception)
        {
            ShowError("Unverified rows could not be accepted", exception);
            ReloadSelectedImport();
        }
    }

    private void DiscardImport()
    {
        if (SelectedImportBatch is null
            || !_dialogs.Confirm(
                "Discard workbook review",
                "Discard this staged workbook review? Client Information will not be changed.",
                "Discard Review",
                "Keep Review"))
        {
            return;
        }

        try
        {
            _repository.DiscardClientInfoImport(SelectedImportBatch);
            Refresh();
            StatusMessage = "Workbook review discarded. Client Information was not changed.";
        }
        catch (Exception exception)
        {
            ShowError("Workbook review could not be discarded", exception);
            ReloadSelectedImport();
        }
    }

    private void ApproveImport()
    {
        if (SelectedImportBatch is null
            || !_dialogs.Confirm(
                "Approve import",
                "Approve this reviewed workbook? This does not change Client Information yet.",
                "Approve",
                "Cancel"))
        {
            return;
        }

        try
        {
            SelectedImportBatch = _repository.ApproveClientInfoImport(
                SelectedImportBatch);
            StatusMessage = "Workbook approved and ready to add to Client Information.";
        }
        catch (Exception exception)
        {
            ShowError("Import could not be approved", exception);
            ReloadSelectedImport();
        }
    }

    private void PromoteImport()
    {
        if (SelectedImportBatch is null
            || !_dialogs.Confirm(
                "Add workbook to Client Information",
                "Add this approved workbook to the client's SQL information? FireDrill remains unchanged during the beta.",
                "Add to Client Information",
                "Cancel"))
        {
            return;
        }

        try
        {
            _repository.PromoteClientInfoImport(SelectedImportBatch);
            Refresh();
            StatusMessage =
                "Complete. Client Information now contains the reviewed workbook data.";
        }
        catch (Exception exception)
        {
            ShowError("Import could not be promoted", exception);
            ReloadSelectedImport();
        }
    }

    private IReadOnlyDictionary<string, string>? ShowEditor(
        string title,
        IReadOnlyList<ClientInfoEditField> fields,
        string? description = null)
    {
        var editor = new ClientInfoRecordEditorWindow(title, fields, description)
        {
            Owner = FindOwner()
        };
        return editor.ShowDialog() == true ? editor.Values : null;
    }

    private void ExecuteSave(string recordName, Action action)
    {
        try
        {
            action();
            StatusMessage = $"{recordName} saved.";
        }
        catch (SqlException exception) when (IsConcurrencyConflict(exception))
        {
            Refresh();
            _dialogs.Error(
                "Another editor saved first",
                $"The {recordName} changed on another workstation. "
                + "The latest SQL version has been loaded; review it and try again.");
        }
        catch (ClientInfoResourceAccessSaveException exception)
        {
            Refresh();
            StatusMessage = "System or service saved; login needs attention.";
            _dialogs.Error(
                "System saved; login could not be completed",
                exception.Message);
        }
        catch (Exception exception)
        {
            ShowError($"{recordName} could not be saved", exception);
        }
    }

    private static bool IsConcurrencyConflict(SqlException exception) =>
        exception.Number is 52324 or 52332 or 52343 or 52354 or 52363
            or 52358 or 52359 or 52375 or 52384 or 52441 or 52453 or 52460;

    private sealed class ClientInfoResourceAccessSaveException(
        string message,
        Exception innerException)
        : Exception(message, innerException);

    private void ShowError(string title, Exception exception)
    {
        StatusMessage = title + ".";
        _dialogs.Error(title, exception.Message);
    }

    private Window? FindOwner() =>
        WpfApplication.Current.Windows
            .OfType<ClientInfoBetaWindow>()
            .FirstOrDefault(window => ReferenceEquals(window.DataContext, this))
        ?? WpfApplication.Current.Windows
            .OfType<ClientInfoImportWindow>()
            .FirstOrDefault(window => ReferenceEquals(window.DataContext, this))
        ?? WpfApplication.Current.MainWindow;

    private static void Replace<T>(
        ObservableCollection<T> collection,
        IEnumerable<T> values)
    {
        collection.Clear();
        foreach (var value in values)
        {
            collection.Add(value);
        }
    }

    private static string YesNo(bool value) => value ? "Yes" : "No";
    private static string StandardFieldEditorKey(string fieldKey) =>
        $"resource_field_{fieldKey}";
    private static string ResourceCredentialEditorKey(
        long credentialId,
        string field) =>
        $"resource_credential_{credentialId}_{field}";
    private static string ResourceAccessEditorKey(
        string slot,
        string field) =>
        $"resource_access_{slot}_{field}";
    private static bool IsYes(string value) =>
        value.Equals("Yes", StringComparison.OrdinalIgnoreCase);

    private static string SafeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '-');
        }

        return value;
    }
}

public sealed class ClientInfoResourceGroup : ObservableObject
{
    private ClientInfoResource? _selectedResource;
    private ClientInfoCredential[] _overviewCredentials = [];
    private ClientInfoCredential[] _standaloneCredentials = [];

    public ClientInfoResourceGroup(
        string categoryName,
        string description)
    {
        CategoryName = categoryName;
        Description = description;
        Resources.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CountLabel));
            OnPropertyChanged(nameof(HasResources));
        };
        OverviewSections.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(OverviewCountLabel));
            OnPropertyChanged(nameof(HasOverviewSections));
        };
    }

    public string CategoryName { get; }
    public string Description { get; }
    public string CountLabel => Resources.Count == 1
        ? "1 record"
        : $"{Resources.Count} records";
    public bool HasResources => Resources.Count > 0;
    public bool HasOverviewSections => OverviewSections.Count > 0;
    public string OverviewCountLabel => OverviewSections.Count == 1
        ? "1 important summary"
        : $"{OverviewSections.Count} important summaries";
    public string SelectedOverviewLabel => SelectedResource?.Name
        ?? "Select a record from the full list";
    public ClientInfoResource? SelectedResource
    {
        get => _selectedResource;
        set
        {
            if (SetProperty(ref _selectedResource, value))
            {
                OnPropertyChanged(nameof(SelectedOverviewLabel));
                RefreshSelectedOverview();
            }
        }
    }
    public ObservableCollection<ClientInfoResource> Resources { get; } = [];
    public ObservableCollection<ClientInfoCategoryOverviewSection> OverviewSections { get; } = [];
    public ObservableCollection<ClientInfoCredential> SelectedCredentials { get; } = [];
    public IReadOnlyList<ClientInfoCategoryOverviewSection> AllOverviewSections { get; private set; } = [];

    public void Replace(
        IEnumerable<ClientInfoResource> resources,
        IEnumerable<ClientInfoCredential> credentials,
        IEnumerable<ClientInfoCredential> standaloneCredentials)
    {
        var selectedId = SelectedResource?.ResourceId;
        var resourceArray = resources.ToArray();
        var credentialArray = credentials.ToArray();
        _standaloneCredentials = standaloneCredentials.ToArray();
        _selectedResource = null;
        OnPropertyChanged(nameof(SelectedResource));
        OnPropertyChanged(nameof(SelectedOverviewLabel));
        Resources.Clear();
        OverviewSections.Clear();
        SelectedCredentials.Clear();
        foreach (var resource in resourceArray)
        {
            Resources.Add(resource);
        }

        var resourceIds = resourceArray
            .Select(resource => resource.ResourceId)
            .ToHashSet();
        var overviewCredentials = credentialArray
            .Where(credential =>
                credential.ResourceId.HasValue
                && resourceIds.Contains(credential.ResourceId.Value))
            .Concat(_standaloneCredentials)
            .GroupBy(credential => credential.CredentialId)
            .Select(group => group.First())
            .ToArray();
        _overviewCredentials = overviewCredentials;
        AllOverviewSections = ClientInfoCategoryOverviewBuilder.Build(
            CategoryName,
            resourceArray,
            overviewCredentials);
        OnPropertyChanged(nameof(AllOverviewSections));

        SelectedResource = selectedId.HasValue
            ? Resources.FirstOrDefault(resource => resource.ResourceId == selectedId.Value)
                ?? Resources.FirstOrDefault()
            : Resources.FirstOrDefault();
    }

    private void RefreshSelectedOverview()
    {
        OverviewSections.Clear();
        SelectedCredentials.Clear();
        if (SelectedResource is null)
        {
            return;
        }

        foreach (var credential in _overviewCredentials
                     .Where(credential => credential.IsActive
                         && credential.ResourceId == SelectedResource.ResourceId)
                     .OrderBy(credential => credential.Name,
                         StringComparer.OrdinalIgnoreCase))
        {
            SelectedCredentials.Add(credential);
        }

        foreach (var section in ClientInfoCategoryOverviewBuilder.BuildSelected(
                     CategoryName,
                     SelectedResource,
                     SelectedCredentials.ToArray()))
        {
            OverviewSections.Add(section);
        }
    }
}
