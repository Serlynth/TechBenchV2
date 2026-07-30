using System.Globalization;
using System.Windows.Data;

namespace TechBench.Converters;

public sealed class SectionActiveConverter :
    IValueConverter,
    IMultiValueConverter
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

    public object? Convert(
        object[] values,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        return values.Length >= 2
               && string.Equals(
                   values[0]?.ToString(),
                   values[1]?.ToString(),
                   StringComparison.Ordinal)
            ? "Active"
            : null;
    }

    public object[] ConvertBack(
        object? value,
        Type[] targetTypes,
        object? parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
