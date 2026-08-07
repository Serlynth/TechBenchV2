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
    byte[]? RowVersion,
    bool IsTechBenchLive = false);

internal sealed record ServerAutomaticClientMatch(
    AutomaticClientMatchCandidate WhdClient,
    AutomaticClientMatchCandidate SageClient,
    double Score);

internal sealed record ServerAutomaticClientSourceMatch(
    AutomaticClientMatchCandidate CanonicalClient,
    AutomaticClientMatchCandidate SourceClient,
    string SourceSystem,
    double Score,
    string? SourceFamilyKey = null,
    IReadOnlyList<AutomaticClientMatchCandidate>? AdditionalWhdFamilyMembers = null);

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

    public static IReadOnlyList<ServerAutomaticClientSourceMatch>
        FindSafeCanonicalSourceMatches(
            IEnumerable<AutomaticClientMatchCandidate> candidates)
    {
        var materialized = candidates.ToList();
        var canonicalCandidates = materialized
            .Where(static client => client.IsActive && client.IsTechBenchLive)
            .OrderBy(static client => client.Id)
            .ToList();
        if (canonicalCandidates.Count == 0)
        {
            return Array.Empty<ServerAutomaticClientSourceMatch>();
        }

        var matches = new List<ServerAutomaticClientSourceMatch>();
        var whdSources = materialized
            .Where(IsUnmatchedNonLiveWhdSource)
            .ToList();
        AddCanonicalSourceMatches(
            canonicalCandidates,
            whdSources,
            "WHD",
            matches);
        AddCanonicalWhdFamilyMatches(
            canonicalCandidates,
            whdSources,
            matches);
        AddCanonicalSourceMatches(
            canonicalCandidates,
            materialized.Where(IsUnmatchedNonLiveSageSource).ToList(),
            "Sage",
            matches);
        return matches;
    }

    public static IReadOnlyList<ServerAutomaticClientMatch> FindSafeAutomaticMatches(
        IEnumerable<AutomaticClientMatchCandidate> candidates)
    {
        var materialized = candidates.ToList();
        var liveCanonicals = materialized
            .Where(static client => client.IsActive && client.IsTechBenchLive)
            .ToList();
        var whdCandidates = materialized
            .Where(IsWhdLocationCandidate)
            .Where(client => !HasCredibleLiveCanonicalCandidate(
                client,
                "WHD",
                liveCanonicals))
            .OrderBy(static client => client.Id)
            .ToList();
        var sageCandidates = materialized
            .Where(IsSageMatchCandidate)
            .Where(client => !HasCredibleLiveCanonicalCandidate(
                client,
                "Sage",
                liveCanonicals))
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

        AddNumberedLocationFamilyMatches(whdCandidates, sageCandidates, matches);

        return matches;
    }

    public static IReadOnlyList<ServerAutomaticClientMatch>
        FindSafeCanonicalWhdFamilyMembers(
            IEnumerable<AutomaticClientMatchCandidate> candidates)
    {
        var materialized = candidates.ToList();
        var canonicalFamilies = materialized
            .Where(static client =>
                client.IsActive
                && client.IsTechBenchLive
                && HasSource(client, "WHD")
                && HasSource(client, "Sage"))
            .Select(client => new
            {
                Client = client,
                Stem = GetNumberedLocationStem(ResolveWhdName(client))
            })
            .Where(static candidate => candidate.Stem is not null)
            .GroupBy(static candidate => candidate.Stem!, StringComparer.Ordinal)
            .Where(static group => group.Count() == 1)
            .ToDictionary(
                static group => group.Key,
                static group => group.Single().Client,
                StringComparer.Ordinal);
        if (canonicalFamilies.Count == 0)
        {
            return Array.Empty<ServerAutomaticClientMatch>();
        }

        return materialized
            .Where(IsUnmatchedNonLiveWhdSource)
            .Select(source => new
            {
                Source = source,
                Stem = GetNumberedLocationStem(ResolveWhdName(source))
            })
            .Where(candidate =>
                candidate.Stem is not null
                && canonicalFamilies.ContainsKey(candidate.Stem))
            .OrderBy(static candidate => candidate.Stem, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.Source.Id)
            .Select(candidate => new ServerAutomaticClientMatch(
                candidate.Source,
                canonicalFamilies[candidate.Stem!],
                1d))
            .ToList();
    }

    private static void AddNumberedLocationFamilyMatches(
        IReadOnlyList<AutomaticClientMatchCandidate> whdCandidates,
        IReadOnlyList<AutomaticClientMatchCandidate> sageCandidates,
        ICollection<ServerAutomaticClientMatch> matches)
    {
        var assignedWhdIds = matches
            .Select(static match => match.WhdClient.Id)
            .ToHashSet();
        var assignedSageIds = matches
            .Select(static match => match.SageClient.Id)
            .ToHashSet();
        var families = whdCandidates
            .Where(client => !assignedWhdIds.Contains(client.Id))
            .Select(client => new
            {
                Client = client,
                Stem = GetNumberedLocationStem(ResolveWhdName(client))
            })
            .Where(static candidate => candidate.Stem is not null)
            .GroupBy(static candidate => candidate.Stem!, StringComparer.Ordinal)
            .Where(static group => group.Count() >= 2)
            .Select(group => new NumberedLocationFamily(
                group.Key,
                group.Select(static candidate => candidate.Client)
                    .OrderBy(static client => client.Id)
                    .ToList()))
            .OrderBy(static family => family.Stem, StringComparer.Ordinal)
            .ToList();
        var availableSageCandidates = sageCandidates
            .Where(client => !assignedSageIds.Contains(client.Id))
            .ToList();
        var familyScores = families
            .SelectMany(family => availableSageCandidates.Select(sage => new FamilyMatchScore(
                family,
                sage,
                ScoreNames(family.Stem, ResolveSageName(sage)))))
            .ToList();

        foreach (var family in families)
        {
            var familyRanking = familyScores
                .Where(score => ReferenceEquals(score.Family, family))
                .OrderByDescending(static score => score.Score)
                .ThenBy(static score => score.SageClient.Id)
                .ToList();
            if (!HasUniqueAutomaticLead(familyRanking.Select(static score => score.Score)))
            {
                continue;
            }

            var best = familyRanking[0];
            if (!IsStructurallySafeAutomaticMatch(
                    family.Stem,
                    ResolveSageName(best.SageClient)))
            {
                continue;
            }

            var sageRanking = familyScores
                .Where(score => score.SageClient.Id == best.SageClient.Id)
                .OrderByDescending(static score => score.Score)
                .ThenBy(static score => score.Family.Stem, StringComparer.Ordinal)
                .ToList();
            if (!ReferenceEquals(sageRanking[0].Family, family)
                || !HasUniqueAutomaticLead(sageRanking.Select(static score => score.Score)))
            {
                continue;
            }

            foreach (var whdClient in family.Clients)
            {
                matches.Add(new ServerAutomaticClientMatch(
                    whdClient,
                    best.SageClient,
                    best.Score));
            }
        }
    }

    private static void AddCanonicalSourceMatches(
        IReadOnlyList<AutomaticClientMatchCandidate> canonicalCandidates,
        IReadOnlyList<AutomaticClientMatchCandidate> sourceCandidates,
        string sourceSystem,
        ICollection<ServerAutomaticClientSourceMatch> matches)
    {
        if (sourceCandidates.Count == 0)
        {
            return;
        }

        var availableCanonicals = canonicalCandidates
            .Where(client => !HasSource(client, sourceSystem))
            .ToList();
        if (availableCanonicals.Count == 0)
        {
            return;
        }

        var scores = availableCanonicals
            .SelectMany(canonical => sourceCandidates.Select(source =>
                ScoreCanonicalSourceMatch(canonical, source, sourceSystem)))
            .ToList();

        foreach (var source in sourceCandidates.OrderBy(static client => client.Id))
        {
            var sourceRanking = scores
                .Where(score => score.SourceClient.Id == source.Id)
                .OrderByDescending(static score => score.Score)
                .ThenBy(static score => score.CanonicalClient.Id)
                .ToList();
            if (!HasUniqueAutomaticLead(sourceRanking.Select(static score => score.Score)))
            {
                continue;
            }

            var best = sourceRanking[0];
            if (!IsStructurallySafeAutomaticMatch(
                    best.CanonicalMatchName,
                    best.SourceMatchName))
            {
                continue;
            }

            var canonicalRanking = scores
                .Where(score => score.CanonicalClient.Id == best.CanonicalClient.Id)
                .OrderByDescending(static score => score.Score)
                .ThenBy(static score => score.SourceClient.Id)
                .ToList();
            if (canonicalRanking[0].SourceClient.Id != source.Id
                || !HasUniqueAutomaticLead(
                    canonicalRanking.Select(static score => score.Score)))
            {
                continue;
            }

            matches.Add(new ServerAutomaticClientSourceMatch(
                best.CanonicalClient,
                source,
                sourceSystem,
                best.Score));
        }
    }

    private static void AddCanonicalWhdFamilyMatches(
        IReadOnlyList<AutomaticClientMatchCandidate> canonicalCandidates,
        IReadOnlyList<AutomaticClientMatchCandidate> whdCandidates,
        ICollection<ServerAutomaticClientSourceMatch> matches)
    {
        var assignedWhdIds = matches
            .Where(static match => match.SourceSystem == "WHD")
            .Select(static match => match.SourceClient.Id)
            .ToHashSet();
        var assignedCanonicalIds = matches
            .Where(static match => match.SourceSystem == "WHD")
            .Select(static match => match.CanonicalClient.Id)
            .ToHashSet();
        var families = whdCandidates
            .Where(client => !assignedWhdIds.Contains(client.Id))
            .Select(client => new
            {
                Client = client,
                Stem = GetNumberedLocationStem(ResolveWhdName(client))
            })
            .Where(static candidate => candidate.Stem is not null)
            .GroupBy(static candidate => candidate.Stem!, StringComparer.Ordinal)
            .Where(static group => group.Count() >= 2)
            .Select(group => new NumberedLocationFamily(
                group.Key,
                group.Select(static candidate => candidate.Client)
                    .OrderBy(static client => client.Id)
                    .ToList()))
            .OrderBy(static family => family.Stem, StringComparer.Ordinal)
            .ToList();
        var availableCanonicals = canonicalCandidates
            .Where(client => !assignedCanonicalIds.Contains(client.Id)
                && !HasSource(client, "WHD")
                && HasSource(client, "Sage"))
            .ToList();
        if (families.Count == 0 || availableCanonicals.Count == 0)
        {
            return;
        }

        var scores = families
            .SelectMany(family => availableCanonicals.Select(canonical =>
            {
                var canonicalNameScore = FindBestCanonicalNameScore(
                    canonical,
                    family.Stem);
                return new CanonicalWhdFamilyMatchScore(
                    family,
                    canonical,
                    canonicalNameScore.Name,
                    canonicalNameScore.Score);
            }))
            .ToList();

        foreach (var family in families)
        {
            var familyRanking = scores
                .Where(score => ReferenceEquals(score.Family, family))
                .OrderByDescending(static score => score.Score)
                .ThenBy(static score => score.CanonicalClient.Id)
                .ToList();
            if (!HasUniqueAutomaticLead(
                    familyRanking.Select(static score => score.Score)))
            {
                continue;
            }

            var best = familyRanking[0];
            if (!IsStructurallySafeAutomaticMatch(
                    best.CanonicalMatchName,
                    family.Stem))
            {
                continue;
            }

            var canonicalRanking = scores
                .Where(score => score.CanonicalClient.Id == best.CanonicalClient.Id)
                .OrderByDescending(static score => score.Score)
                .ThenBy(static score => score.Family.Stem, StringComparer.Ordinal)
                .ToList();
            if (!ReferenceEquals(canonicalRanking[0].Family, family)
                || !HasUniqueAutomaticLead(
                    canonicalRanking.Select(static score => score.Score)))
            {
                continue;
            }

            matches.Add(new ServerAutomaticClientSourceMatch(
                best.CanonicalClient,
                family.Clients[0],
                "WHD",
                best.Score,
                family.Stem,
                family.Clients.Skip(1).ToList()));
        }
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
        && !client.IsTechBenchLive
        && client.Source.Equals("WHD", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrWhiteSpace(client.SageCustomerId)
        && (client.ExternalId ?? string.Empty)
            .StartsWith("WHD-LOCATION-", StringComparison.OrdinalIgnoreCase);

    private static bool IsSageMatchCandidate(AutomaticClientMatchCandidate client) =>
        client.IsActive
        && !client.IsTechBenchLive
        && client.Source.Equals("Sage", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(client.SageCustomerId);

    private static bool IsUnmatchedNonLiveWhdSource(
        AutomaticClientMatchCandidate client) =>
        !client.IsTechBenchLive && IsWhdLocationCandidate(client);

    private static bool IsUnmatchedNonLiveSageSource(
        AutomaticClientMatchCandidate client) =>
        !client.IsTechBenchLive && IsSageMatchCandidate(client);

    private static bool HasSource(
        AutomaticClientMatchCandidate client,
        string sourceSystem) =>
        sourceSystem.Equals("WHD", StringComparison.OrdinalIgnoreCase)
            ? HasWhdLocationExternalId(client.ExternalId)
            : sourceSystem.Equals("Sage", StringComparison.OrdinalIgnoreCase)
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

    private static string? GetNumberedLocationStem(string? value)
    {
        var normalized = NormalizeCompanyName(value);
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var removedNumber = false;
        while (words.Count > 0
               && words[^1].Length <= 6
               && words[^1].All(char.IsDigit))
        {
            words.RemoveAt(words.Count - 1);
            removedNumber = true;
        }

        var stem = string.Join(' ', words);
        return removedNumber && words.Count >= 2 && stem.Length >= 12
            ? stem
            : null;
    }

    private static string ResolveWhdName(AutomaticClientMatchCandidate client) =>
        string.IsNullOrWhiteSpace(client.WhdLocationName) ? client.Name : client.WhdLocationName;

    private static string ResolveSageName(AutomaticClientMatchCandidate client) =>
        string.IsNullOrWhiteSpace(client.SageCustomerName) ? client.Name : client.SageCustomerName;

    private static string ResolveSourceName(
        AutomaticClientMatchCandidate client,
        string sourceSystem) =>
        sourceSystem.Equals("WHD", StringComparison.OrdinalIgnoreCase)
            ? ResolveWhdName(client)
            : ResolveSageName(client);

    private static CanonicalSourceMatchScore ScoreCanonicalSourceMatch(
        AutomaticClientMatchCandidate canonical,
        AutomaticClientMatchCandidate source,
        string sourceSystem)
    {
        var sourceName = ResolveSourceName(source, sourceSystem);
        var bestCanonicalName = FindBestCanonicalNameScore(canonical, sourceName);
        return new CanonicalSourceMatchScore(
            canonical,
            source,
            bestCanonicalName.Name,
            sourceName,
            bestCanonicalName.Score);
    }

    private static bool HasCredibleLiveCanonicalCandidate(
        AutomaticClientMatchCandidate source,
        string sourceSystem,
        IEnumerable<AutomaticClientMatchCandidate> liveCanonicals)
    {
        return liveCanonicals
            .Where(canonical => !HasSource(canonical, sourceSystem))
            .Select(canonical => ScoreCanonicalSourceMatch(
                canonical,
                source,
                sourceSystem))
            .Any(score =>
                score.Score >= AutomaticMatchThreshold
                && IsStructurallySafeAutomaticMatch(
                    score.CanonicalMatchName,
                    score.SourceMatchName));
    }

    private static CanonicalNameScore FindBestCanonicalNameScore(
        AutomaticClientMatchCandidate canonical,
        string comparisonName)
    {
        var bestCanonicalName = new[]
            {
                canonical.Name,
                canonical.WhdLocationName,
                canonical.SageCustomerName
            }
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new
            {
                Name = name!,
                Score = ScoreNames(name, comparisonName)
            })
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (bestCanonicalName is null)
        {
            return new CanonicalNameScore(string.Empty, 0);
        }

        return new CanonicalNameScore(
            bestCanonicalName.Name,
            bestCanonicalName.Score);
    }

    private static bool HasWhdLocationExternalId(string? externalIds) =>
        (externalIds ?? string.Empty)
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(static id => id.StartsWith(
                "WHD-LOCATION-",
                StringComparison.OrdinalIgnoreCase));

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

    private sealed record NumberedLocationFamily(
        string Stem,
        IReadOnlyList<AutomaticClientMatchCandidate> Clients);

    private sealed record FamilyMatchScore(
        NumberedLocationFamily Family,
        AutomaticClientMatchCandidate SageClient,
        double Score);

    private sealed record CanonicalSourceMatchScore(
        AutomaticClientMatchCandidate CanonicalClient,
        AutomaticClientMatchCandidate SourceClient,
        string CanonicalMatchName,
        string SourceMatchName,
        double Score);

    private sealed record CanonicalWhdFamilyMatchScore(
        NumberedLocationFamily Family,
        AutomaticClientMatchCandidate CanonicalClient,
        string CanonicalMatchName,
        double Score);

    private sealed record CanonicalNameScore(string Name, double Score);
}
