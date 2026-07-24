using System.Text.RegularExpressions;
using TechBench.Models;

namespace TechBench.Services;

internal static partial class CredentialFieldGrouper
{
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
        new("Active Directory", 50,
        [
            "active directory", "domain admin", "domain password",
            "ad auth", "ad user", "ad password"
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

    public static IReadOnlyList<FireDrillCredentialFieldGroup> Group(
        IEnumerable<FireDrillCredentialField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var groupedFields = fields
            .OrderBy(field => field.SortOrder)
            .ThenBy(field => field.Label, StringComparer.OrdinalIgnoreCase)
            .GroupBy(ResolveGroup);

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

    public static bool IsWirelessField(FireDrillCredentialField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return Normalize(field.Label).StartsWith("wireless", StringComparison.Ordinal) ||
               Normalize(field.FieldName).StartsWith("wireless", StringComparison.Ordinal);
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

    private static CredentialGroup ResolveGroup(FireDrillCredentialField field)
    {
        var label = Normalize(field.Label);
        var fieldName = Normalize(field.FieldName);
        var searchable = $"{label} {fieldName}";

        if (WatchGuardLegacyLabels.Contains(label))
        {
            return new("WatchGuard", 10);
        }

        foreach (var rule in KnownGroups)
        {
            if (rule.Terms.Any(term =>
                searchable.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                return new(rule.Name, rule.SortOrder);
            }
        }

        var inferredProvider = InferProviderName(field.Label);
        return string.IsNullOrWhiteSpace(inferredProvider)
            ? new("Other", 1000)
            : new(inferredProvider, 500);
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

    private sealed record CredentialGroup(string Name, int SortOrder);

    private sealed record CredentialGroupRule(
        string Name,
        int SortOrder,
        IReadOnlyList<string> Terms);
}
