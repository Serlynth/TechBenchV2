using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Documents;
using TechBench.Services;

namespace TechBench.Tests;

public sealed class MarkdownFlowDocumentRendererTests
{
    [Fact]
    public void RendersCommonMarkdownAndAdvancedWorkNoteStructures()
    {
        const string markdown = """
            # Service Summary

            **Result:** VPN restored with *no data loss* and ~~temporary workaround~~ removed.

            - Checked firewall policy
            - [x] Confirmed remote access

            > User verified the connection.

            ```powershell
            Get-Service -Name RasMan
            ```

            | Check | Result |
            | --- | --- |
            | VPN | Passed |
            """;

        var renderedText = RunInSta(() =>
        {
            var document = MarkdownFlowDocumentRenderer.Render(markdown);
            return new TextRange(document.ContentStart, document.ContentEnd).Text;
        });

        Assert.Contains("Service Summary", renderedText);
        Assert.Contains("VPN restored", renderedText);
        Assert.Contains("Confirmed remote access", renderedText);
        Assert.Contains("User verified the connection", renderedText);
        Assert.Contains("Get-Service -Name RasMan", renderedText);
        Assert.Contains("Passed", renderedText);
    }

    [Fact]
    public void OnlyCreatesClickableHyperlinksForSafeSchemes()
    {
        var links = RunInSta(() =>
        {
            var document = MarkdownFlowDocumentRenderer.Render(
                "[safe](https://example.com) <tech@example.com> [unsafe](javascript:alert)");
            return document.Blocks
                .OfType<Paragraph>()
                .SelectMany(static paragraph => paragraph.Inlines.OfType<Hyperlink>())
                .Select(static hyperlink => hyperlink.NavigateUri?.AbsoluteUri)
                .ToArray();
        });

        Assert.Equal(2, links.Length);
        Assert.Equal("https://example.com/", links[0]);
        Assert.Equal("mailto:tech@example.com", links[1]);
    }

    [Fact]
    public void DecodesMarkdownHtmlEntitiesWithoutDroppingText()
    {
        var renderedText = RunInSta(() =>
        {
            var document = MarkdownFlowDocumentRenderer.Render("R&D &amp; support");
            return new TextRange(document.ContentStart, document.ContentEnd).Text;
        });

        Assert.Contains("R&D & support", renderedText);
    }

    [Fact]
    public void DisplaysRawHtmlAsTextInsteadOfHostingBrowserContent()
    {
        var renderedText = RunInSta(() =>
        {
            var document = MarkdownFlowDocumentRenderer.Render("<script>alert('test')</script>");
            return new TextRange(document.ContentStart, document.ContentEnd).Text;
        });

        Assert.Contains("<script>alert('test')</script>", renderedText);
    }

    private static T RunInSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result!;
    }
}
