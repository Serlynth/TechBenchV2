namespace TechBench.Models;

public static class WorkEntryTags
{
    public static IReadOnlyList<string> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string Normalize(string? value)
    {
        return string.Join(", ", Parse(value));
    }

    public static string Add(string? existingTags, string? tagsToAdd)
    {
        var tags = Parse(existingTags).ToList();
        var seen = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
        foreach (var tag in Parse(tagsToAdd))
        {
            if (seen.Add(tag))
            {
                tags.Add(tag);
            }
        }

        return string.Join(", ", tags);
    }
}
