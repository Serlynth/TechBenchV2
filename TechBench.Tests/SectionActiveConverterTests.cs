using TechBench.Converters;

namespace TechBench.Tests;

public sealed class SectionActiveConverterTests
{
    [Fact]
    public void ReturnsActiveOnlyForTheRequestedSection()
    {
        var converter = new SectionActiveConverter();

        Assert.Equal(
            "Active",
            converter.Convert(
                "Equipment Board",
                typeof(object),
                "Equipment Board",
                System.Globalization.CultureInfo.InvariantCulture));
        Assert.Null(
            converter.Convert(
                "Inventory",
                typeof(object),
                "Equipment Board",
                System.Globalization.CultureInfo.InvariantCulture));
    }
}
