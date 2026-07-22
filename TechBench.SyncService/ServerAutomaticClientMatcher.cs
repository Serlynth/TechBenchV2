using System.Text;

namespace TechBench.SyncService;

internal sealed record AutomaticClientMatchCandidate(
    int Id,
    string Name,
    string Source,
    string? ExternalId,
    bool IsActive,
    string? WhdLocationName,
    string? SageCustomerId,
    string? SageCustomerName,
    byte[]? RowVersion);

internal sealed record ServerAutomaticClientMatch(
    AutomaticClientMatchCandidate WhdClient,
    AutomaticClientMatchCandidate SageClient,
    double Score);

internal static class ServerAutomaticClientMatcher
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
        "CO", "COMPANY", "CORP", "CORPORATION", "INC", "INCORPORATED",
        "LLC", "LLP", "LTD", "LIMITED", "PC", "PLLC"
    ];

    public static IReadOnlyList<ServerAutomaticClientMatch> FindSafeAutomaticMatches(
        IEnumerable<AutomaticClientMatchCandidate> candidates)
    {
        var materialized = candidates.ToList();
        var whdCandidates = materialized
            .Where(IsWhdLocationCandidate)
            .OrderBy(static client => client.Id)
            .ToList();
        var sageCandidates = materialized
            .Where(IsSageMatchCandidate)
            .OrderBy(static client => client.Id)
            .ToList();
        if (whdCandidates.Count == 0 || sageCandidates.Count == 0)
        {
            return Array.Empty<ServerAutomaticClientMatch>();
        }

        var scores = whdCandidates
            .SelectMany(whd => sageCandidates.Select(sage => new MatchScore(
                whd,
                sage,
                ScoreNames(ResolveWhdName(whd), ResolveSageName(sage)))))
            .ToList();
        var matches = new List<ServerAutomaticClientMatch>();

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

            matches.Add(new ServerAutomaticClientMatch(whd, best.SageClient, best.Score));
        }

        return matches;
    }

    internal static string NormalizeCompanyName(string? value)
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

    private static bool IsWhdLocationCandidate(AutomaticClientMatchCandidate client) =>
        client.IsActive
        && client.Source.Equals("WHD", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrWhiteSpace(client.SageCustomerId)
        && (client.ExternalId ?? string.Empty)
            .StartsWith("WHD-LOCATION-", StringComparison.OrdinalIgnoreCase);

    private static bool IsSageMatchCandidate(AutomaticClientMatchCandidate client) =>
        client.IsActive
        && client.Source.Equals("Sage", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(client.SageCustomerId);

    private static double ScoreNames(string? left, string? right)
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
            : shorterTokens.Average(token => longerTokens.Max(candidate =>
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

    private static string ResolveWhdName(AutomaticClientMatchCandidate client) =>
        string.IsNullOrWhiteSpace(client.WhdLocationName) ? client.Name : client.WhdLocationName;

    private static string ResolveSageName(AutomaticClientMatchCandidate client) =>
        string.IsNullOrWhiteSpace(client.SageCustomerName) ? client.Name : client.SageCustomerName;

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

    private sealed record MatchScore(
        AutomaticClientMatchCandidate WhdClient,
        AutomaticClientMatchCandidate SageClient,
        double Score);
}
