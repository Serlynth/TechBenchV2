namespace TechBench.Formatting;

internal static class AnyDeskIdFormatter
{
    public static string FormatForDisplay(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        if (!value.All(static character =>
                char.IsDigit(character)
                || char.IsWhiteSpace(character)
                || character == '-'))
        {
            return value;
        }

        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length switch
        {
            9 => FormatNineDigits(digits),
            10 when digits[0] == '1' => $"1 {FormatNineDigits(digits[1..])}",
            _ => value
        };
    }

    private static string FormatNineDigits(string digits) =>
        $"{digits[..3]} {digits[3..6]} {digits[6..]}";
}
