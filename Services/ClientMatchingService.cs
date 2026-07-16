using System.Text;
using TechBench.Models;

namespace TechBench.Services;

public sealed record ClientMatchSuggestion(
    Client Candidate,
    double Score,
    string Description);

public sealed record ClientAutomaticMatch(
    Client WhdClient,
    Client SageClient,
    double Score);

public static class ClientMatchingService
{
    private const double AutomaticMatchThreshold = 0.86;
    private const double AutomaticMatchMargin = 0.08;

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

    public static IReadOnlyList<ClientAutomaticMatch> FindSafeAutomaticMatches(
        IEnumerable<Client> whdClients,
        IEnumerable<Client> sageClients)
    {
        var whdCandidates = whdClients
            .Where(IsWhdLocationCandidate)
            .OrderBy(static client => client.Id)
            .ToList();
        var sageCandidates = sageClients
            .Where(IsSageMatchCandidate)
            .OrderBy(static client => client.Id)
            .ToList();
        if (whdCandidates.Count == 0 || sageCandidates.Count == 0)
        {
            return Array.Empty<ClientAutomaticMatch>();
        }

        var scores = whdCandidates
            .SelectMany(whd => sageCandidates.Select(sage => new MatchScore(
                whd,
                sage,
                ScoreNames(ResolveWhdName(whd), ResolveSageName(sage)))))
            .ToList();
        var matches = new List<ClientAutomaticMatch>();

        foreach (var whd in whdCandidates)
        {
            var whdRanking = scores
                .Where(score => score.WhdClient.Id == whd.Id)
                .OrderByDescending(static score => score.Score)
                .ThenBy(static score => score.SageClient.Id)
                .ToList();
            if (!HasUniqueAutomaticLead(whdRanking.Select(static score => score.Score)))
            {
                continue;
            }

            var best = whdRanking[0];
            if (!IsStructurallySafeAutomaticMatch(
                    ResolveWhdName(whd),
                    ResolveSageName(best.SageClient)))
            {
                continue;
            }

            var sageRanking = scores
                .Where(score => score.SageClient.Id == best.SageClient.Id)
                .OrderByDescending(static score => score.Score)
                .ThenBy(static score => score.WhdClient.Id)
                .ToList();
            if (sageRanking[0].WhdClient.Id != whd.Id
                || !HasUniqueAutomaticLead(sageRanking.Select(static score => score.Score)))
            {
                continue;
            }

            matches.Add(new ClientAutomaticMatch(whd, best.SageClient, best.Score));
        }

        return matches;
    }

    public static bool IsWhdLocationCandidate(Client client)
    {
        return client.IsActive
            && client.Source.Equals("WHD", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(client.SageCustomerId)
            && HasWhdLocationExternalId(client.ExternalId);
    }

    public static bool IsSageMatchCandidate(Client client)
    {
        if (!client.IsActive || string.IsNullOrWhiteSpace(client.SageCustomerId))
        {
            return false;
        }

        if (client.Source.Equals("Sage", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return client.Source.Equals("Both", StringComparison.OrdinalIgnoreCase)
            && !HasWhdLocationExternalId(client.ExternalId);
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

    private static bool HasUniqueAutomaticLead(IEnumerable<double> rankedScores)
    {
        var scores = rankedScores.Take(2).ToArray();
        return scores.Length > 0
            && scores[0] >= AutomaticMatchThreshold
            && (scores.Length == 1 || scores[0] - scores[1] >= AutomaticMatchMargin);
    }

    private static bool IsStructurallySafeAutomaticMatch(string? left, string? right)
    {
        var leftNormalized = NormalizeCompanyName(left);
        var rightNormalized = NormalizeCompanyName(right);
        if (leftNormalized.Length == 0 || rightNormalized.Length == 0)
        {
            return false;
        }

        if (leftNormalized.Equals(rightNormalized, StringComparison.Ordinal))
        {
            return true;
        }

        var editSimilarity = 1d
            - ((double)LevenshteinDistance(leftNormalized, rightNormalized)
                / Math.Max(leftNormalized.Length, rightNormalized.Length));
        if (editSimilarity >= 0.92)
        {
            return true;
        }

        var shorter = leftNormalized.Length <= rightNormalized.Length
            ? leftNormalized
            : rightNormalized;
        var longer = leftNormalized.Length <= rightNormalized.Length
            ? rightNormalized
            : leftNormalized;
        return shorter.Length >= 12
            && (longer.StartsWith($"{shorter} ", StringComparison.Ordinal)
                || longer.EndsWith($" {shorter}", StringComparison.Ordinal));
    }

    private static bool HasWhdLocationExternalId(string? externalIds)
    {
        return (externalIds ?? string.Empty)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(id => id.StartsWith("WHD-LOCATION-", StringComparison.OrdinalIgnoreCase));
    }

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

    private sealed record MatchScore(Client WhdClient, Client SageClient, double Score);
}
