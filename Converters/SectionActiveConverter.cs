using System.Globalization;
using System.Windows.Data;

namespace TechBench.Converters;

public sealed class SectionActiveConverter : IValueConverter
{
    public object? Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        return string.Equals(
                value?.ToString(),
                parameter?.ToString(),
                StringComparison.Ordinal)
            ? "Active"
            : null;
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
