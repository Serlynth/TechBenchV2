namespace TechBench.Models;

public sealed record TimeOption(
    string Value,
    string DisplayText,
    int MinutesSinceMidnight);
