using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace TechBench;

public partial class MarkdownEditorWindow : Window
{
    private readonly DispatcherTimer _previewTimer;
    private readonly string _originalMarkdown;
    private readonly bool _isReadOnly;
    private bool _allowClose;
    private bool _isInitializing = true;
    private bool _isFullScreen;
    private WindowState _windowedState;

    public MarkdownEditorWindow(string? markdown, bool isReadOnly)
    {
        _previewTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _previewTimer.Tick += PreviewTimer_Tick;
        _originalMarkdown = markdown ?? string.Empty;
        _isReadOnly = isReadOnly;
        MarkdownText = _originalMarkdown;

        InitializeComponent();

        SourceTextBox.Text = _originalMarkdown;
        SourceTextBox.IsReadOnly = _isReadOnly;
        ReadOnlyBadge.Visibility = _isReadOnly ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = _isReadOnly ? Visibility.Collapsed : Visibility.Visible;
        ApplyButton.Content = _isReadOnly ? "Close" : "Apply to Entry";
        Title = _isReadOnly ? "Internal Note - Markdown (Read Only)" : "Internal Note - Markdown";
        _isInitializing = false;

        RefreshPreview();
        UpdateLayoutMode();
        UpdateDocumentStats();
        UpdateApplyState();
        Loaded += Window_Loaded;
    }

    public string MarkdownText { get; private set; }

    private bool HasChanges => !string.Equals(SourceTextBox.Text, _originalMarkdown, StringComparison.Ordinal);

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isReadOnly)
        {
            PreviewModeButton.IsChecked = true;
            PreviewViewer.Focus();
            return;
        }

        SourceTextBox.Focus();
        Keyboard.Focus(SourceTextBox);
    }

    private void SourceTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isInitializing)
        {
            _previewTimer.Stop();
            _previewTimer.Start();
        }

        UpdateDocumentStats();
        UpdateApplyState();
    }

    private void SourceTextBox_SelectionChanged(object sender, RoutedEventArgs e) => UpdateDocumentStats();

    private void PreviewTimer_Tick(object? sender, EventArgs e)
    {
        _previewTimer.Stop();
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        PreviewViewer.Markdown = SourceTextBox.Text;
    }

    private void UpdateDocumentStats()
    {
        if (SourceTextBox is null || DocumentStatsText is null)
        {
            return;
        }

        var text = SourceTextBox.Text ?? string.Empty;
        var words = text.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        var lineIndex = SourceTextBox.GetLineIndexFromCharacterIndex(SourceTextBox.CaretIndex);
        var lineStart = lineIndex >= 0 ? SourceTextBox.GetCharacterIndexFromLineIndex(lineIndex) : 0;
        var column = Math.Max(0, SourceTextBox.CaretIndex - lineStart) + 1;
        DocumentStatsText.Text = $"Ln {Math.Max(0, lineIndex) + 1}, Col {column}   |   {words} words   |   {text.Length} characters";
    }

    private void UpdateApplyState()
    {
        if (ApplyButton is not null)
        {
            ApplyButton.IsEnabled = _isReadOnly || HasChanges;
        }
    }

    private void ModeButton_Checked(object sender, RoutedEventArgs e)
    {
        if (EditorLayoutGrid is not null)
        {
            UpdateLayoutMode();
        }
    }

    private void UpdateLayoutMode()
    {
        if (SourceModeButton.IsChecked == true)
        {
            SourcePane.Visibility = Visibility.Visible;
            PaneSplitter.Visibility = Visibility.Collapsed;
            PreviewPane.Visibility = Visibility.Collapsed;
            SourceColumn.Width = new GridLength(1, GridUnitType.Star);
            SplitterColumn.Width = new GridLength(0);
            PreviewColumn.Width = new GridLength(0);
            return;
        }

        if (PreviewModeButton.IsChecked == true)
        {
            RefreshPreview();
            SourcePane.Visibility = Visibility.Collapsed;
            PaneSplitter.Visibility = Visibility.Collapsed;
            PreviewPane.Visibility = Visibility.Visible;
            SourceColumn.Width = new GridLength(0);
            SplitterColumn.Width = new GridLength(0);
            PreviewColumn.Width = new GridLength(1, GridUnitType.Star);
            return;
        }

        SourcePane.Visibility = Visibility.Visible;
        PaneSplitter.Visibility = Visibility.Visible;
        PreviewPane.Visibility = Visibility.Visible;
        SourceColumn.Width = new GridLength(1, GridUnitType.Star);
        SplitterColumn.Width = new GridLength(8);
        PreviewColumn.Width = new GridLength(1, GridUnitType.Star);
    }

    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isReadOnly)
        {
            _allowClose = true;
            Close();
            return;
        }

        MarkdownText = SourceTextBox.Text;
        _allowClose = true;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _previewTimer.Stop();
        if (_allowClose || _isReadOnly || !HasChanges)
        {
            return;
        }

        var discard = AppDialogWindow.Confirm(
            "Discard Markdown changes?",
            "The internal note has changes that have not been applied to the entry.",
            this,
            confirmText: "Discard",
            cancelText: "Keep Editing");
        if (!discard)
        {
            e.Cancel = true;
            return;
        }

        _allowClose = true;
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control && !_isReadOnly)
        {
            ApplyButton_Click(ApplyButton, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_isFullScreen)
            {
                ToggleFullScreen();
            }
            else
            {
                Close();
            }

            e.Handled = true;
        }
    }

    private void FullScreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        if (!_isFullScreen)
        {
            _windowedState = WindowState;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            FullScreenButton.Content = "Restore";
            _isFullScreen = true;
            return;
        }

        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        WindowState = _windowedState;
        FullScreenButton.Content = "Full screen";
        _isFullScreen = false;
    }
}
