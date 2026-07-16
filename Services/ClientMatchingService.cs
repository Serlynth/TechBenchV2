using System.Text;
using TechBench.Models;

namespace TechBench.Services;

public sealed record ClientMatchSuggestion(
    Client Candidate,
    double Score,
    string Description);

public static class ClientMatchingService
{
    private static readonly IReadOnlyDictionary<string, string> WordAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ASSN"] = "ASSOCIATION",
            ["CTR"] = "CENTER",
            ["SCH"] = "SCHOOL",
            ["SVCS"] = "SERVICES"
        };

    private static readonly HashSet<string> LegalSuffixes =
    [
        "CO",
        "COMPANY",
        "CORP",
        "CORPORATION",
        "INC",
        "INCORPORATED",
        "LLC",
        "LLP",
        "LTD",
        "LIMITED",
        "PC",
        "PLLC"
    ];

    public static ClientMatchSuggestion? FindBestSuggestion(
        Client whdClient,
        IEnumerable<Client> sageCandidates)
    {
        var scored = sageCandidates
            .Where(candidate => candidate.Id != whdClient.Id
                && !string.IsNullOrWhiteSpace(candidate.SageCustomerId))
            .Select(candidate => new
            {
                Candidate = candidate,
                Score = ScoreNames(ResolveWhdName(whdClient), ResolveSageName(candidate))
            })
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();

        if (scored.Count == 0 || scored[0].Score < 0.68)
        {
            return null;
        }

        var top = scored[0];
        var isAmbiguous = scored.Count > 1
            && top.Score < 0.98
            && top.Score - scored[1].Score < 0.05;
        var description = isAmbiguous
            ? $"Possible match ({top.Score:P0}); review carefully because another Sage name is similar."
            : top.Score >= 0.98
                ? "Exact normalized company-name match."
                : top.Score >= 0.86
                    ? $"Strong company-name match ({top.Score:P0})."
                    : $"Possible company-name match ({top.Score:P0}); review before linking.";

        return new ClientMatchSuggestion(top.Candidate, top.Score, description);
    }

    public static double ScoreNames(string? left, string? right)
    {
        var leftNormalized = NormalizeCompanyName(left);
        var rightNormalized = NormalizeCompanyName(right);
        if (leftNormalized.Length == 0 || rightNormalized.Length == 0)
        {
            return 0;
        }

        if (leftNormalized.Equals(rightNormalized, StringComparison.Ordinal))
        {
            return 1;
        }

        var leftTokens = leftNormalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rightTokens = rightNormalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var leftSet = leftTokens.ToHashSet(StringComparer.Ordinal);
        var rightSet = rightTokens.ToHashSet(StringComparer.Ordinal);
        var intersection = leftSet.Intersect(rightSet, StringComparer.Ordinal).Count();
        var union = leftSet.Union(rightSet, StringComparer.Ordinal).Count();
        var tokenJaccard = union == 0 ? 0 : (double)intersection / union;
        var tokenContainment = Math.Min(leftSet.Count, rightSet.Count) == 0
            ? 0
            : (double)intersection / Math.Min(leftSet.Count, rightSet.Count);
        var shorterTokens = leftTokens.Length <= rightTokens.Length ? leftTokens : rightTokens;
        var longerTokens = leftTokens.Length <= rightTokens.Length ? rightTokens : leftTokens;
        var fuzzyTokenCoverage = shorterTokens.Length == 0
            ? 0
            : shorterTokens.Average(token =>
                longerTokens.Max(candidate =>
                    1d - ((double)LevenshteinDistance(token, candidate)
                        / Math.Max(token.Length, candidate.Length))));
        var lengthRatio = (double)Math.Min(leftNormalized.Length, rightNormalized.Length)
            / Math.Max(leftNormalized.Length, rightNormalized.Length);
        var editSimilarity = 1d
            - ((double)LevenshteinDistance(leftNormalized, rightNormalized)
                / Math.Max(leftNormalized.Length, rightNormalized.Length));

        var tokenScore = (fuzzyTokenCoverage * 0.58)
            + (tokenContainment * 0.17)
            + (tokenJaccard * 0.10)
            + (lengthRatio * 0.15);
        return Math.Clamp(Math.Max(editSimilarity, tokenScore), 0, 1);
    }

    public static string NormalizeCompanyName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToUpperInvariant())
        {
            if (character is '\'' or '\u2019')
            {
                continue;
            }

            if (character == '&')
            {
                builder.Append(" AND ");
                continue;
            }

            builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }

        var words = builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => WordAliases.GetValueOrDefault(word, word))
            .ToList();

        if (words.Count > 1 && words[0] == "THE")
        {
            words.RemoveAt(0);
        }

        while (words.Count > 0 && LegalSuffixes.Contains(words[^1]))
        {
            words.RemoveAt(words.Count - 1);
        }

        return string.Join(' ', words);
    }

    private static string ResolveWhdName(Client client) =>
        !string.IsNullOrWhiteSpace(client.WhdLocationName)
            ? client.WhdLocationName
            : client.Name;

    private static string ResolveSageName(Client client) =>
        !string.IsNullOrWhiteSpace(client.SageCustomerName)
            ? client.SageCustomerName
            : client.Name;

    private static int LevenshteinDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var substitutionCost = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
