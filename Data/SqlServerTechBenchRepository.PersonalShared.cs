using System.Text.Json;
using Microsoft.Data.SqlClient;
using TechBench.Models;

namespace TechBench.Data;

public sealed partial class SqlServerTechBenchRepository
{
    public IReadOnlyList<OrganizationTag> GetOrganizationTags() =>
        GetOrganizationTagsAsync().GetAwaiter().GetResult();

    public Task<IReadOnlyList<OrganizationTag>> GetOrganizationTagsAsync(
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            Procedures.GetOrganizationTags,
            null,
            (reader, token) => ReadListAsync(reader, token, ReadOrganizationTag),
            cancellationToken);

    public int SaveOrganizationTag(OrganizationTag tag) =>
        SaveOrganizationTagAsync(tag).GetAwaiter().GetResult();

    public async Task<int> SaveOrganizationTagAsync(
        OrganizationTag tag,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tag);
        var saved = await QueryAsync(
                Procedures.SaveOrganizationTag,
                command =>
                {
                    AddInt(command, "@Id", tag.Id > 0 ? tag.Id : null);
                    AddRequiredText(command, "@Tag", 1000, tag.Tag);
                    AddBinary(
                        command,
                        "@ExpectedRowVersion",
                        8,
                        tag.RowVersion
                        ?? GetTrackedRowVersion("OrganizationTag", tag.Id));
                    AddGuid(command, "@RequestId", Guid.NewGuid());
                },
                (reader, token) =>
                    ReadSingleAsync(reader, token, ReadOrganizationTag),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"{Procedures.SaveOrganizationTag} did not return the saved tag.");
        tag.Id = saved.Id;
        tag.Tag = saved.Tag;
        tag.UpdatedAt = saved.UpdatedAt;
        tag.RowVersion = saved.RowVersion;
        return tag.Id;
    }

    public void DeleteOrganizationTag(OrganizationTag tag) =>
        DeleteOrganizationTagAsync(tag).GetAwaiter().GetResult();

    public async Task DeleteOrganizationTagAsync(
        OrganizationTag tag,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tag);
        await ExecuteNonQueryAsync(
                Procedures.DeleteOrganizationTag,
                command =>
                {
                    AddInt(command, "@Id", tag.Id);
                    AddBinary(
                        command,
                        "@ExpectedRowVersion",
                        8,
                        tag.RowVersion
                        ?? GetTrackedRowVersion("OrganizationTag", tag.Id));
                    AddGuid(command, "@RequestId", Guid.NewGuid());
                },
                cancellationToken)
            .ConfigureAwait(false);
        _rowVersions.TryRemove(
            BuildRowVersionKey("OrganizationTag", tag.Id),
            out _);
    }

    public IReadOnlyList<NoteTemplate> GetTemplates() =>
        GetTemplatesAsync().GetAwaiter().GetResult();

    public Task<IReadOnlyList<NoteTemplate>> GetTemplatesAsync(
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            Procedures.GetTemplates,
            null,
            (reader, token) => ReadListAsync(reader, token, ReadTemplate),
            cancellationToken);

    public int SaveTemplate(NoteTemplate template) =>
        SaveTemplateAsync(template).GetAwaiter().GetResult();

    public async Task<int> SaveTemplateAsync(
        NoteTemplate template,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);
        var saved = await QueryAsync(
                Procedures.SaveTemplate,
                command =>
                {
                    AddInt(command, "@Id", template.Id > 0 ? template.Id : null);
                    AddRequiredText(command, "@ScopeType", 20, template.ScopeType);
                    AddRequiredText(command, "@Name", 160, template.Name);
                    AddText(command, "@Category", 160, template.Category);
                    AddMaxText(command, "@TemplateText", template.TemplateText);
                    AddBinary(
                        command,
                        "@ExpectedRowVersion",
                        8,
                        template.RowVersion
                        ?? GetTrackedRowVersion("Template", template.Id));
                    AddGuid(command, "@RequestId", Guid.NewGuid());
                },
                (reader, token) => ReadSingleAsync(reader, token, ReadTemplate),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"{Procedures.SaveTemplate} did not return the saved template.");
        template.Id = saved.Id;
        template.ScopeType = saved.ScopeType;
        template.Name = saved.Name;
        template.Category = saved.Category;
        template.TemplateText = saved.TemplateText;
        template.RowVersion = saved.RowVersion;
        return template.Id;
    }

    public void DeleteTemplate(int id) =>
        DeleteTemplateAsync(id).GetAwaiter().GetResult();

    public async Task DeleteTemplateAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        await ExecuteNonQueryAsync(
                Procedures.DeleteTemplate,
                command =>
                {
                    AddInt(command, "@Id", id);
                    AddBinary(
                        command,
                        "@ExpectedRowVersion",
                        8,
                        GetTrackedRowVersion("Template", id));
                    AddGuid(command, "@RequestId", Guid.NewGuid());
                },
                cancellationToken)
            .ConfigureAwait(false);
        _rowVersions.TryRemove(BuildRowVersionKey("Template", id), out _);
    }

    public EditorDraft? GetEditorDraft() =>
        GetEditorDraftAsync().GetAwaiter().GetResult();

    public Task<EditorDraft?> GetEditorDraftAsync(
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            Procedures.GetEditorDraft,
            command => AddGuid(command, "@DeviceId", DeviceId),
            async (reader, token) =>
            {
                if (!await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    return null;
                }

                var draft = ReadEditorDraft(reader);
                var rowVersion = GetBytes(reader, "RowVersion");
                if (rowVersion is { Length: > 0 })
                {
                    _rowVersions[BuildRowVersionKey("EditorDraft", DeviceId.GetHashCode())] =
                        rowVersion;
                }

                return draft;
            },
            cancellationToken);

    public void SaveEditorDraft(EditorDraft draft) =>
        SaveEditorDraftAsync(draft).GetAwaiter().GetResult();

    public async Task SaveEditorDraftAsync(
        EditorDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        await QueryAsync(
                Procedures.SaveEditorDraft,
                command =>
                {
                    AddGuid(command, "@DeviceId", DeviceId);
                    AddMaxText(command, "@Payload", SerializePayload(draft));
                    AddBinary(
                        command,
                        "@ExpectedRowVersion",
                        8,
                        draft.RowVersion
                        ?? GetTrackedRowVersion("EditorDraft", DeviceId.GetHashCode()));
                },
                async (reader, token) =>
                {
                    if (!await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        return false;
                    }

                    var rowVersion = GetBytes(reader, "RowVersion");
                    draft.RowVersion = rowVersion;
                    if (rowVersion is { Length: > 0 })
                    {
                        _rowVersions[
                            BuildRowVersionKey("EditorDraft", DeviceId.GetHashCode())] =
                            rowVersion;
                    }

                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public void ClearEditorDraft() =>
        ClearEditorDraftAsync().GetAwaiter().GetResult();

    public async Task ClearEditorDraftAsync(
        CancellationToken cancellationToken = default)
    {
        await ExecuteNonQueryAsync(
                Procedures.DeleteEditorDraft,
                command =>
                {
                    AddGuid(command, "@DeviceId", DeviceId);
                    AddBinary(
                        command,
                        "@ExpectedRowVersion",
                        8,
                        GetTrackedRowVersion("EditorDraft", DeviceId.GetHashCode()));
                },
                cancellationToken)
            .ConfigureAwait(false);
        _rowVersions.TryRemove(
            BuildRowVersionKey("EditorDraft", DeviceId.GetHashCode()),
            out _);
    }

    public IReadOnlyDictionary<string, int> GetClientAliases() =>
        GetClientAliasesAsync().GetAwaiter().GetResult();

    public Task<IReadOnlyDictionary<string, int>> GetClientAliasesAsync(
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            Procedures.GetClientAliases,
            null,
            async (reader, token) =>
            {
                var aliases = new Dictionary<string, int>(
                    StringComparer.OrdinalIgnoreCase);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    var alias = GetString(reader, "Alias");
                    var clientId = GetInt32(reader, "ClientId");
                    if (!string.IsNullOrWhiteSpace(alias) && clientId > 0)
                    {
                        aliases[alias] = clientId;
                        var aliasId = GetInt64(reader, "Id");
                        if (aliasId > 0
                            && GetString(reader, "ScopeType", OrganizationScope)
                                .Equals(OrganizationScope, StringComparison.OrdinalIgnoreCase))
                        {
                            _clientAliasIds[alias] = aliasId;
                            TrackRowVersion("ClientAlias", aliasId, reader);
                        }
                    }
                }

                return (IReadOnlyDictionary<string, int>)aliases;
            },
            cancellationToken);

    public void SaveClientAlias(string alias, int clientId) =>
        SaveClientAliasAsync(alias, clientId).GetAwaiter().GetResult();

    public async Task SaveClientAliasAsync(
        string alias,
        int clientId,
        CancellationToken cancellationToken = default)
    {
        var normalizedAlias = alias.Trim();
        var aliasId = _clientAliasIds.TryGetValue(normalizedAlias, out var existingId)
            ? existingId
            : 0;
        await QueryAsync(
                Procedures.SaveClientAlias,
                command =>
                {
                    AddBigInt(command, "@Id", aliasId > 0 ? aliasId : null);
                    AddRequiredText(command, "@ScopeType", 20, OrganizationScope);
                    AddRequiredText(command, "@Alias", 240, normalizedAlias);
                    AddInt(command, "@ClientId", clientId);
                    AddBinary(
                        command,
                        "@ExpectedRowVersion",
                        8,
                        GetTrackedRowVersion("ClientAlias", aliasId));
                    AddGuid(command, "@RequestId", Guid.NewGuid());
                },
                async (reader, token) =>
                {
                    if (!await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        return false;
                    }

                    var returnedId = GetInt64(reader, "Id");
                    if (returnedId > 0)
                    {
                        _clientAliasIds[normalizedAlias] = returnedId;
                        TrackRowVersion("ClientAlias", returnedId, reader);
                    }

                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DeleteClientAliasAsync(
        string alias,
        CancellationToken cancellationToken = default)
    {
        var normalizedAlias = alias.Trim();
        if (!_clientAliasIds.TryGetValue(normalizedAlias, out var aliasId)
            || aliasId <= 0)
        {
            return;
        }

        await ExecuteNonQueryAsync(
                Procedures.DeleteClientAlias,
                command =>
                {
                    AddBigInt(command, "@Id", aliasId);
                    AddBinary(
                        command,
                        "@ExpectedRowVersion",
                        8,
                        GetTrackedRowVersion("ClientAlias", aliasId));
                    AddGuid(command, "@RequestId", Guid.NewGuid());
                },
                cancellationToken)
            .ConfigureAwait(false);
        _clientAliasIds.TryRemove(normalizedAlias, out _);
        _rowVersions.TryRemove(BuildRowVersionKey("ClientAlias", aliasId), out _);
    }

    public IReadOnlyList<CommonLink> GetCommonLinks() =>
        GetCommonLinksAsync().GetAwaiter().GetResult();

    public Task<IReadOnlyList<CommonLink>> GetCommonLinksAsync(
        CancellationToken cancellationToken = default) =>
        QueryAsync(
            Procedures.GetCommonLinks,
            null,
            (reader, token) => ReadListAsync(reader, token, ReadCommonLink),
            cancellationToken);

    public int SaveCommonLink(CommonLink link) =>
        SaveCommonLinkAsync(link).GetAwaiter().GetResult();

    public async Task<int> SaveCommonLinkAsync(
        CommonLink link,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        var saved = await QueryAsync(
                Procedures.SaveCommonLink,
                command =>
                {
                    AddInt(command, "@Id", link.Id > 0 ? link.Id : null);
                    AddRequiredText(command, "@ScopeType", 20, link.ScopeType);
                    AddRequiredText(command, "@Name", 160, link.Name);
                    AddRequiredText(command, "@Url", 2048, link.Url);
                    AddInt(command, "@SortOrder", link.SortOrder);
                    AddText(command, "@BuiltInKey", 120, link.BuiltInKey);
                    AddBinary(
                        command,
                        "@ExpectedRowVersion",
                        8,
                        link.RowVersion
                        ?? GetTrackedRowVersion("CommonLink", link.Id));
                    AddGuid(command, "@RequestId", Guid.NewGuid());
                },
                (reader, token) => ReadSingleAsync(reader, token, ReadCommonLink),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"{Procedures.SaveCommonLink} did not return the saved link.");
        CopyCommonLink(saved, link);
        return link.Id;
    }

    public void DeleteCommonLink(int id) =>
        DeleteCommonLinkAsync(id).GetAwaiter().GetResult();

    public async Task DeleteCommonLinkAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        await ExecuteNonQueryAsync(
                Procedures.DeleteCommonLink,
                command =>
                {
                    AddInt(command, "@Id", id);
                    AddBinary(
                        command,
                        "@ExpectedRowVersion",
                        8,
                        GetTrackedRowVersion("CommonLink", id));
                    AddGuid(command, "@RequestId", Guid.NewGuid());
                },
                cancellationToken)
            .ConfigureAwait(false);
        _rowVersions.TryRemove(BuildRowVersionKey("CommonLink", id), out _);
    }

    public IReadOnlyDictionary<string, string> GetSettings() =>
        GetSettingsAsync().GetAwaiter().GetResult();

    public async Task<IReadOnlyDictionary<string, string>> GetSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        foreach (var trackedKey in _rowVersions.Keys.Where(
                     static key => key.StartsWith("Setting:", StringComparison.Ordinal)))
        {
            _rowVersions.TryRemove(trackedKey, out _);
        }

        return await QueryAsync(
            Procedures.GetSettings,
            null,
            async (reader, token) =>
            {
                var settings = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    var key = GetString(
                        reader,
                        "SettingKey",
                        GetString(reader, "Key"));
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    if (_connectionFactory.IsReadOnlyPreview
                        && IsPersonalSecretSetting(key))
                    {
                        // Never expose or migrate another user's legacy secret
                        // while an Admin is previewing that user's workspace.
                        continue;
                    }

                    settings[key] = GetString(
                        reader,
                        "SettingValue",
                        GetString(reader, "Value"));
                    var rowVersion = GetBytes(reader, "RowVersion");
                    var scopeType = GetString(reader, "ScopeType", UserSettingScope);
                    if (rowVersion is { Length: > 0 })
                    {
                        _rowVersions[BuildSettingRowVersionKey(scopeType, key)] = rowVersion;
                    }
                }

                return (IReadOnlyDictionary<string, string>)settings;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsPersonalSecretSetting(string key) =>
        key.Equals("Whd.ApiToken", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Sage.Password", StringComparison.OrdinalIgnoreCase);

    public string GetSetting(string key, string fallback = "") =>
        GetSettingAsync(key, fallback).GetAwaiter().GetResult();

    public async Task<string> GetSettingAsync(
        string key,
        string fallback = "",
        CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        return settings.TryGetValue(key, out var value) ? value : fallback;
    }

    public void SaveSetting(string key, string value) =>
        SaveSettingAsync(key, value).GetAwaiter().GetResult();

    public async Task SaveSettingAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        await QueryAsync(
                Procedures.SaveSetting,
                command =>
                {
                    AddRequiredText(command, "@SettingKey", 200, key);
                    AddMaxText(command, "@SettingValue", value);
                    AddBinary(
                        command,
                        "@ExpectedRowVersion",
                        8,
                        GetSettingRowVersion(key));
                },
                async (reader, token) =>
                {
                    if (!await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        return false;
                    }

                    var rowVersion = GetBytes(reader, "RowVersion");
                    if (rowVersion is { Length: > 0 })
                    {
                        _rowVersions[BuildSettingRowVersionKey(key)] = rowVersion;
                    }

                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public void DeleteSetting(string key) =>
        DeleteSettingAsync(key).GetAwaiter().GetResult();

    public async Task DeleteSettingAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        await ExecuteNonQueryAsync(
                Procedures.DeleteSetting,
                command =>
                {
                    AddRequiredText(command, "@SettingKey", 200, key);
                    AddBinary(
                        command,
                        "@ExpectedRowVersion",
                        8,
                        GetSettingRowVersion(key));
                },
                cancellationToken)
            .ConfigureAwait(false);
        _rowVersions.TryRemove(BuildSettingRowVersionKey(key), out _);
    }

    public void SaveOrganizationSetting(string key, string value) =>
        SaveOrganizationSettingAsync(key, value).GetAwaiter().GetResult();

    public async Task SaveOrganizationSettingAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        await QueryAsync(
                Procedures.SaveOrganizationSetting,
                command =>
                {
                    AddRequiredText(command, "@SettingKey", 200, key);
                    AddMaxText(command, "@SettingValue", value);
                    AddBinary(
                        command,
                        "@ExpectedRowVersion",
                        8,
                        GetSettingRowVersion(key, OrganizationScope));
                    AddGuid(command, "@RequestId", Guid.NewGuid());
                },
                async (reader, token) =>
                {
                    if (!await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        return false;
                    }

                    var rowVersion = GetBytes(reader, "RowVersion");
                    if (rowVersion is { Length: > 0 })
                    {
                        _rowVersions[
                            BuildSettingRowVersionKey(OrganizationScope, key)] = rowVersion;
                    }

                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public void DeleteOrganizationSetting(string key) =>
        DeleteOrganizationSettingAsync(key).GetAwaiter().GetResult();

    public async Task DeleteOrganizationSettingAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        await ExecuteNonQueryAsync(
                Procedures.DeleteOrganizationSetting,
                command =>
                {
                    AddRequiredText(command, "@SettingKey", 200, key);
                    AddBinary(
                        command,
                        "@ExpectedRowVersion",
                        8,
                        GetSettingRowVersion(key, OrganizationScope));
                    AddGuid(command, "@RequestId", Guid.NewGuid());
                },
                cancellationToken)
            .ConfigureAwait(false);
        _rowVersions.TryRemove(
            BuildSettingRowVersionKey(OrganizationScope, key),
            out _);
    }

    private NoteTemplate ReadTemplate(SqlDataReader reader)
    {
        var template = new NoteTemplate
        {
            Id = GetInt32(reader, "Id"),
            ScopeType = GetString(reader, "ScopeType", "User"),
            Name = GetString(reader, "Name"),
            Category = GetString(reader, "Category"),
            TemplateText = GetString(reader, "TemplateText")
        };
        template.RowVersion = GetBytes(reader, "RowVersion");
        TrackRowVersion("Template", template.Id, reader);
        return template;
    }

    private EditorDraft ReadEditorDraft(SqlDataReader reader)
    {
        var payload = GetNullableString(reader, "Payload");
        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                return JsonSerializer.Deserialize<EditorDraft>(
                           payload,
                           PayloadJsonOptions)
                       ?? new EditorDraft();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"{Procedures.GetEditorDraft} returned invalid draft JSON.",
                    ex);
            }
        }

        return new EditorDraft
        {
            WorkEntryId = GetInt32(reader, "WorkEntryId"),
            WorkDate = GetDate(reader, "WorkDate", DateTime.Today),
            ClientId = GetNullableInt32(reader, "ClientId"),
            UseManualClient = GetBoolean(reader, "UseManualClient"),
            ManualClientName = GetString(reader, "ManualClientName"),
            TicketId = GetNullableInt32(reader, "TicketId"),
            ManualTicketNumber = GetString(reader, "ManualTicketNumber"),
            StartTimeText = GetString(reader, "StartTimeText"),
            EndTimeText = GetString(reader, "EndTimeText"),
            DurationMinutesText = GetString(reader, "DurationMinutesText"),
            Billable = GetBoolean(reader, "Billable", true),
            Note = GetString(reader, "Note"),
            InternalNote = GetString(reader, "InternalNote"),
            IncludePersonalNoteInWhd =
                GetBoolean(reader, "IncludePersonalNoteInWhd"),
            Tags = GetString(reader, "Tags"),
            FollowUpState = GetEnum(reader, "FollowUpState", FollowUpState.None),
            FollowUpDueDate = GetNullableDateTime(reader, "FollowUpDueDate")?.Date,
            PendingFollowUpSourceId =
                GetNullableInt32(reader, "PendingFollowUpSourceId"),
            UpdatedAt = GetDateTime(reader, "UpdatedAt", DateTime.Now)
        };
    }

    private CommonLink ReadCommonLink(SqlDataReader reader)
    {
        var link = new CommonLink
        {
            Id = GetInt32(reader, "Id"),
            ScopeType = GetString(reader, "ScopeType", "User"),
            Name = GetString(reader, "Name"),
            Url = GetString(reader, "Url"),
            SortOrder = GetInt32(reader, "SortOrder"),
            BuiltInKey = GetNullableString(reader, "BuiltInKey"),
            CreatedAt = GetDateTime(reader, "CreatedAt", DateTime.Now),
            UpdatedAt = GetDateTime(reader, "UpdatedAt", DateTime.Now)
        };
        link.RowVersion = GetBytes(reader, "RowVersion");
        TrackRowVersion("CommonLink", link.Id, reader);
        return link;
    }

    private OrganizationTag ReadOrganizationTag(SqlDataReader reader)
    {
        var tag = new OrganizationTag
        {
            Id = GetInt32(reader, "Id"),
            Tag = GetString(reader, "Tag"),
            UpdatedAt = GetDateTime(reader, "UpdatedAt", DateTime.Now),
            RowVersion = GetBytes(reader, "RowVersion")
        };
        TrackRowVersion("OrganizationTag", tag.Id, reader);
        return tag;
    }

    private static string BuildSettingRowVersionKey(string scopeType, string key) =>
        $"Setting:{scopeType.Trim()}:{key.Trim()}";

    private static string BuildSettingRowVersionKey(string key) =>
        BuildSettingRowVersionKey(UserSettingScope, key);

    private byte[]? GetSettingRowVersion(
        string key,
        string scopeType = UserSettingScope) =>
        _rowVersions.TryGetValue(
            BuildSettingRowVersionKey(scopeType, key),
            out var rowVersion)
            ? rowVersion
            : null;

    private static void CopyCommonLink(CommonLink source, CommonLink target)
    {
        target.Id = source.Id;
        target.ScopeType = source.ScopeType;
        target.Name = source.Name;
        target.Url = source.Url;
        target.SortOrder = source.SortOrder;
        target.BuiltInKey = source.BuiltInKey;
        target.CreatedAt = source.CreatedAt;
        target.UpdatedAt = source.UpdatedAt;
        target.RowVersion = source.RowVersion;
    }
}
