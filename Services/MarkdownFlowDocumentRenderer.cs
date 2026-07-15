using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using WpfList = System.Windows.Documents.List;
using WpfBlock = System.Windows.Documents.Block;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfTable = System.Windows.Documents.Table;
using WpfTableCell = System.Windows.Documents.TableCell;
using WpfTableRow = System.Windows.Documents.TableRow;

namespace TechBench.Services;

public static class MarkdownFlowDocumentRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    public static FlowDocument Render(string? markdown, double baseFontSize = 13)
    {
        var document = new FlowDocument
        {
            FontFamily = new WpfFontFamily("Segoe UI"),
            FontSize = baseFontSize,
            PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity,
            TextAlignment = TextAlignment.Left
        };
        SetResource(document, TextElement.ForegroundProperty, "PrimaryTextBrush");

        if (string.IsNullOrWhiteSpace(markdown))
        {
            var empty = new Paragraph(new Run("Nothing to preview."))
            {
                Margin = new Thickness(0)
            };
            SetResource(empty, TextElement.ForegroundProperty, "MutedTextBrush");
            document.Blocks.Add(empty);
            return document;
        }

        var parsed = Markdown.Parse(markdown, Pipeline);
        AddBlocks(parsed, document.Blocks, baseFontSize);
        return document;
    }

    private static void AddBlocks(ContainerBlock source, BlockCollection target, double baseFontSize)
    {
        foreach (var block in source)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    target.Add(CreateHeading(heading, baseFontSize));
                    break;
                case ParagraphBlock paragraph:
                    target.Add(CreateParagraph(paragraph));
                    break;
                case QuoteBlock quote:
                    target.Add(CreateQuote(quote, baseFontSize));
                    break;
                case ListBlock list:
                    target.Add(CreateList(list, baseFontSize));
                    break;
                case MdTable table:
                    target.Add(CreateTable(table, baseFontSize));
                    break;
                case FencedCodeBlock fencedCode:
                    target.Add(CreateCodeBlock(fencedCode.Lines.ToString(), fencedCode.Info?.ToString()));
                    break;
                case CodeBlock code:
                    target.Add(CreateCodeBlock(code.Lines.ToString(), null));
                    break;
                case ThematicBreakBlock:
                    target.Add(CreateThematicBreak());
                    break;
                case HtmlBlock html:
                    target.Add(CreateCodeBlock(html.Lines.ToString(), "HTML"));
                    break;
                case ContainerBlock container:
                {
                    var section = new Section { Margin = new Thickness(0) };
                    AddBlocks(container, section.Blocks, baseFontSize);
                    target.Add(section);
                    break;
                }
                case LeafBlock leaf when leaf.Inline is not null:
                {
                    var fallback = new Paragraph { Margin = new Thickness(0, 0, 0, 9) };
                    AppendInlines(fallback.Inlines, leaf.Inline);
                    target.Add(fallback);
                    break;
                }
            }
        }
    }

    private static Paragraph CreateHeading(HeadingBlock heading, double baseFontSize)
    {
        var size = heading.Level switch
        {
            1 => baseFontSize + 11,
            2 => baseFontSize + 7,
            3 => baseFontSize + 4,
            4 => baseFontSize + 2,
            _ => baseFontSize + 1
        };
        var paragraph = new Paragraph
        {
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, heading.Level == 1 ? 2 : 8, 0, 7),
            KeepWithNext = true
        };
        AppendInlines(paragraph.Inlines, heading.Inline);
        return paragraph;
    }

    private static Paragraph CreateParagraph(ParagraphBlock source)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 9),
            LineHeight = double.NaN
        };
        AppendInlines(paragraph.Inlines, source.Inline);
        return paragraph;
    }

    private static Section CreateQuote(QuoteBlock quote, double baseFontSize)
    {
        var section = new Section
        {
            Margin = new Thickness(0, 3, 0, 10),
            Padding = new Thickness(12, 7, 9, 2),
            BorderThickness = new Thickness(3, 0, 0, 0)
        };
        SetResource(section, WpfBlock.BorderBrushProperty, "AccentBrush");
        SetResource(section, TextElement.BackgroundProperty, "PanelAltBackgroundBrush");
        SetResource(section, TextElement.ForegroundProperty, "SecondaryTextBrush");
        AddBlocks(quote, section.Blocks, baseFontSize);
        return section;
    }

    private static WpfList CreateList(ListBlock list, double baseFontSize)
    {
        var rendered = new WpfList
        {
            MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(4, 0, 0, 9),
            Padding = new Thickness(22, 0, 0, 0)
        };
        if (list.IsOrdered
            && int.TryParse(list.OrderedStart, out var orderedStart)
            && orderedStart > 0)
        {
            rendered.StartIndex = orderedStart;
        }

        foreach (var item in list.OfType<ListItemBlock>())
        {
            var listItem = new System.Windows.Documents.ListItem
            {
                Margin = new Thickness(0, 1, 0, 2)
            };
            AddBlocks(item, listItem.Blocks, baseFontSize);
            rendered.ListItems.Add(listItem);
        }

        return rendered;
    }

    private static WpfTable CreateTable(MdTable table, double baseFontSize)
    {
        var rendered = new WpfTable
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 2, 0, 10)
        };
        var columnCount = table.OfType<MdTableRow>()
            .Select(static row => row.Count)
            .DefaultIfEmpty(1)
            .Max();
        for (var index = 0; index < columnCount; index++)
        {
            rendered.Columns.Add(new TableColumn());
        }

        var group = new TableRowGroup();
        rendered.RowGroups.Add(group);
        foreach (var sourceRow in table.OfType<MdTableRow>())
        {
            var row = new WpfTableRow();
            group.Rows.Add(row);
            foreach (var sourceCell in sourceRow.OfType<MdTableCell>())
            {
                var cell = new WpfTableCell
                {
                    Padding = new Thickness(8, 5, 8, 5),
                    BorderThickness = new Thickness(0, 0, 1, 1)
                };
                SetResource(cell, WpfBlock.BorderBrushProperty, "BorderBrush");
                if (sourceRow.IsHeader)
                {
                    cell.FontWeight = FontWeights.SemiBold;
                    SetResource(cell, TextElement.BackgroundProperty, "PanelAltBackgroundBrush");
                }

                AddBlocks(sourceCell, cell.Blocks, baseFontSize);
                row.Cells.Add(cell);
            }
        }

        return rendered;
    }

    private static Paragraph CreateCodeBlock(string? content, string? language)
    {
        var paragraph = new Paragraph
        {
            FontFamily = new WpfFontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 10),
            Padding = new Thickness(11, 9, 11, 9),
            BorderThickness = new Thickness(1),
            LineHeight = 18
        };
        SetResource(paragraph, TextElement.BackgroundProperty, "ControlAltBackgroundBrush");
        SetResource(paragraph, WpfBlock.BorderBrushProperty, "BorderBrush");
        if (!string.IsNullOrWhiteSpace(language))
        {
            var label = new Run(language.Trim()) { FontWeight = FontWeights.SemiBold };
            SetResource(label, TextElement.ForegroundProperty, "MutedTextBrush");
            paragraph.Inlines.Add(label);
            paragraph.Inlines.Add(new LineBreak());
        }

        paragraph.Inlines.Add(new Run((content ?? string.Empty).TrimEnd('\r', '\n')));
        return paragraph;
    }

    private static BlockUIContainer CreateThematicBreak()
    {
        var rule = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 9, 0, 12)
        };
        rule.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
        return new BlockUIContainer(rule) { Margin = new Thickness(0) };
    }

    private static void AppendInlines(InlineCollection target, ContainerInline? source)
    {
        for (var inline = source?.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run(literal.Content.ToString()));
                    break;
                case CodeInline code:
                {
                    var span = new Span(new Run(code.Content))
                    {
                        FontFamily = new WpfFontFamily("Cascadia Mono, Consolas"),
                        FontSize = 12
                    };
                    SetResource(span, TextElement.BackgroundProperty, "ControlAltBackgroundBrush");
                    target.Add(span);
                    break;
                }
                case EmphasisInline emphasis:
                {
                    var span = new Span();
                    if (emphasis.DelimiterChar is '*' or '_')
                    {
                        if (emphasis.DelimiterCount >= 2)
                        {
                            span.FontWeight = FontWeights.Bold;
                        }
                        else
                        {
                            span.FontStyle = FontStyles.Italic;
                        }
                    }
                    else if (emphasis.DelimiterChar == '~')
                    {
                        span.TextDecorations = TextDecorations.Strikethrough;
                    }

                    AppendInlines(span.Inlines, emphasis);
                    target.Add(span);
                    break;
                }
                case LinkInline link:
                    AppendLink(target, link);
                    break;
                case AutolinkInline autoLink:
                    AppendHyperlink(
                        target,
                        autoLink.Url,
                        autoLink.IsEmail ? $"mailto:{autoLink.Url}" : autoLink.Url);
                    break;
                case HtmlEntityInline entity:
                    target.Add(new Run(entity.Transcoded.ToString()));
                    break;
                case LineBreakInline:
                    target.Add(new LineBreak());
                    break;
                case TaskList task:
                    target.Add(new Run(task.Checked ? "[x] " : "[ ] "));
                    break;
                case HtmlInline html:
                {
                    var run = new Run(html.Tag) { FontFamily = new WpfFontFamily("Cascadia Mono, Consolas") };
                    SetResource(run, TextElement.ForegroundProperty, "MutedTextBrush");
                    target.Add(run);
                    break;
                }
                case ContainerInline container:
                {
                    var span = new Span();
                    AppendInlines(span.Inlines, container);
                    target.Add(span);
                    break;
                }
            }
        }
    }

    private static void AppendLink(InlineCollection target, LinkInline link)
    {
        var label = ReadInlineText(link);
        if (link.IsImage)
        {
            target.Add(new Run(string.IsNullOrWhiteSpace(label) ? "[Image]" : $"[Image: {label}]"));
            return;
        }

        AppendHyperlink(target, label, link.Url);
    }

    private static void AppendHyperlink(InlineCollection target, string? label, string? url)
    {
        var display = string.IsNullOrWhiteSpace(label) ? url ?? string.Empty : label;
        if (!TryCreateSafeUri(url, out var uri))
        {
            target.Add(new Run(display));
            return;
        }

        var hyperlink = new Hyperlink(new Run(display))
        {
            NavigateUri = uri,
            ToolTip = uri.AbsoluteUri
        };
        hyperlink.RequestNavigate += HandleRequestNavigate;
        SetResource(hyperlink, TextElement.ForegroundProperty, "AccentHoverBrush");
        target.Add(hyperlink);
    }

    private static string ReadInlineText(ContainerInline container)
    {
        var parts = new List<string>();
        CollectInlineText(container, parts);
        return string.Concat(parts);
    }

    private static void CollectInlineText(ContainerInline container, ICollection<string> parts)
    {
        for (var child = container.FirstChild; child is not null; child = child.NextSibling)
        {
            switch (child)
            {
                case LiteralInline literal:
                    parts.Add(literal.Content.ToString());
                    break;
                case CodeInline code:
                    parts.Add(code.Content);
                    break;
                case ContainerInline nested:
                    CollectInlineText(nested, parts);
                    break;
            }
        }
    }

    private static bool TryCreateSafeUri(string? value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            && parsed.Scheme is "http" or "https" or "mailto")
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }

    private static void HandleRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (TryCreateSafeUri(e.Uri?.AbsoluteUri, out var uri))
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }

        e.Handled = true;
    }

    private static void SetResource(FrameworkContentElement element, DependencyProperty property, string key) =>
        element.SetResourceReference(property, key);
}
