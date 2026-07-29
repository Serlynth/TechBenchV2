using System.Globalization;
using System.Windows.Data;

namespace TechBench.Converters;

public sealed class StringOrPlaceholderConverter : IValueConverter
{
    public object Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        var text = value?.ToString();
        return string.IsNullOrWhiteSpace(text)
            ? parameter?.ToString() ?? "—"
            : text;
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
