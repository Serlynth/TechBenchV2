using System.Text.RegularExpressions;
using TechBench.Models;

namespace TechBench.Services;

internal static partial class CredentialFieldGrouper
{
    public const string WorkspaceSectionPrefix = "FireDrill::";

    private static readonly CredentialGroupRule[] KnownGroups =
    [
        new("Wireless", 5, ["wireless"]),
        new("WatchGuard", 10,
        [
            "watchguard", "firebox", "authpoint", "sslvpn",
            "ssl vpn", "csriadmin", "*if enabled"
        ]),
        new("Microsoft 365", 20,
        [
            "microsoft 365", "office 365", "365", "m365", "o365",
            "onmicrosoft", "azure", "entra", "tenant"
        ]),
        new("ESET", 30, ["eset"]),
        new("Barracuda", 40, ["barracuda"]),
        new("Veeam", 45, ["veeam"]),
        new("Active Directory", 50,
        [
            "active directory", "domain admin", "domain password",
            "domain controller", "domain login", "domain username",
            "domain account", "local domain", "ad auth", "ad user",
            "ad password", "ad admin"
        ]),
        new("Remote Access", 60,
        [
            "rustdesk", "rust pw", "rustpw", "screenconnect",
            "connectwise", "splashtop", "teamviewer"
        ])
    ];

    private static readonly HashSet<string> WatchGuardLegacyLabels =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "status",
            "admin",
            "csriadmin"
        };

    private static readonly HashSet<string> ActiveDirectoryLegacyLabels =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "ad",
            "domain",
            "domain name",
            "local ad",
            "local domain"
        };

    private static readonly HashSet<string> IgnoredLeadingKeywords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "account",
            "address",
            "admin",
            "all",
            "client",
            "company",
            "contact",
            "customer",
            "description",
            "email",
            "host",
            "id",
            "ip",
            "key",
            "login",
            "misc",
            "miscellaneous",
            "name",
            "note",
            "notes",
            "other",
            "pass",
            "password",
            "phone",
            "primary",
            "pw",
            "pwd",
            "secondary",
            "serial",
            "site",
            "status",
            "url",
            "user",
            "username"
        };

    private static readonly IReadOnlyDictionary<string, CredentialGroup>
        NoRepeatedKeywords =
            new Dictionary<string, CredentialGroup>(
                StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<FireDrillCredentialFieldGroup> Group(
        IEnumerable<FireDrillCredentialField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var orderedFields = fields
            .OrderBy(field => field.SortOrder)
            .ThenBy(field => field.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var repeatedKeywords =
            DiscoverRepeatedLeadingKeywordGroups(orderedFields);
        var groupedFields = orderedFields
            .GroupBy(field => ResolveGroup(field, repeatedKeywords));

        return groupedFields
            .Select(group => new FireDrillCredentialFieldGroup(
                group.Key.Name,
                group.Key.SortOrder,
                group.ToArray()))
            .OrderBy(group => group.SortOrder)
            .ThenBy(group => group.Fields.Min(field => field.SortOrder))
            .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<FireDrillWorkspaceSection>
        DiscoverWorkspaceSections(
            IEnumerable<FireDrillCredentialField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var uniqueFields = fields
            .OrderBy(field => field.SortOrder)
            .ThenBy(field => field.Label, StringComparer.OrdinalIgnoreCase)
            .GroupBy(GetFieldKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var assignedFieldKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sections = new List<FireDrillWorkspaceSection>();

        foreach (var group in Group(uniqueFields))
        {
            var fieldKeys = group.Fields
                .Select(GetFieldKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (group.Name.Equals("Other", StringComparison.OrdinalIgnoreCase)
                || fieldKeys.Length < 2)
            {
                continue;
            }

            sections.Add(new FireDrillWorkspaceSection(
                group.Name,
                GetWorkspaceDisplayName(group.Name),
                CreateWorkspaceSectionKey(group.Name),
                group.SortOrder,
                fieldKeys));
            assignedFieldKeys.UnionWith(fieldKeys);
        }

        var miscellaneousFieldKeys = uniqueFields
            .Select(GetFieldKey)
            .Where(fieldKey => !assignedFieldKeys.Contains(fieldKey))
            .ToArray();
        if (miscellaneousFieldKeys.Length > 0)
        {
            sections.Add(new FireDrillWorkspaceSection(
                "Other",
                "Miscellaneous",
                CreateWorkspaceSectionKey("Other"),
                1000,
                miscellaneousFieldKeys));
        }

        return sections
            .OrderBy(section => section.SortOrder)
            .ThenBy(section => section.DisplayName,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool IsWorkspaceSectionKey(string? sectionKey) =>
        sectionKey?.StartsWith(
            WorkspaceSectionPrefix,
            StringComparison.Ordinal) == true;

    public static bool IsFieldInWorkspaceSection(
        FireDrillCredentialField field,
        FireDrillWorkspaceSection section)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(section);
        var fieldKey = GetFieldKey(field);
        return section.FieldKeys.Contains(
            fieldKey,
            StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsWirelessField(FireDrillCredentialField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return Normalize(field.Label).StartsWith("wireless", StringComparison.Ordinal) ||
               Normalize(field.FieldName).StartsWith("wireless", StringComparison.Ordinal);
    }

    public static bool IsDomainOrAdField(FireDrillCredentialField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return ResolveGroup(field).Name.Equals("Active Directory", StringComparison.Ordinal);
    }

    public static bool IsConnectionField(FireDrillCredentialField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return ResolveGroup(field).Name.Equals("WatchGuard", StringComparison.Ordinal);
    }

    public static bool IsVeeamField(FireDrillCredentialField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return ResolveGroup(field).Name.Equals("Veeam", StringComparison.Ordinal);
    }

    public static bool IsMiscInfoField(FireDrillCredentialField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return !IsWirelessField(field) &&
               !IsDomainOrAdField(field) &&
               !IsConnectionField(field) &&
               !IsVeeamField(field);
    }

    public static FireDrillCredentialField CreateWirelessDisplayField(
        FireDrillCredentialField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        var displayLabel = WirelessLabelPrefixRegex()
            .Replace(field.Label ?? string.Empty, string.Empty)
            .Trim();
        return string.IsNullOrWhiteSpace(displayLabel)
            ? field
            : field with { Label = displayLabel };
    }

    public static FireDrillCredentialFieldGroup? CreateWirelessSectionGroup(
        IEnumerable<FireDrillCredentialField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var wirelessFields = fields
            .Where(IsWirelessField)
            .Select(CreateWirelessDisplayField)
            .OrderBy(field => field.SortOrder)
            .ThenBy(field => field.Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return wirelessFields.Length == 0
            ? null
            : new FireDrillCredentialFieldGroup(
                "Wireless",
                5,
                wirelessFields);
    }

    private static CredentialGroup ResolveGroup(
        FireDrillCredentialField field) =>
        ResolveGroup(field, NoRepeatedKeywords);

    private static CredentialGroup ResolveGroup(
        FireDrillCredentialField field,
        IReadOnlyDictionary<string, CredentialGroup> repeatedKeywords)
    {
        var label = Normalize(field.Label);
        var fieldName = Normalize(field.FieldName);
        var searchable = $"{label} {fieldName}";

        if (WatchGuardLegacyLabels.Contains(label))
        {
            return new("WatchGuard", 10);
        }

        if (ActiveDirectoryLegacyLabels.Contains(label))
        {
            return new("Active Directory", 50);
        }

        foreach (var rule in KnownGroups)
        {
            if (rule.Terms.Any(term =>
                searchable.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                return new(rule.Name, rule.SortOrder);
            }
        }

        if (TryGetLeadingKeyword(field, out var leadingKeyword)
            && repeatedKeywords.TryGetValue(
                Normalize(leadingKeyword),
                out var repeatedKeywordGroup))
        {
            return repeatedKeywordGroup;
        }

        var inferredProvider = InferProviderName(field.Label);
        return string.IsNullOrWhiteSpace(inferredProvider)
            ? new("Other", 1000)
            : new(inferredProvider, 500);
    }

    private static IReadOnlyDictionary<string, CredentialGroup>
        DiscoverRepeatedLeadingKeywordGroups(
            IReadOnlyList<FireDrillCredentialField> fields)
    {
        return fields
            .Select(field => new
            {
                Field = field,
                Keyword = TryGetLeadingKeyword(field, out var keyword)
                    ? keyword
                    : string.Empty
            })
            .Where(candidate =>
                !string.IsNullOrWhiteSpace(candidate.Keyword)
                && !IgnoredLeadingKeywords.Contains(candidate.Keyword))
            .GroupBy(
                candidate => Normalize(candidate.Keyword),
                StringComparer.OrdinalIgnoreCase)
            .Where(group => group
                .Select(candidate => GetFieldKey(candidate.Field))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .Count() >= 2)
            .ToDictionary(
                group => group.Key,
                group => new CredentialGroup(
                    FormatKeyword(group.First().Keyword),
                    400 + group.Min(candidate =>
                        candidate.Field.SortOrder)),
                StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryGetLeadingKeyword(
        FireDrillCredentialField field,
        out string keyword)
    {
        var source = string.IsNullOrWhiteSpace(field.Label)
            ? field.FieldName
            : field.Label;
        var match = LeadingKeywordRegex().Match(source ?? string.Empty);
        keyword = match.Success
            ? match.Groups["keyword"].Value
            : string.Empty;
        return !string.IsNullOrWhiteSpace(keyword);
    }

    private static string GetFieldKey(
        FireDrillCredentialField field)
    {
        var fieldName = Normalize(field.FieldName);
        return string.IsNullOrWhiteSpace(fieldName)
            ? Normalize(field.Label)
            : fieldName;
    }

    private static string GetWorkspaceDisplayName(string groupName) =>
        groupName switch
        {
            "Wireless" => "WiFi",
            "WatchGuard" => "Connections",
            "Active Directory" => "Domain & AD",
            _ => groupName
        };

    private static string CreateWorkspaceSectionKey(string groupName) =>
        $"{WorkspaceSectionPrefix}{groupName}";

    private static string FormatKeyword(string keyword)
    {
        var trimmed = keyword.Trim();
        if (trimmed.Any(char.IsUpper)
            && trimmed.Any(char.IsLower))
        {
            return trimmed;
        }

        if (trimmed.Length <= 4)
        {
            return trimmed.ToUpperInvariant();
        }

        return char.ToUpperInvariant(trimmed[0])
               + trimmed[1..].ToLowerInvariant();
    }

    private static string? InferProviderName(string label)
    {
        var match = ProviderCredentialSuffixRegex().Match(label.Trim());
        if (!match.Success)
        {
            return null;
        }

        var provider = match.Groups["provider"].Value
            .Trim(' ', '-', '_', '/', '\\', ':');
        if (provider.Length < 2 ||
            provider.Equals("admin", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals("user", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return provider;
    }

    private static string Normalize(string value) =>
        WhitespaceRegex().Replace(value ?? string.Empty, " ").Trim().ToLowerInvariant();

    [GeneratedRegex(
        @"^(?<provider>.+?)[\s\-_/\\:]+(?:user(?:name)?|login|password|pass|pwd|pw|account|email|token|api[\s\-]*key)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProviderCredentialSuffixRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(
        @"^\s*wireless(?=\s|[-_/\\:]|$)[\s\-_/\\:]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WirelessLabelPrefixRegex();

    [GeneratedRegex(
        @"^\s*[*#-]*\s*(?<keyword>[\p{L}]{2,})(?=[\p{N}\s\-_/\\:().]|$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex LeadingKeywordRegex();

    private sealed record CredentialGroup(string Name, int SortOrder);

    private sealed record CredentialGroupRule(
        string Name,
        int SortOrder,
        IReadOnlyList<string> Terms);
}
