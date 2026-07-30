using System.Text;
using TechBench.Models;

namespace TechBench.ViewModels;

internal static class ClientProfileWhdMatcher
{
    private static readonly HashSet<string> LegalSuffixes =
    [
        "CO", "COMPANY", "CORP", "CORPORATION", "INC", "INCORPORATED",
        "LLC", "LLP", "LTD", "LIMITED", "PC", "PLLC"
    ];
    private static readonly HashSet<string> JoinWords =
    [
        "AND"
    ];

    public static Client? FindConfidentMatch(
        string credentialClientName,
        IEnumerable<Client> clients)
    {
        var normalizedCredential = Normalize(credentialClientName);
        if (normalizedCredential.Length == 0)
            return null;

        var candidates = clients
            .Where(static client => client.IsActive && IsWhdCandidate(client))
            .Select(client => new
            {
                Client = client,
                Names = CandidateNames(client)
                    .Select(Normalize)
                    .Where(static value => value.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            })
            .Where(static candidate => candidate.Names.Length > 0)
            .ToList();

        var exact = candidates
            .Where(candidate => candidate.Names.Contains(
                normalizedCredential,
                StringComparer.Ordinal))
            .OrderByDescending(candidate => ContactCompleteness(candidate.Client))
            .ThenBy(candidate => candidate.Client.Id)
            .ToList();
        if (exact.Count == 1)
            return exact[0].Client;
        if (exact.Count > 1)
            return null;

        if (normalizedCredential.Length < 12)
            return null;

        var structural = candidates
            .Where(candidate => candidate.Names.Any(name =>
                name.Length >= 12
                && (name.StartsWith($"{normalizedCredential} ", StringComparison.Ordinal)
                    || normalizedCredential.StartsWith($"{name} ", StringComparison.Ordinal))))
            .OrderByDescending(candidate => ContactCompleteness(candidate.Client))
            .ThenBy(candidate => candidate.Client.Id)
            .ToList();

        return structural.Count == 1 ? structural[0].Client : null;
    }

    private static IEnumerable<string?> CandidateNames(Client client)
    {
        yield return client.Name;
        yield return client.WhdLocationName;
        yield return client.SageCustomerName;
    }

    private static bool IsWhdCandidate(Client client) =>
        client.Source.Equals("WHD", StringComparison.OrdinalIgnoreCase)
        || client.Source.Equals("Both", StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrWhiteSpace(client.WhdLocationName)
        || !string.IsNullOrWhiteSpace(client.WhdContactName);

    private static int ContactCompleteness(Client client) =>
        (string.IsNullOrWhiteSpace(client.WhdContactName) ? 0 : 1)
        + (string.IsNullOrWhiteSpace(client.WhdContactEmail) ? 0 : 1)
        + (string.IsNullOrWhiteSpace(client.WhdPhone) ? 0 : 1)
        + (string.IsNullOrWhiteSpace(client.WhdAddress) ? 0 : 1);

    internal static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToUpperInvariant())
        {
            if (character is '\'' or '\u2019')
                continue;
            if (character == '&')
            {
                builder.Append(' ');
                continue;
            }

            builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }

        var words = builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (words.Count > 1 && words[0] == "THE")
            words.RemoveAt(0);
        words.RemoveAll(JoinWords.Contains);
        while (words.Count > 0 && LegalSuffixes.Contains(words[^1]))
            words.RemoveAt(words.Count - 1);
        return string.Join(' ', words);
    }
}
