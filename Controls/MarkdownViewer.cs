using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using TechBench.Services;

namespace TechBench.Controls;

public sealed class MarkdownViewer : FlowDocumentScrollViewer
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown),
        typeof(string),
        typeof(MarkdownViewer),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure, HandleMarkdownChanged));

    public static readonly DependencyProperty BaseFontSizeProperty = DependencyProperty.Register(
        nameof(BaseFontSize),
        typeof(double),
        typeof(MarkdownViewer),
        new FrameworkPropertyMetadata(13d, FrameworkPropertyMetadataOptions.AffectsMeasure, HandleMarkdownChanged));

    public MarkdownViewer()
    {
        IsToolBarVisible = false;
        IsSelectionEnabled = true;
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Background = System.Windows.Media.Brushes.Transparent;
        Document = MarkdownFlowDocumentRenderer.Render(string.Empty, BaseFontSize);
    }

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public double BaseFontSize
    {
        get => (double)GetValue(BaseFontSizeProperty);
        set => SetValue(BaseFontSizeProperty, value);
    }

    private static void HandleMarkdownChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is MarkdownViewer viewer)
        {
            viewer.Document = MarkdownFlowDocumentRenderer.Render(viewer.Markdown, viewer.BaseFontSize);
        }
    }
}
