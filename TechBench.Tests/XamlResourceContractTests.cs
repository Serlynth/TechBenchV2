using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TechBench.Tests;

public sealed partial class XamlResourceContractTests
{
    [Fact]
    public void EveryNamedStaticResourceIsDefinedByTheApplicationOrItsXamlFile()
    {
        var appResources = ReadResourceKeys(RepositoryFile("App.xaml"));
        var failures = new List<string>();

        foreach (var xamlPath in Directory.EnumerateFiles(
                     RepositoryRoot(),
                     "*.xaml",
                     SearchOption.AllDirectories)
                 .Where(path => !IsGeneratedPath(path)))
        {
            var availableResources = new HashSet<string>(appResources, StringComparer.Ordinal);
            availableResources.UnionWith(ReadResourceKeys(xamlPath));

            var document = XDocument.Load(xamlPath, LoadOptions.SetLineInfo);
            foreach (var attribute in document.Descendants().Attributes())
            {
                foreach (Match match in NamedStaticResourceRegex().Matches(attribute.Value))
                {
                    var key = match.Groups[1].Value;
                    if (!availableResources.Contains(key))
                    {
                        failures.Add($"{Path.GetRelativePath(RepositoryRoot(), xamlPath)}: missing StaticResource '{key}'");
                    }
                }
            }
        }

        Assert.Empty(failures);
    }

    private static HashSet<string> ReadResourceKeys(string path)
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(path)
            .Descendants()
            .Select(element => (string?)element.Attribute(xaml + "Key"))
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Select(static key => key!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool IsGeneratedPath(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static string RepositoryRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, ".."));

    private static string RepositoryFile(string relativePath) =>
        Path.Combine(RepositoryRoot(), relativePath);

    [GeneratedRegex(@"\{StaticResource\s+([A-Za-z_][A-Za-z0-9_.-]*)\}")]
    private static partial Regex NamedStaticResourceRegex();
}
