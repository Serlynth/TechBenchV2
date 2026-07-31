using TechBench.Models;

namespace TechBench.Data;

/// <summary>
/// Persistence contract for the SQL Server-backed V2 workspace. Synchronous
/// members keep the WPF command model simple while the implementation also
/// exposes asynchronous operations for server-bound workflows.
/// </summary>
public interface ITechBenchRepository
{
    string DatabasePath { get; }

    bool FullTextSearchAvailable { get; }

    bool EquipmentBoardAvailable => false;

    bool ClientInfoBetaAvailable => false;

    void Initialize();

    IReadOnlyList<Client> GetClients(bool includeInactive = false, string? searchTerm = null);

    Client? GetClient(int id);

    int SaveClient(Client client);

    void SynchronizeServerClientCache(IReadOnlyList<Client> clients);

    IReadOnlyList<Ticket> GetTickets(
        int? clientId = null,
        string? searchTerm = null,
        bool includeClosed = false);

    Ticket? GetTicket(int id);

    int SaveTicket(Ticket ticket);

    IReadOnlyList<TicketStatusOption> GetTicketStatusOptions();

    int UpsertTicketStatusOption(TicketStatusOption option);

    int UpsertSyncedClient(Client client);

    int UpsertSageCustomer(SageCustomer customer, DateTime? syncedAt = null);

    Client MergeClientRecords(int whdClientId, int sageClientId);

    int ReconcileExactClientMatches();

    int ReconcileStrongClientMatches();

    int ReconcileSafeClientMatches();

    int RemoveStaleSageCustomers(
        IReadOnlyCollection<string> activeSageCustomerIds,
        DateTime? syncedAt = null);

    Client? TryAutoMatchSageCustomerForClient(int clientId);

    int UpsertSyncedTicket(Ticket ticket);

    void SynchronizeWhdTickets(
        IReadOnlyList<WhdSyncedTicket> whdTickets,
        DateTime syncedAt,
        bool reconcileMissing);

    int SynchronizeWhdClients(
        IReadOnlyList<WhdSyncedClient> whdClients,
        DateTime syncedAt,
        bool reconcileMissing = false);

    IReadOnlyList<WorkEntry> GetWorkEntries(WorkEntryQuery query);

    IReadOnlyList<string> GetDistinctTags();

    IReadOnlyList<OrganizationTag> GetOrganizationTags();

    int SaveOrganizationTag(OrganizationTag tag);

    void DeleteOrganizationTag(OrganizationTag tag);

    WorkEntry? GetWorkEntry(int id);

    int SaveWorkEntry(WorkEntry entry);

    int ImportWorkEntries(
        IEnumerable<WorkEntry> entries,
        IReadOnlyDictionary<string, int>? clientAliases = null);

    V1ImportReferenceResolution ResolveV1ImportReferences(
        V1DatabaseImportPackage package);

    V1DatabaseImportResult ImportV1Database(V1DatabaseImportPackage package);

    void AbandonV1Import();

    void DeleteWorkEntry(int id, bool confirmMissingWhdTechNote = false);

    IReadOnlyList<WorkEntryLink> GetWorkEntryLinks(int workEntryId);

    int SaveWorkEntryLink(
        int sourceWorkEntryId,
        int targetWorkEntryId,
        WorkEntryLinkType linkType);

    void DeleteWorkEntryLink(int linkId);

    IReadOnlyList<NoteTemplate> GetTemplates();

    int SaveTemplate(NoteTemplate template);

    void DeleteTemplate(int id);

    EditorDraft? GetEditorDraft();

    void SaveEditorDraft(EditorDraft draft);

    void ClearEditorDraft();

    IReadOnlyDictionary<string, int> GetClientAliases();

    void SaveClientAlias(string alias, int clientId);

    IReadOnlyList<CommonLink> GetCommonLinks();

    IReadOnlyList<FireDrillCredentialSummary> SearchFireDrillCredentials(string? searchTerm = null) => [];

    FireDrillCredential? RevealFireDrillCredential(long credentialId) => null;

    IReadOnlyList<ClientInfoClientSummary> SearchClientInfoClients(
        string? searchTerm = null,
        bool includeInactive = false) => [];

    ClientInfoSnapshot? GetClientInfoSnapshot(int clientId) => null;

    ClientInfoProfile SaveClientInfoProfile(ClientInfoProfile profile) =>
        throw new NotSupportedException(
            "Canonical Client Info requires the shared SQL Server beta extension.");

    ClientInfoLocation SaveClientInfoLocation(ClientInfoLocation location) =>
        throw new NotSupportedException(
            "Canonical Client Info requires the shared SQL Server beta extension.");

    ClientInfoPerson SaveClientInfoPerson(ClientInfoPerson person) =>
        throw new NotSupportedException(
            "Canonical Client Info requires the shared SQL Server beta extension.");

    ClientInfoResource SaveClientInfoResource(ClientInfoResource resource) =>
        throw new NotSupportedException(
            "Canonical Client Info requires the shared SQL Server beta extension.");

    ClientInfoResourceField SaveClientInfoResourceField(
        ClientInfoResourceField field) =>
        throw new NotSupportedException(
            "Canonical Client Info requires the shared SQL Server beta extension.");

    void DeleteClientInfoResourceField(ClientInfoResourceField field) =>
        throw new NotSupportedException(
            "Canonical Client Info requires the shared SQL Server beta extension.");

    ClientInfoFact SaveClientInfoFact(ClientInfoFact fact) =>
        throw new NotSupportedException(
            "Canonical Client Info requires the shared SQL Server beta extension.");

    ClientInfoCredential SaveClientInfoCredential(ClientInfoCredential credential) =>
        throw new NotSupportedException(
            "Canonical Client Info requires the shared SQL Server beta extension.");

    ClientInfoSecretSummary SetClientInfoSecret(
        ClientInfoSecretSummary secret,
        string secretValue,
        bool verified) =>
        throw new NotSupportedException(
            "Canonical Client Info requires the shared SQL Server beta extension.");

    RevealedClientInfoSecret? RevealClientInfoSecret(
        long secretId,
        bool forClipboard = false) => null;

    ClientInfoImportBatch ImportClientInfoWorkbook(
        ClientInfoWorkbookPackage package) =>
        throw new NotSupportedException(
            "Canonical Client Info import requires the shared SQL Server beta extension.");

    ClientInfoImportBatch GetClientInfoImportBatch(Guid batchId) =>
        throw new NotSupportedException(
            "Canonical Client Info import requires the shared SQL Server beta extension.");

    ClientInfoImportBatch ValidateClientInfoImport(Guid batchId) =>
        throw new NotSupportedException(
            "Canonical Client Info import requires the shared SQL Server beta extension.");

    ClientInfoImportBatch CompareClientInfoImportToFireDrill(Guid batchId) =>
        throw new NotSupportedException(
            "Canonical Client Info comparison requires the shared SQL Server beta extension.");

    ClientInfoImportBatch ApproveClientInfoImport(ClientInfoImportBatch batch) =>
        throw new NotSupportedException(
            "Canonical Client Info import requires the shared SQL Server beta extension.");

    void PromoteClientInfoImport(ClientInfoImportBatch batch) =>
        throw new NotSupportedException(
            "Canonical Client Info import requires the shared SQL Server beta extension.");

    IReadOnlyList<ClientUserSummary> SearchClientUsers(
        int? clientId = null,
        string? searchTerm = null) => [];

    ClientUserSummary? RevealClientUser(long clientUserId) => null;

    CredentialsSyncServiceStatus GetCredentialsSyncStatus() => new();

    CredentialsSyncRequestResult RequestCredentialsSync() =>
        throw new NotSupportedException(
            "Credentials synchronization requires the shared SQL Server workspace.");

    int SaveCommonLink(CommonLink link);

    void DeleteCommonLink(int id);

    IReadOnlyDictionary<string, string> GetSettings();

    string GetSetting(string key, string fallback = "");

    void SaveSetting(string key, string value);

    void DeleteSetting(string key);

    void SaveOrganizationSetting(string key, string value);

    void DeleteOrganizationSetting(string key);

    WhdSyncServiceStatus GetWhdSyncStatus();

    WhdSyncRequestResult RequestWhdSync();

    SageSyncServiceStatus GetSageSyncStatus();

    SageSyncRequestResult RequestSageSync(
        bool allowLargeRemoval = false,
        Guid? confirmedRequestId = null);

    ClientSessionHeartbeatResult HeartbeatClientSession(
        Guid sessionId,
        string machineName,
        string clientVersion,
        string currentSection,
        bool hasUnsavedChanges,
        bool isBusy) => new();

    IReadOnlyList<ClientSessionInfo> GetActiveClientSessions(
        Guid currentSessionId) => [];

    IReadOnlyList<ClientSessionCommandResponse> GetRecentClientSessionResponses() => [];

    ClientSessionCommand QueueClientSessionCommand(
        Guid requesterSessionId,
        Guid targetSessionId,
        string commandType,
        string message) =>
        throw new NotSupportedException("Client session administration requires SQL Server schema 11.");

    void AcknowledgeClientSessionCommand(
        Guid sessionId,
        long commandId,
        string result,
        string? responseMessage = null)
    {
    }

    void CloseClientSession(Guid sessionId)
    {
    }

    IReadOnlyList<WhdUserMapping> GetWhdUserMappings();

    IReadOnlyList<WhdTechnician> GetWhdTechnicians();

    WhdUserMapping SaveWhdUserMapping(WhdUserMapping mapping);

    IReadOnlyList<EquipmentItem> GetEquipmentBoard() => [];

    IReadOnlyList<EquipmentItem> GetEquipmentInventory(
        int? clientId = null,
        long? clientUserId = null,
        string? clientName = null) => [];

    IReadOnlyList<InventoryClient> GetInventoryClients() => [];

    IReadOnlyList<EquipmentAssignmentHistoryEntry> GetEquipmentAssignmentHistory(
        long equipmentId) => [];

    EquipmentItem SaveEquipment(EquipmentItem equipment) =>
        throw new NotSupportedException(
            "The equipment board requires the shared SQL Server workspace.");

    IReadOnlyList<EquipmentItem> MoveEquipment(
        EquipmentItem equipment,
        string? targetWindowsLoginName,
        string targetWorkflowStage,
        int targetIndex) =>
        throw new NotSupportedException(
            "The equipment board requires the shared SQL Server workspace.");

    void ArchiveEquipment(EquipmentItem equipment) =>
        throw new NotSupportedException(
            "The equipment board requires the shared SQL Server workspace.");

    void AddPostingLog(PostingLog log);

    PostingLog? GetLatestVerifiedWhdPostingLog(int workEntryId);

    PostingAttemptStartResult TryBeginPostingAttempt(
        int workEntryId,
        string destination,
        string attemptKey,
        string payloadHash);

    PostingAttempt? GetOutstandingPostingAttempt(int workEntryId, string destination);

    void CompletePostingAttempt(
        int attemptId,
        PostingAttemptStatus status,
        string message,
        string? externalReference = null,
        bool markPosted = true);

    int ResolveOutstandingPostingAttempts(
        int workEntryId,
        string destination,
        string message,
        string? externalReference = null);

    int AbandonOutstandingPostingAttempts(
        int workEntryId,
        string destination,
        string message);

    void MarkWorkEntryPosted(
        int workEntryId,
        string destination,
        string message,
        string? externalReference = null);

    bool HasSuccessfulSageDraftLog(int workEntryId);

    IReadOnlyList<PostingLog> GetPostingLogs(
        string? destination = null,
        bool? success = null,
        string? keyword = null,
        DateTime? startDate = null,
        DateTime? endDate = null,
        int limit = 250);
}
