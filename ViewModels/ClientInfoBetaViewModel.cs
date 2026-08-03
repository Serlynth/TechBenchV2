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
    private ClientInfoProfile _profile = new();
    private string _summary = "";
    private string _reviewStatus = "Unverified";
    private string _statusMessage = "Loading client information...";
    private ClientInfoLocation? _selectedLocation;
    private ClientInfoPerson? _selectedPerson;
    private ClientInfoResource? _selectedResource;
    private ClientInfoCredential? _selectedCredential;
    private ClientInfoFact? _selectedFact;
    private ClientInfoImportBatch? _selectedImportBatch;
    private ClientInfoAttachment? _selectedAttachment;
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
        IUserDialogService dialogs)
    {
        ClientId = clientId;
        _repository = repository;
        _currentUser = currentUser;
        _dialogs = dialogs;
        _attachmentStorage = new ClientAttachmentStorageService(repository);

        RefreshCommand = new RelayCommand(_ => Refresh());
        SaveProfileCommand = new RelayCommand(
            _ => SaveProfile(),
            _ => CanEdit);
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
            _ => CanEdit && SelectedResource is not null);
        ManageResourceFieldsCommand = new RelayCommand(
            _ => ManageResourceFields(),
            _ => CanEdit && SelectedResource is not null);
        AddCredentialCommand = new RelayCommand(
            _ => EditCredential(null),
            _ => CanEdit);
        EditCredentialCommand = new RelayCommand(
            item => EditCredential(
                item as ClientInfoCredential ?? SelectedCredential),
            _ => CanEdit && SelectedCredential is not null);
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
            item => RevealSecret(item as ClientInfoSecretSummary),
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
        ApproveImportCommand = new RelayCommand(
            _ => ApproveImport(),
            _ => CanManageImports
                && SelectedImportBatch is { State: "InReview" });
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
        OpenAttachmentCommand = new RelayCommand(
            item => OpenAttachment(item as ClientInfoAttachment
                ?? SelectedAttachment),
            _ => SelectedAttachment is not null);
        CopyAttachmentCommand = new RelayCommand(
            item => CopyAttachment(item as ClientInfoAttachment
                ?? SelectedAttachment),
            _ => SelectedAttachment is not null);
        DownloadAttachmentCommand = new RelayCommand(
            item => DownloadAttachment(item as ClientInfoAttachment
                ?? SelectedAttachment),
            _ => SelectedAttachment is not null);
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
    public string InternalClientIdLabel => $"Internal client ID {ClientId}";
    public string CutoverLabel => Profile.IsLive
        ? $"Canonical SQL is live · {Profile.CutoverState}"
        : $"Migration workspace · {Profile.CutoverState}";
    public string ClientInfoStatusLabel => Profile.IsLive
        ? $"Client Information is live - {Profile.CutoverState}"
        : $"Client Information draft - {Profile.CutoverState}";
    public bool CanEdit => _currentUser.CanWrite;
    public bool CanRevealSecrets => !_currentUser.IsReadOnlyPreview;
    public bool CanManageImports => _currentUser.IsAdmin && _currentUser.CanWrite;
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
    public ClientInfoResourceGroup BackupSecurityGroup { get; } = new(
        ClientInfoResourceCategories.BackupSecurity,
        "Backup systems, antivirus, EDR, MFA, filtering, and other security services.");
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
            ApproveImportCommand.RaiseCanExecuteChanged();
            PromoteImportCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(HasSelectedImportBatch));
        }
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand SaveProfileCommand { get; }
    public RelayCommand AddLocationCommand { get; }
    public RelayCommand EditLocationCommand { get; }
    public RelayCommand AddPersonCommand { get; }
    public RelayCommand EditPersonCommand { get; }
    public RelayCommand AddResourceCommand { get; }
    public RelayCommand EditResourceCommand { get; }
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
    public RelayCommand ApproveImportCommand { get; }
    public RelayCommand PromoteImportCommand { get; }
    public AsyncRelayCommand UploadAttachmentCommand { get; }
    public AsyncRelayCommand PasteAttachmentCommand { get; }
    public RelayCommand EditAttachmentCommand { get; }
    public RelayCommand OpenAttachmentCommand { get; }
    public RelayCommand CopyAttachmentCommand { get; }
    public RelayCommand DownloadAttachmentCommand { get; }
    public RelayCommand ArchiveAttachmentCommand { get; }
    public RelayCommand RestoreAttachmentCommand { get; }
    public RelayCommand RefreshAttachmentsCommand { get; }

    public void Refresh()
    {
        try
        {
            var snapshot = _repository.GetClientInfoSnapshot(ClientId)
                ?? throw new InvalidOperationException(
                    "The selected client is no longer available.");
            Profile = snapshot.Profile;
            Summary = snapshot.Profile.Summary;
            ReviewStatus = snapshot.Profile.ReviewStatus;
            Replace(Locations, snapshot.Locations);
            Replace(People, snapshot.People);
            Replace(Resources, snapshot.Resources);
            Replace(
                Equipment,
                _repository.EquipmentBoardAvailable
                    ? _repository.GetEquipmentInventory(ClientId)
                    : []);
            RefreshResourceGroups();
            Replace(Credentials, snapshot.Credentials);
            Replace(Facts, snapshot.Facts);
            Replace(ImportBatches, snapshot.ImportBatches);
            OnPropertyChanged(nameof(HasImportBatches));
            SelectedImportBatch = null;
            RefreshAttachments();
            StatusMessage =
                $"Loaded {Locations.Count} locations, {People.Count} people, "
                + $"{Equipment.Count} equipment records, {Resources.Count} technology records, "
                + $"{Credentials.Count} passwords, and {Attachments.Count} attachments.";
        }
        catch (Exception exception)
        {
            ShowError("Client Info could not be loaded", exception);
        }
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
        RaiseAttachmentCommandState();
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
                    ReviewStatus = ReviewStatus
                });
                StatusMessage = "Client profile saved.";
            });
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
        var locationOptions = new[] { "(None)" }
            .Concat(Locations.Select(item => item.Name))
            .ToArray();
        var values = ShowEditor(
            current is null ? "Add person" : "Edit person",
            [
                new("name", "Display name", current?.DisplayName ?? "", true),
                new("location", "Location", current?.LocationName ?? "(None)",
                    Options: locationOptions),
                new("role", "Role / department", current?.RoleDepartment ?? ""),
                new("email", "Email", current?.Email ?? ""),
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
            "person",
            () =>
            {
                _repository.SaveClientInfoPerson((current ?? new ClientInfoPerson
                {
                    ClientId = ClientId
                }) with
                {
                    DisplayName = values["name"],
                    LocationId = location?.LocationId,
                    LocationName = location?.Name ?? "",
                    RoleDepartment = values["role"],
                    Email = values["email"],
                    Phone = values["phone"],
                    MobilePhone = values["mobile"],
                    ContactType = values["type"],
                    IsPrimary = IsYes(values["primary"]),
                    IsActive = IsYes(values["active"]),
                    ReviewStatus = values["review"]
                });
                Refresh();
            });
    }

    private void EditResource(
        ClientInfoResource? current,
        string? requestedCategory = null)
    {
        var editingCategory = current?.Category
            ?? ClientInfoResourceCategories.NormalizeCategory(requestedCategory);
        var standardFields = ClientInfoResourceFieldDefinitions.ForCategory(
            editingCategory);
        var locationOptions = new[] { "(None)" }
            .Concat(Locations.Select(item => item.Name))
            .ToArray();
        var editorFields = new List<ClientInfoEditField>
        {
            new(
                "category",
                "Category",
                editingCategory,
                true,
                Options: ResourceCategories),
            new("name", "Name", current?.Name ?? "", true),
            new("type", "Type", current?.TypeLabel ?? "", true),
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
                current?.GetFieldValue(field.FieldKey) ?? "")));
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
        var values = ShowEditor(
            current is null ? "Add technology or service" : "Edit technology or service",
            editorFields);
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
                        values["category"],
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
                Refresh();
            });
    }

    private void ManageResourceFields()
    {
        if (SelectedResource is null)
        {
            return;
        }

        var editor = new ClientInfoResourceFieldsWindow(
            SelectedResource,
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
        foreach (var group in new[]
                 {
                     ServerInfrastructureGroup,
                     ConnectionInternetGroup,
                     WifiGroup,
                     ApplicationsCloudGroup,
                     DomainsEmailGroup,
                     BackupSecurityGroup,
                     VendorsServicesGroup,
                     NeedsSortingGroup
                 })
        {
            group.Replace(Resources.Where(resource => string.Equals(
                resource.Category,
                group.CategoryName,
                StringComparison.OrdinalIgnoreCase)));
        }
    }

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
                new("person", "Person", currentPerson, Options: personOptions),
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

    private void RevealSecret(ClientInfoSecretSummary? secret)
    {
        if (secret is null)
        {
            return;
        }

        try
        {
            var revealed = _repository.RevealClientInfoSecret(
                secret.SecretId)
                ?? throw new InvalidOperationException(
                    "The secret is no longer available.");
            var window = new ClientInfoSecretRevealWindow(revealed)
            {
                Owner = FindOwner()
            };
            window.ShowDialog();
            StatusMessage = $"Revealed {secret.SecretLabel}; access was audited.";
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
            var revealed = _repository.RevealClientInfoSecret(
                secret.SecretId,
                forClipboard: true)
                ?? throw new InvalidOperationException(
                    "The secret is no longer available.");
            WpfClipboard.SetText(revealed.SecretValue);
            StatusMessage =
                $"Copied {secret.SecretLabel}; clipboard access was audited.";
        }
        catch (Exception exception)
        {
            ShowError("Secret could not be copied", exception);
        }
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
            _workbooks.CreateTemplate(dialog.FileName, ClientId, ClientName);
            StatusMessage =
                $"Created the migration workbook for internal client ID {ClientId}. Copy the cleaned client information into it, then return here to import it.";
        }
        catch (Exception exception)
        {
            ShowError("Template could not be created", exception);
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
            SelectedImportBatch = _repository.GetClientInfoImportBatch(
                SelectedImportBatch.BatchId);
            StatusMessage = "Import review refreshed.";
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
        IReadOnlyList<ClientInfoEditField> fields)
    {
        var editor = new ClientInfoRecordEditorWindow(title, fields)
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
        catch (Exception exception)
        {
            ShowError($"{recordName} could not be saved", exception);
        }
    }

    private static bool IsConcurrencyConflict(SqlException exception) =>
        exception.Number is 52324 or 52332 or 52343 or 52354 or 52363
            or 52358 or 52359 or 52375 or 52384 or 52441 or 52453 or 52460;

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
    public ClientInfoResourceGroup(
        string categoryName,
        string description)
    {
        CategoryName = categoryName;
        Description = description;
        Resources.CollectionChanged += (_, _) =>
            OnPropertyChanged(nameof(CountLabel));
    }

    public string CategoryName { get; }
    public string Description { get; }
    public string CountLabel => Resources.Count == 1
        ? "1 record"
        : $"{Resources.Count} records";
    public ObservableCollection<ClientInfoResource> Resources { get; } = [];

    public void Replace(IEnumerable<ClientInfoResource> resources)
    {
        Resources.Clear();
        foreach (var resource in resources)
        {
            Resources.Add(resource);
        }
    }
}
