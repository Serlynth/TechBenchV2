using Microsoft.Data.SqlClient;
using System.IO;
using TechBench.Models;

namespace TechBench.Data;

public sealed partial class SqlServerTechBenchRepository
{
    public IReadOnlyList<ClientInfoClientSummary> SearchClientInfoClients(
        string? searchTerm = null,
        bool includeInactive = false)
    {
        if (!ClientInfoBetaAvailable)
        {
            return [];
        }

        return QueryAsync(
            Procedures.SearchClientInfoClients,
            command =>
            {
                AddText(command, "@Search", 240, searchTerm);
                AddBit(command, "@IncludeInactive", includeInactive);
                AddInt(command, "@Limit", 1000);
            },
            (reader, token) => ReadListAsync(
                reader,
                token,
                ReadClientInfoClientSummary),
            CancellationToken.None).GetAwaiter().GetResult();
    }

    public ClientInfoClientSummary CreateManualClientInfoClient(
        string clientName)
    {
        if (!ManualClientInfoCreationAvailable)
        {
            throw new NotSupportedException(
                "Creating a live manual client requires the current TechBench Server package.");
        }

        return QueryAsync(
            Procedures.CreateManualClientInfoClient,
            command =>
            {
                AddRequiredText(command, "@Name", 240, clientName);
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            (reader, token) => ReadSingleAsync(
                reader,
                token,
                ReadClientInfoClientSummary),
            CancellationToken.None).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException(
                "SQL Server did not return the new live client.");
    }

    public ClientInfoSnapshot? GetClientInfoSnapshot(int clientId)
    {
        if (!ClientInfoBetaAvailable || clientId <= 0)
        {
            return null;
        }

        return QueryAsync(
            Procedures.GetClientInfoSnapshot,
            command => AddInt(command, "@ClientId", clientId),
            ReadClientInfoSnapshotAsync,
            CancellationToken.None).GetAwaiter().GetResult();
    }

    public ClientAttachmentStorageConfiguration GetClientAttachmentStorageConfiguration() =>
        QueryAsync(
            Procedures.GetClientAttachmentStorageConfiguration,
            null,
            (reader, token) => ReadSingleAsync(
                reader,
                token,
                row => new ClientAttachmentStorageConfiguration
                {
                    RootPath = GetString(row, "RootPath"),
                    MaximumFileSizeMegabytes = Math.Clamp(
                        GetInt32(row, "MaximumFileSizeMegabytes", 50),
                        1,
                        2048),
                    AllowedExtensions = GetString(
                        row,
                        "AllowedExtensions",
                        new ClientAttachmentStorageConfiguration()
                            .AllowedExtensions)
                }),
            CancellationToken.None).GetAwaiter().GetResult()
        ?? new ClientAttachmentStorageConfiguration();

    public IReadOnlyList<ClientInfoAttachment> GetClientInfoAttachments(
        int clientId,
        bool includeArchived = false)
    {
        if (clientId <= 0)
        {
            return [];
        }

        return QueryAsync(
            Procedures.GetClientInfoAttachments,
            command =>
            {
                AddInt(command, "@ClientId", clientId);
                AddBit(command, "@IncludeArchived", includeArchived);
            },
            (reader, token) => ReadListAsync(
                reader,
                token,
                ReadClientInfoAttachment),
            CancellationToken.None).GetAwaiter().GetResult();
    }

    public ClientInfoAttachment SaveClientInfoAttachment(
        ClientInfoAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return QueryAsync(
            Procedures.SaveClientInfoAttachment,
            command =>
            {
                AddGuid(command, "@AttachmentId", attachment.AttachmentId);
                AddInt(command, "@ClientId", attachment.ClientId);
                AddRequiredText(
                    command,
                    "@RelativePath",
                    400,
                    attachment.RelativePath);
                AddRequiredText(
                    command,
                    "@OriginalFileName",
                    260,
                    attachment.OriginalFileName);
                AddRequiredText(
                    command,
                    "@ContentType",
                    160,
                    attachment.ContentType);
                AddRequiredText(command, "@Category", 80, attachment.Category);
                AddText(command, "@Caption", 500, attachment.Caption);
                AddBigInt(command, "@FileSizeBytes", attachment.FileSizeBytes);
                AddBinary(
                    command,
                    "@ContentSha256",
                    32,
                    attachment.ContentSha256);
                AddBinary(
                    command,
                    "@ExpectedRowVersion",
                    8,
                    attachment.RowVersion);
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            (reader, token) => ReadSingleAsync(
                reader,
                token,
                ReadClientInfoAttachment),
            CancellationToken.None).GetAwaiter().GetResult()
        ?? throw new InvalidOperationException(
            "SQL Server did not return the saved client attachment.");
    }

    public ClientInfoAttachment SetClientInfoAttachmentArchived(
        ClientInfoAttachment attachment,
        bool isArchived)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return QueryAsync(
            Procedures.SetClientInfoAttachmentArchived,
            command =>
            {
                AddGuid(command, "@AttachmentId", attachment.AttachmentId);
                AddInt(command, "@ClientId", attachment.ClientId);
                AddBit(command, "@IsArchived", isArchived);
                AddBinary(
                    command,
                    "@ExpectedRowVersion",
                    8,
                    attachment.RowVersion);
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            (reader, token) => ReadSingleAsync(
                reader,
                token,
                ReadClientInfoAttachment),
            CancellationToken.None).GetAwaiter().GetResult()
        ?? throw new InvalidOperationException(
            "SQL Server did not return the archived client attachment.");
    }

    public ClientInfoAttachment SetClientInfoAttachmentEquipmentLink(
        ClientInfoAttachment attachment,
        long? equipmentId)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return QueryAsync(
            Procedures.SetClientInfoAttachmentEquipmentLink,
            command =>
            {
                AddGuid(command, "@AttachmentId", attachment.AttachmentId);
                AddInt(command, "@ClientId", attachment.ClientId);
                AddBigInt(command, "@EquipmentId", equipmentId);
                AddBinary(
                    command,
                    "@ExpectedRowVersion",
                    8,
                    attachment.RowVersion);
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            (reader, token) => ReadSingleAsync(
                reader,
                token,
                ReadClientInfoAttachment),
            CancellationToken.None).GetAwaiter().GetResult()
        ?? throw new InvalidOperationException(
            "SQL Server did not return the linked client attachment.");
    }

    public ClientInfoProfile SaveClientInfoProfile(ClientInfoProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return QueryAsync(
            Procedures.SaveClientInfoProfile,
            command =>
            {
                AddInt(command, "@ClientId", profile.ClientId);
                AddText(command, "@Summary", 2000, profile.Summary);
                AddText(
                    command,
                    "@ClientFolderPath",
                    2048,
                    profile.ClientFolderPath);
                AddText(
                    command,
                    "@LegacyClientInfoSheetPath",
                    2048,
                    profile.LegacyClientInfoSheetPath);
                AddRequiredText(
                    command,
                    "@ReviewStatus",
                    24,
                    profile.ReviewStatus);
                AddBinary(command, "@ExpectedRowVersion", 8, profile.RowVersion);
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            (reader, token) => ReadSingleAsync(
                reader,
                token,
                row => ReadClientInfoProfile(row) with
                {
                    ClientName = profile.ClientName,
                    IsActive = profile.IsActive,
                    WhdContactName = profile.WhdContactName,
                    WhdContactEmail = profile.WhdContactEmail,
                    WhdPhone = profile.WhdPhone,
                    WhdAddress = profile.WhdAddress,
                    CutoverState = profile.CutoverState,
                    CutoverRowVersion = profile.CutoverRowVersion
                }),
            CancellationToken.None).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException(
                "SQL Server did not return the saved Client Info profile.");
    }

    public ClientInfoLocation SaveClientInfoLocation(ClientInfoLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return QueryAsync(
            Procedures.SaveClientInfoLocation,
            command =>
            {
                AddBigInt(
                    command,
                    "@LocationId",
                    location.LocationId > 0 ? location.LocationId : null);
                AddInt(command, "@ClientId", location.ClientId);
                AddText(command, "@LocalKey", 120, location.LocalKey);
                AddRequiredText(command, "@Name", 240, location.Name);
                AddText(command, "@LocationType", 80, location.LocationType);
                AddText(command, "@Address1", 240, location.Address1);
                AddText(command, "@Address2", 240, location.Address2);
                AddText(command, "@City", 120, location.City);
                AddText(command, "@StateProvince", 80, location.StateProvince);
                AddText(command, "@PostalCode", 40, location.PostalCode);
                AddText(command, "@MainPhone", 80, location.MainPhone);
                AddText(command, "@TimeZoneId", 120, location.TimeZoneId);
                AddBit(command, "@IsPrimary", location.IsPrimary);
                AddRequiredText(
                    command,
                    "@ReviewStatus",
                    24,
                    location.ReviewStatus);
                AddBit(command, "@IsActive", location.IsActive);
                AddBinary(
                    command,
                    "@ExpectedRowVersion",
                    8,
                    location.RowVersion);
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            (reader, token) => ReadSingleAsync(
                reader,
                token,
                ReadClientInfoLocation),
            CancellationToken.None).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException(
                "SQL Server did not return the saved Client Info location.");
    }

    public ClientInfoPerson SaveClientInfoPerson(ClientInfoPerson person)
    {
        ArgumentNullException.ThrowIfNull(person);
        return QueryAsync(
            Procedures.SaveClientInfoPerson,
            command =>
            {
                AddBigInt(
                    command,
                    "@PersonId",
                    person.PersonId > 0 ? person.PersonId : null);
                AddInt(command, "@ClientId", person.ClientId);
                AddBigInt(command, "@LocationId", person.LocationId);
                AddText(command, "@LocalKey", 120, person.LocalKey);
                AddRequiredText(
                    command,
                    "@DisplayName",
                    240,
                    person.DisplayName);
                AddText(
                    command,
                    "@RoleDepartment",
                    240,
                    person.RoleDepartment);
                AddText(command, "@AdUsername", 256, person.AdUsername);
                AddText(command, "@Email", 320, person.Email);
                AddBit(command, "@HasMicrosoft365", person.HasMicrosoft365);
                AddText(
                    command,
                    "@Microsoft365License",
                    240,
                    person.Microsoft365License);
                AddText(command, "@PcName", 240, person.PcName);
                AddText(command, "@Phone", 80, person.Phone);
                AddText(command, "@MobilePhone", 80, person.MobilePhone);
                AddText(command, "@ContactType", 80, person.ContactType);
                AddBit(command, "@IsPrimary", person.IsPrimary);
                AddRequiredText(
                    command,
                    "@ReviewStatus",
                    24,
                    person.ReviewStatus);
                AddBit(command, "@IsActive", person.IsActive);
                AddBinary(command, "@ExpectedRowVersion", 8, person.RowVersion);
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            (reader, token) => ReadSingleAsync(
                reader,
                token,
                row => ReadClientInfoPerson(row) with
                {
                    LocationName = person.LocationName
                }),
            CancellationToken.None).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException(
                "SQL Server did not return the saved Client Info person.");
    }

    public ClientInfoResource SaveClientInfoResource(ClientInfoResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return QueryAsync(
            Procedures.SaveClientInfoResource,
            command =>
            {
                AddBigInt(
                    command,
                    "@ResourceId",
                    resource.ResourceId > 0 ? resource.ResourceId : null);
                AddInt(command, "@ClientId", resource.ClientId);
                AddBigInt(command, "@LocationId", resource.LocationId);
                AddBigInt(
                    command,
                    "@ParentResourceId",
                    resource.ParentResourceId);
                AddBigInt(command, "@EquipmentId", resource.EquipmentId);
                AddText(command, "@LocalKey", 120, resource.LocalKey);
                AddRequiredText(
                    command,
                    "@ResourceType",
                    80,
                    resource.ResourceType);
                AddRequiredText(command, "@Name", 240, resource.Name);
                AddText(command, "@Provider", 160, resource.Provider);
                AddText(
                    command,
                    "@AddressOrUrl",
                    1000,
                    resource.AddressOrUrl);
                AddText(command, "@Status", 80, resource.Status);
                AddMaxText(command, "@Notes", resource.Notes);
                AddRequiredText(
                    command,
                    "@ReviewStatus",
                    24,
                    resource.ReviewStatus);
                AddBit(command, "@IsActive", resource.IsActive);
                AddBinary(
                    command,
                    "@ExpectedRowVersion",
                    8,
                    resource.RowVersion);
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            (reader, token) => ReadSingleAsync(
                reader,
                token,
                row => ReadClientInfoResource(row) with
                {
                    LocationName = resource.LocationName,
                    Fields = resource.Fields
                }),
            CancellationToken.None).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException(
                "SQL Server did not return the saved Client Info resource.");
    }

    public ClientInfoResourceField SaveClientInfoResourceField(
        ClientInfoResourceField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return QueryAsync(
            Procedures.SaveClientInfoResourceField,
            command =>
            {
                AddBigInt(
                    command,
                    "@ResourceFieldId",
                    field.ResourceFieldId > 0 ? field.ResourceFieldId : null);
                AddBigInt(command, "@ResourceId", field.ResourceId);
                AddRequiredText(command, "@FieldKey", 120, field.FieldKey);
                AddRequiredText(command, "@FieldLabel", 200, field.FieldLabel);
                AddMaxText(command, "@ValueText", field.ValueText);
                AddRequiredText(command, "@ValueType", 24, field.ValueType);
                AddInt(command, "@SortOrder", field.SortOrder);
                AddBinary(command, "@ExpectedRowVersion", 8, field.RowVersion);
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            (reader, token) => ReadSingleAsync(
                reader,
                token,
                ReadClientInfoResourceField),
            CancellationToken.None).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException(
                "SQL Server did not return the saved resource field.");
    }

    public void DeleteClientInfoResourceField(ClientInfoResourceField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        ExecuteNonQueryAsync(
            Procedures.DeleteClientInfoResourceField,
            command =>
            {
                AddBigInt(command, "@ResourceFieldId", field.ResourceFieldId);
                AddBinary(command, "@ExpectedRowVersion", 8, field.RowVersion);
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            CancellationToken.None).GetAwaiter().GetResult();
    }

    public ClientInfoFact SaveClientInfoFact(ClientInfoFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        return QueryAsync(
            Procedures.SaveClientInfoFact,
            command =>
            {
                AddBigInt(
                    command,
                    "@FactId",
                    fact.FactId > 0 ? fact.FactId : null);
                AddInt(command, "@ClientId", fact.ClientId);
                AddText(command, "@LocalKey", 120, fact.LocalKey);
                AddRequiredText(
                    command,
                    "@SectionName",
                    120,
                    fact.SectionName);
                AddRequiredText(
                    command,
                    "@FieldLabel",
                    200,
                    fact.FieldLabel);
                AddMaxText(command, "@ValueText", fact.ValueText);
                AddRequiredText(
                    command,
                    "@ValueType",
                    24,
                    fact.ValueType);
                AddRequiredText(
                    command,
                    "@ReviewStatus",
                    24,
                    fact.ReviewStatus);
                AddInt(command, "@SortOrder", fact.SortOrder);
                AddBit(command, "@IsActive", fact.IsActive);
                AddBinary(command, "@ExpectedRowVersion", 8, fact.RowVersion);
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            (reader, token) => ReadSingleAsync(
                reader,
                token,
                ReadClientInfoFact),
            CancellationToken.None).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException(
                "SQL Server did not return the saved Client Info fact.");
    }

    public ClientInfoCredential SaveClientInfoCredential(
        ClientInfoCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return QueryAsync(
            Procedures.SaveClientCredential,
            command =>
            {
                AddBigInt(
                    command,
                    "@CredentialId",
                    credential.CredentialId > 0
                        ? credential.CredentialId
                        : null);
                AddInt(command, "@ClientId", credential.ClientId);
                AddBigInt(command, "@ResourceId", credential.ResourceId);
                AddBigInt(command, "@PersonId", credential.PersonId);
                AddText(command, "@LocalKey", 120, credential.LocalKey);
                AddRequiredText(command, "@Name", 240, credential.Name);
                AddText(command, "@Category", 120, credential.Category);
                AddText(command, "@Username", 500, credential.Username);
                AddText(command, "@LoginUrl", 1000, credential.LoginUrl);
                AddText(command, "@Notes", 1000, credential.Notes);
                AddRequiredText(
                    command,
                    "@ReviewStatus",
                    24,
                    credential.ReviewStatus);
                AddBit(command, "@IsActive", credential.IsActive);
                AddBinary(
                    command,
                    "@ExpectedRowVersion",
                    8,
                    credential.RowVersion);
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            (reader, token) => ReadSingleAsync(
                reader,
                token,
                row => ReadClientInfoCredential(row) with
                {
                    SecretCount = credential.SecretCount,
                    Secrets = credential.Secrets
                }),
            CancellationToken.None).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException(
                "SQL Server did not return the saved client credential.");
    }

    public ClientInfoSecretSummary SetClientInfoSecret(
        ClientInfoSecretSummary secret,
        string secretValue,
        bool verified)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretValue);
        return QueryAsync(
            Procedures.SetClientCredentialSecret,
            command =>
            {
                AddBigInt(
                    command,
                    "@SecretId",
                    secret.SecretId > 0 ? secret.SecretId : null);
                AddBigInt(command, "@CredentialId", secret.CredentialId);
                AddRequiredText(
                    command,
                    "@SecretType",
                    80,
                    secret.SecretType);
                AddRequiredText(
                    command,
                    "@SecretLabel",
                    200,
                    secret.SecretLabel);
                AddMaxText(command, "@SecretValue", secretValue);
                AddBit(command, "@IsVerified", verified);
                AddBinary(
                    command,
                    "@ExpectedRowVersion",
                    8,
                    secret.RowVersion);
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            (reader, token) => ReadSingleAsync(
                reader,
                token,
                ReadClientInfoSecretSummary),
            CancellationToken.None).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException(
                "SQL Server did not return the saved client secret.");
    }

    public RevealedClientInfoSecret? RevealClientInfoSecret(
        long secretId,
        bool forClipboard = false,
        byte[]? authorizationToken = null)
    {
        if (secretId <= 0)
        {
            return null;
        }

        var loginSession = _connectionFactory.AuthPointLoginSession;
        try
        {
            return QueryAsync(
                Procedures.RevealClientCredentialSecret,
                command =>
                {
                    AddBigInt(command, "@SecretId", secretId);
                    AddRequiredText(
                        command,
                        "@AccessAction",
                        12,
                        forClipboard ? "Copy" : "Reveal");
                    AddBinary(
                        command,
                        "@AuthorizationToken",
                        32,
                        authorizationToken);
                    if (loginSession is not null)
                    {
                        AddGuid(command, "@MfaSessionId", loginSession.SessionId);
                        AddBinary(
                            command,
                            "@MfaSessionToken",
                            32,
                            loginSession.SessionToken);
                    }
                    AddGuid(command, "@RequestId", Guid.NewGuid());
                },
                (reader, token) => ReadSingleAsync(
                    reader,
                    token,
                    row => new RevealedClientInfoSecret
                    {
                        SecretId = GetInt64(row, "SecretId"),
                        CredentialId = GetInt64(row, "CredentialId"),
                        ClientId = GetInt32(row, "ClientId"),
                        CredentialName = GetString(row, "CredentialName"),
                        SecretType = GetString(row, "SecretType"),
                        SecretLabel = GetString(row, "SecretLabel"),
                        SecretValue = GetString(row, "SecretValue"),
                        RowVersion = GetBytes(row, "RowVersion")
                    }),
                CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Microsoft.Data.SqlClient.SqlException exception)
            when (exception.Number == 52440 && loginSession is not null)
        {
            _connectionFactory.ClearAuthPointLoginSession();
            throw new InvalidOperationException(
                "Your AuthPoint TechBench login has expired. Close and reopen TechBench to sign in again.",
                exception);
        }
    }

    public ClientSecretMfaChallenge BeginClientSecretMfaChallenge(
        long secretId,
        bool forClipboard)
    {
        if (secretId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(secretId));
        }

        return QueryAsync(
            Procedures.BeginClientSecretMfaChallenge,
            command =>
            {
                AddBigInt(command, "@SecretId", secretId);
                AddRequiredText(
                    command,
                    "@ActionScope",
                    16,
                    forClipboard ? "Copy" : "Reveal");
                AddText(command, "@ClientMachine", 128, Environment.MachineName);
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            (reader, token) => ReadSingleAsync(
                reader,
                token,
                row => new ClientSecretMfaChallenge
                {
                    ChallengeId = GetNullableGuid(row, "ChallengeId") ?? Guid.Empty,
                    ChallengeNonce = GetBytes(row, "ChallengeNonce") ?? [],
                    Status = GetString(row, "Status"),
                    ExpiresAtUtc = GetNullableDateTime(row, "ExpiresAtUtc"),
                    ProviderLogin = GetString(row, "ProviderLogin")
                }),
            CancellationToken.None).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException(
                "SQL Server did not return an AuthPoint challenge.");
    }

    public ClientSecretMfaStatus GetClientSecretMfaChallenge(
        Guid challengeId,
        byte[] challengeNonce)
    {
        if (challengeId == Guid.Empty || challengeNonce.Length != 32)
        {
            throw new ArgumentException("The AuthPoint challenge proof is invalid.");
        }

        return QueryAsync(
            Procedures.GetClientSecretMfaChallenge,
            command =>
            {
                AddGuid(command, "@ChallengeId", challengeId);
                AddBinary(command, "@ChallengeNonce", 32, challengeNonce);
            },
            (reader, token) => ReadSingleAsync(
                reader,
                token,
                row => new ClientSecretMfaStatus
                {
                    ChallengeId = GetNullableGuid(row, "ChallengeId") ?? Guid.Empty,
                    Status = GetString(row, "Status"),
                    OutcomeCode = GetString(row, "OutcomeCode"),
                    OutcomeMessage = GetString(row, "OutcomeMessage"),
                    ExpiresAtUtc = GetNullableDateTime(row, "ExpiresAtUtc"),
                    AuthorizationToken = GetBytes(row, "AuthorizationToken")
                }),
            CancellationToken.None).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException(
                "SQL Server did not return the AuthPoint challenge status.");
    }

    public void CancelClientSecretMfaChallenge(
        Guid challengeId,
        byte[] challengeNonce)
    {
        if (challengeId == Guid.Empty || challengeNonce.Length != 32)
        {
            return;
        }

        ExecuteNonQueryAsync(
            Procedures.CancelClientSecretMfaChallenge,
            command =>
            {
                AddGuid(command, "@ChallengeId", challengeId);
                AddBinary(command, "@ChallengeNonce", 32, challengeNonce);
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            CancellationToken.None).GetAwaiter().GetResult();
    }

    public ClientInfoImportBatch ImportClientInfoWorkbook(
        ClientInfoWorkbookPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.ContentSha256.Length != 32)
        {
            throw new ArgumentException(
                "A SHA-256 workbook hash is required.",
                nameof(package));
        }

        var batch = QueryAsync(
            Procedures.BeginClientInfoImport,
            command =>
            {
                AddInt(command, "@ClientId", package.ClientId);
                AddRequiredText(
                    command,
                    "@TemplateVersion",
                    40,
                    package.TemplateVersion);
                AddGuid(command, "@WorkbookId", package.WorkbookId);
                AddBinary(
                    command,
                    "@ContentSha256",
                    32,
                    package.ContentSha256);
                AddRequiredText(
                    command,
                    "@SourceDisplayName",
                    260,
                    Path.GetFileName(package.SourcePath));
                AddDateTime(
                    command,
                    "@SourceModifiedAtUtc",
                    package.SourceModifiedAtUtc);
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            (reader, token) => ReadSingleAsync(
                reader,
                token,
                ReadClientInfoImportBatch),
            CancellationToken.None).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException(
                "SQL Server did not start the Client Info import.");

        if (batch.State is "Approved" or "Promoted")
        {
            return GetClientInfoImportBatch(batch.BatchId);
        }

        foreach (var record in package.Records)
        {
            ExecuteNonQueryAsync(
                Procedures.StageClientInfoRecord,
                command =>
                {
                    AddGuid(command, "@BatchId", batch.BatchId);
                    AddRequiredText(
                        command,
                        "@RecordType",
                        40,
                        record.RecordType);
                    AddRequiredText(
                        command,
                        "@LocalKey",
                        120,
                        record.LocalKey);
                    AddText(
                        command,
                        "@ParentLocalKey",
                        120,
                        record.ParentLocalKey);
                    AddMaxText(command, "@PayloadJson", record.PayloadJson);
                    AddText(
                        command,
                        "@SourceSheet",
                        128,
                        record.SourceSheet);
                    AddInt(command, "@SourceRow", record.SourceRow);
                    AddRequiredText(
                        command,
                        "@ReviewStatus",
                        24,
                        record.ReviewStatus);
                },
                CancellationToken.None).GetAwaiter().GetResult();
        }

        foreach (var secret in package.Secrets)
        {
            ExecuteNonQueryAsync(
                Procedures.StageClientInfoSecret,
                command =>
                {
                    AddGuid(command, "@BatchId", batch.BatchId);
                    AddRequiredText(
                        command,
                        "@CredentialLocalKey",
                        120,
                        secret.CredentialLocalKey);
                    AddRequiredText(
                        command,
                        "@SecretType",
                        80,
                        secret.SecretType);
                    AddRequiredText(
                        command,
                        "@SecretLabel",
                        200,
                        secret.SecretLabel);
                    AddMaxText(command, "@SecretValue", secret.SecretValue);
                },
                CancellationToken.None).GetAwaiter().GetResult();
        }

        _ = ValidateClientInfoImport(batch.BatchId);
        return CompareClientInfoImportToFireDrill(batch.BatchId);
    }

    public ClientInfoImportBatch GetClientInfoImportBatch(Guid batchId) =>
        QueryAsync(
            Procedures.GetClientInfoImportBatch,
            command => AddGuid(command, "@BatchId", batchId),
            ReadClientInfoImportBatchWithIssuesAsync,
            CancellationToken.None).GetAwaiter().GetResult()
        ?? throw new InvalidOperationException(
            "The Client Info import batch was not found.");

    public ClientInfoImportBatch ValidateClientInfoImport(Guid batchId) =>
        QueryAsync(
            Procedures.ValidateClientInfoImport,
            command =>
            {
                AddGuid(command, "@BatchId", batchId);
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            ReadClientInfoImportBatchWithIssuesAsync,
            CancellationToken.None).GetAwaiter().GetResult()
        ?? throw new InvalidOperationException(
            "SQL Server did not return the validated Client Info import.");

    public ClientInfoImportBatch CompareClientInfoImportToFireDrill(
        Guid batchId) =>
        QueryAsync(
            Procedures.CompareClientInfoImportToFireDrill,
            command =>
            {
                AddGuid(command, "@BatchId", batchId);
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            ReadClientInfoImportBatchWithIssuesAsync,
            CancellationToken.None).GetAwaiter().GetResult()
        ?? throw new InvalidOperationException(
            "SQL Server did not return the FireDrill comparison.");

    public void AcceptClientInfoImportUnverified(ClientInfoImportBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ExecuteNonQueryAsync(
            Procedures.AcceptClientInfoImportUnverified,
            command =>
            {
                AddGuid(command, "@BatchId", batch.BatchId);
                AddBinary(
                    command,
                    "@ExpectedRowVersion",
                    8,
                    batch.RowVersion);
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            CancellationToken.None).GetAwaiter().GetResult();
    }

    public void DiscardClientInfoImport(ClientInfoImportBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ExecuteNonQueryAsync(
            Procedures.DiscardClientInfoImport,
            command =>
            {
                AddGuid(command, "@BatchId", batch.BatchId);
                AddBinary(
                    command,
                    "@ExpectedRowVersion",
                    8,
                    batch.RowVersion);
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            CancellationToken.None).GetAwaiter().GetResult();
    }

    public ClientInfoImportBatch ApproveClientInfoImport(
        ClientInfoImportBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ExecuteNonQueryAsync(
            Procedures.ApproveClientInfoImport,
            command =>
            {
                AddGuid(command, "@BatchId", batch.BatchId);
                AddBinary(
                    command,
                    "@ExpectedRowVersion",
                    8,
                    batch.RowVersion);
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            CancellationToken.None).GetAwaiter().GetResult();
        return GetClientInfoImportBatch(batch.BatchId);
    }

    public void PromoteClientInfoImport(ClientInfoImportBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ExecuteNonQueryAsync(
            Procedures.PromoteClientInfoImport,
            command =>
            {
                AddGuid(command, "@BatchId", batch.BatchId);
                AddBinary(
                    command,
                    "@ExpectedRowVersion",
                    8,
                    batch.RowVersion);
                AddGuid(command, "@RequestId", Guid.NewGuid());
            },
            CancellationToken.None).GetAwaiter().GetResult();
    }

    private static ClientInfoClientSummary ReadClientInfoClientSummary(
        SqlDataReader reader) => new()
    {
        ClientId = GetInt32(reader, "ClientId"),
        ClientName = GetString(reader, "ClientName"),
        IsActive = GetBoolean(reader, "IsActive"),
        ReviewStatus = GetString(reader, "ReviewStatus", "Unverified"),
        CutoverState = GetString(reader, "CutoverState", "NotStarted"),
        IsLive = GetBoolean(reader, "IsLive"),
        UpdatedAtUtc = GetNullableDateTime(reader, "UpdatedAtUtc"),
        RowVersion = GetBytes(reader, "RowVersion"),
        LocationCount = GetInt64(reader, "LocationCount"),
        PersonCount = GetInt64(reader, "PersonCount"),
        ResourceCount = GetInt64(reader, "ResourceCount"),
        CredentialCount = GetInt64(reader, "CredentialCount")
    };

    private static ClientInfoProfile ReadClientInfoProfile(
        SqlDataReader reader) => new()
    {
        ClientId = GetInt32(reader, "ClientId"),
        ClientName = GetString(reader, "ClientName"),
        IsActive = GetBoolean(reader, "IsActive"),
        WhdContactName = GetString(reader, "WhdContactName"),
        WhdContactEmail = GetString(reader, "WhdContactEmail"),
        WhdPhone = GetString(reader, "WhdPhone"),
        WhdAddress = GetString(reader, "WhdAddress"),
        Summary = GetString(reader, "Summary"),
        ClientFolderPath = GetString(reader, "ClientFolderPath"),
        LegacyClientInfoSheetPath = GetString(
            reader,
            "LegacyClientInfoSheetPath"),
        ReviewStatus = GetString(reader, "ReviewStatus", "Unverified"),
        IsLive = GetBoolean(reader, "IsLive"),
        LastVerifiedAtUtc = GetNullableDateTime(
            reader,
            "LastVerifiedAtUtc"),
        UpdatedAtUtc = GetNullableDateTime(reader, "UpdatedAtUtc"),
        RowVersion = GetBytes(reader, "RowVersion"),
        CutoverState = GetString(reader, "CutoverState", "NotStarted"),
        CutoverRowVersion = GetBytes(reader, "CutoverRowVersion")
    };

    private static ClientInfoAttachment ReadClientInfoAttachment(
        SqlDataReader reader) => new()
    {
        AttachmentId = GetNullableGuid(reader, "AttachmentId") ?? Guid.Empty,
        ClientId = GetInt32(reader, "ClientId"),
        EquipmentId = GetNullableInt64(reader, "EquipmentId"),
        EquipmentName = GetString(reader, "EquipmentName"),
        EquipmentAssetTag = GetString(reader, "EquipmentAssetTag"),
        RelativePath = GetString(reader, "RelativePath"),
        OriginalFileName = GetString(reader, "OriginalFileName"),
        ContentType = GetString(
            reader,
            "ContentType",
            "application/octet-stream"),
        Category = GetString(reader, "Category", "Other"),
        Caption = GetString(reader, "Caption"),
        FileSizeBytes = GetInt64(reader, "FileSizeBytes"),
        ContentSha256 = GetBytes(reader, "ContentSha256") ?? [],
        UploadedBy = GetString(reader, "UploadedBy"),
        UploadedAtUtc = GetNullableDateTime(reader, "UploadedAtUtc") ?? default,
        IsArchived = GetBoolean(reader, "IsArchived"),
        ArchivedBy = GetString(reader, "ArchivedBy"),
        ArchivedAtUtc = GetNullableDateTime(reader, "ArchivedAtUtc"),
        RowVersion = GetBytes(reader, "RowVersion")
    };

    private static ClientInfoLocation ReadClientInfoLocation(
        SqlDataReader reader) => new()
    {
        LocationId = GetInt64(reader, "LocationId"),
        ClientId = GetInt32(reader, "ClientId"),
        LocalKey = GetString(reader, "LocalKey"),
        Name = GetString(reader, "Name"),
        LocationType = GetString(reader, "LocationType"),
        Address1 = GetString(reader, "Address1"),
        Address2 = GetString(reader, "Address2"),
        City = GetString(reader, "City"),
        StateProvince = GetString(reader, "StateProvince"),
        PostalCode = GetString(reader, "PostalCode"),
        MainPhone = GetString(reader, "MainPhone"),
        TimeZoneId = GetString(reader, "TimeZoneId"),
        IsPrimary = GetBoolean(reader, "IsPrimary"),
        ReviewStatus = GetString(reader, "ReviewStatus", "Unverified"),
        IsActive = GetBoolean(reader, "IsActive", true),
        LastVerifiedAtUtc = GetNullableDateTime(
            reader,
            "LastVerifiedAtUtc"),
        UpdatedAtUtc = GetNullableDateTime(reader, "UpdatedAtUtc"),
        RowVersion = GetBytes(reader, "RowVersion")
    };

    private static ClientInfoPerson ReadClientInfoPerson(
        SqlDataReader reader) => new()
    {
        PersonId = GetInt64(reader, "PersonId"),
        ClientId = GetInt32(reader, "ClientId"),
        LocationId = GetNullableInt64(reader, "LocationId"),
        LocationName = GetString(reader, "LocationName"),
        LocalKey = GetString(reader, "LocalKey"),
        DisplayName = GetString(reader, "DisplayName"),
        RoleDepartment = GetString(reader, "RoleDepartment"),
        AdUsername = GetString(reader, "AdUsername"),
        Email = GetString(reader, "Email"),
        HasMicrosoft365 = GetBoolean(reader, "HasMicrosoft365"),
        Microsoft365License = GetString(reader, "Microsoft365License"),
        PcName = GetString(reader, "PcName"),
        Phone = GetString(reader, "Phone"),
        MobilePhone = GetString(reader, "MobilePhone"),
        ContactType = GetString(reader, "ContactType"),
        IsPrimary = GetBoolean(reader, "IsPrimary"),
        ReviewStatus = GetString(reader, "ReviewStatus", "Unverified"),
        IsActive = GetBoolean(reader, "IsActive", true),
        LastVerifiedAtUtc = GetNullableDateTime(
            reader,
            "LastVerifiedAtUtc"),
        UpdatedAtUtc = GetNullableDateTime(reader, "UpdatedAtUtc"),
        RowVersion = GetBytes(reader, "RowVersion")
    };

    private static ClientInfoResource ReadClientInfoResource(
        SqlDataReader reader) => new()
    {
        ResourceId = GetInt64(reader, "ResourceId"),
        ClientId = GetInt32(reader, "ClientId"),
        LocationId = GetNullableInt64(reader, "LocationId"),
        LocationName = GetString(reader, "LocationName"),
        ParentResourceId = GetNullableInt64(reader, "ParentResourceId"),
        EquipmentId = GetNullableInt64(reader, "EquipmentId"),
        LocalKey = GetString(reader, "LocalKey"),
        ResourceType = GetString(reader, "ResourceType"),
        Name = GetString(reader, "Name"),
        Provider = GetString(reader, "Provider"),
        AddressOrUrl = GetString(reader, "AddressOrUrl"),
        Status = GetString(reader, "Status"),
        Notes = GetString(reader, "Notes"),
        ReviewStatus = GetString(reader, "ReviewStatus", "Unverified"),
        IsActive = GetBoolean(reader, "IsActive", true),
        LastVerifiedAtUtc = GetNullableDateTime(
            reader,
            "LastVerifiedAtUtc"),
        UpdatedAtUtc = GetNullableDateTime(reader, "UpdatedAtUtc"),
        RowVersion = GetBytes(reader, "RowVersion")
    };

    private static ClientInfoResourceField ReadClientInfoResourceField(
        SqlDataReader reader) => new()
    {
        ResourceFieldId = GetInt64(reader, "ResourceFieldId"),
        ResourceId = GetInt64(reader, "ResourceId"),
        FieldKey = GetString(reader, "FieldKey"),
        FieldLabel = GetString(reader, "FieldLabel"),
        ValueText = GetString(reader, "ValueText"),
        ValueType = GetString(reader, "ValueType", "Text"),
        SortOrder = GetInt32(reader, "SortOrder"),
        UpdatedAtUtc = GetNullableDateTime(reader, "UpdatedAtUtc"),
        RowVersion = GetBytes(reader, "RowVersion")
    };

    private static ClientInfoCredential ReadClientInfoCredential(
        SqlDataReader reader) => new()
    {
        CredentialId = GetInt64(reader, "CredentialId"),
        ClientId = GetInt32(reader, "ClientId"),
        ResourceId = GetNullableInt64(reader, "ResourceId"),
        PersonId = GetNullableInt64(reader, "PersonId"),
        LocalKey = GetString(reader, "LocalKey"),
        Name = GetString(reader, "Name"),
        Category = GetString(reader, "Category"),
        Username = GetString(reader, "Username"),
        LoginUrl = GetString(reader, "LoginUrl"),
        Notes = GetString(reader, "Notes"),
        ReviewStatus = GetString(reader, "ReviewStatus", "Unverified"),
        IsActive = GetBoolean(reader, "IsActive", true),
        LastVerifiedAtUtc = GetNullableDateTime(
            reader,
            "LastVerifiedAtUtc"),
        UpdatedAtUtc = GetNullableDateTime(reader, "UpdatedAtUtc"),
        RowVersion = GetBytes(reader, "RowVersion"),
        SecretCount = GetInt32(reader, "SecretCount")
    };

    private static ClientInfoSecretSummary ReadClientInfoSecretSummary(
        SqlDataReader reader) => new()
    {
        SecretId = GetInt64(reader, "SecretId"),
        CredentialId = GetInt64(reader, "CredentialId"),
        SecretType = GetString(reader, "SecretType"),
        SecretLabel = GetString(reader, "SecretLabel"),
        IsCurrent = GetBoolean(reader, "IsCurrent", true),
        LastVerifiedAtUtc = GetNullableDateTime(
            reader,
            "LastVerifiedAtUtc"),
        UpdatedAtUtc = GetNullableDateTime(reader, "UpdatedAtUtc"),
        RowVersion = GetBytes(reader, "RowVersion")
    };

    private static ClientInfoFact ReadClientInfoFact(
        SqlDataReader reader) => new()
    {
        FactId = GetInt64(reader, "FactId"),
        ClientId = GetInt32(reader, "ClientId"),
        LocalKey = GetString(reader, "LocalKey"),
        SectionName = GetString(reader, "SectionName"),
        FieldLabel = GetString(reader, "FieldLabel"),
        ValueText = GetString(reader, "ValueText"),
        ValueType = GetString(reader, "ValueType", "Text"),
        ReviewStatus = GetString(reader, "ReviewStatus", "Unverified"),
        SortOrder = GetInt32(reader, "SortOrder"),
        IsActive = GetBoolean(reader, "IsActive", true),
        LastVerifiedAtUtc = GetNullableDateTime(
            reader,
            "LastVerifiedAtUtc"),
        UpdatedAtUtc = GetNullableDateTime(reader, "UpdatedAtUtc"),
        RowVersion = GetBytes(reader, "RowVersion")
    };

    private static ClientInfoImportBatch ReadClientInfoImportBatch(
        SqlDataReader reader) => new()
    {
        BatchId = GetNullableGuid(reader, "BatchId") ?? Guid.Empty,
        ClientId = GetInt32(reader, "ClientId"),
        ClientName = GetString(reader, "ClientName"),
        TemplateVersion = GetString(reader, "TemplateVersion"),
        WorkbookId = GetNullableGuid(reader, "WorkbookId") ?? Guid.Empty,
        State = GetString(reader, "State", "Draft"),
        Message = GetString(reader, "Message"),
        CreatedAtUtc = GetNullableDateTime(reader, "CreatedAtUtc"),
        UpdatedAtUtc = GetNullableDateTime(reader, "UpdatedAtUtc"),
        ApprovedAtUtc = GetNullableDateTime(reader, "ApprovedAtUtc"),
        PromotedAtUtc = GetNullableDateTime(reader, "PromotedAtUtc"),
        RowVersion = GetBytes(reader, "RowVersion"),
        RecordCount = GetInt32(reader, "RecordCount"),
        SecretCount = GetInt32(reader, "SecretCount"),
        SecretMatchCount = GetInt32(reader, "SecretMatchCount"),
        SecretMismatchCount = GetInt32(reader, "SecretMismatchCount"),
        SecretWorkbookOnlyCount = GetInt32(
            reader,
            "SecretWorkbookOnlyCount"),
        BlockingIssueCount = GetInt32(reader, "BlockingIssueCount"),
        WarningCount = GetInt32(reader, "WarningCount")
    };

    private static async Task<ClientInfoSnapshot?> ReadClientInfoSnapshotAsync(
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var profile = ReadClientInfoProfile(reader);
        var locations = await ReadNextResultAsync(
            reader,
            cancellationToken,
            ReadClientInfoLocation).ConfigureAwait(false);
        var people = await ReadNextResultAsync(
            reader,
            cancellationToken,
            ReadClientInfoPerson).ConfigureAwait(false);
        var resources = await ReadNextResultAsync(
            reader,
            cancellationToken,
            ReadClientInfoResource).ConfigureAwait(false);
        var resourceFields = await ReadNextResultAsync(
            reader,
            cancellationToken,
            ReadClientInfoResourceField).ConfigureAwait(false);
        var credentials = await ReadNextResultAsync(
            reader,
            cancellationToken,
            ReadClientInfoCredential).ConfigureAwait(false);
        var secrets = await ReadNextResultAsync(
            reader,
            cancellationToken,
            ReadClientInfoSecretSummary).ConfigureAwait(false);
        var facts = await ReadNextResultAsync(
            reader,
            cancellationToken,
            ReadClientInfoFact).ConfigureAwait(false);
        var batches = await ReadNextResultAsync(
            reader,
            cancellationToken,
            ReadClientInfoImportBatch).ConfigureAwait(false);

        var fieldsByResource = resourceFields
            .GroupBy(field => field.ResourceId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ClientInfoResourceField>)group.ToArray());
        var secretsByCredential = secrets
            .GroupBy(secret => secret.CredentialId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ClientInfoSecretSummary>)group.ToArray());

        return new ClientInfoSnapshot
        {
            Profile = profile,
            Locations = locations,
            People = people,
            Resources = resources
                .Select(resource => resource with
                {
                    Fields = fieldsByResource.GetValueOrDefault(
                        resource.ResourceId,
                        [])
                })
                .ToArray(),
            Credentials = credentials
                .Select(credential => credential with
                {
                    Secrets = secretsByCredential.GetValueOrDefault(
                        credential.CredentialId,
                        [])
                })
                .ToArray(),
            Facts = facts,
            ImportBatches = batches
        };
    }

    private static async Task<ClientInfoImportBatch?> ReadClientInfoImportBatchWithIssuesAsync(
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var batch = ReadClientInfoImportBatch(reader);
        var issues = await ReadNextResultAsync(
            reader,
            cancellationToken,
            row => new ClientInfoImportIssue
            {
                IssueId = GetInt64(row, "IssueId"),
                ImportRecordId = GetNullableInt64(row, "ImportRecordId"),
                Severity = GetString(row, "Severity"),
                IssueCode = GetString(row, "IssueCode"),
                Message = GetString(row, "Message"),
                IsResolved = GetBoolean(row, "IsResolved"),
                ResolutionNote = GetString(row, "ResolutionNote"),
                ResolvedAtUtc = GetNullableDateTime(row, "ResolvedAtUtc"),
                RowVersion = GetBytes(row, "RowVersion")
            }).ConfigureAwait(false);
        return batch with { Issues = issues };
    }

    private static async Task<IReadOnlyList<T>> ReadNextResultAsync<T>(
        SqlDataReader reader,
        CancellationToken cancellationToken,
        Func<SqlDataReader, T> map)
    {
        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        return await ReadListAsync(
            reader,
            cancellationToken,
            map).ConfigureAwait(false);
    }
}
