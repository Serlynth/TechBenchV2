using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using TechBench.ViewModels;

namespace TechBench;

public partial class MarkdownEditorWindow : Window
{
    private readonly DispatcherTimer _previewTimer;
    private readonly bool _isFixedReadOnly;
    private readonly MainWindowViewModel? _liveViewModel;
    private string _appliedMarkdown;
    private bool _allowClose;
    private bool _isInitializing = true;
    private bool _isFullScreen;
    private WindowState _windowedState;

    public MarkdownEditorWindow(MainWindowViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _previewTimer = CreatePreviewTimer();
        _liveViewModel = viewModel;
        _appliedMarkdown = viewModel.Editor.InternalNote ?? string.Empty;

        InitializeComponent();
        DataContext = viewModel;
        ConfigureLiveEditorBindings(viewModel);
        ApplyButton.Content = "Save Entry";
        ApplyButton.Command = viewModel.SaveEntryCommand;
        CancelButton.Content = "Close";
        _isInitializing = false;

        RefreshLiveContext();
        RefreshPreview();
        UpdateLayoutMode();
        UpdateDocumentStats();
        viewModel.PropertyChanged += LiveViewModel_PropertyChanged;
        viewModel.Editor.PropertyChanged += LiveEditor_PropertyChanged;
        Closed += LiveWindow_Closed;
        Loaded += Window_Loaded;
    }

    public MarkdownEditorWindow(string? markdown, bool isReadOnly)
    {
        _previewTimer = CreatePreviewTimer();
        _isFixedReadOnly = isReadOnly;
        _appliedMarkdown = markdown ?? string.Empty;
        MarkdownText = _appliedMarkdown;

        InitializeComponent();
        SourceTextBox.Text = _appliedMarkdown;
        SourceTextBox.IsReadOnly = _isFixedReadOnly;
        ReadOnlyBadge.Visibility = _isFixedReadOnly ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = _isFixedReadOnly ? Visibility.Collapsed : Visibility.Visible;
        ApplyButton.Content = _isFixedReadOnly ? "Close" : "Apply";
        ContextTextBlock.Text = _isFixedReadOnly ? "Markdown / Read only" : "Markdown";
        Title = _isFixedReadOnly ? "Personal Note - Markdown (Read Only)" : "Personal Note - Markdown";
        _isInitializing = false;

        RefreshPreview();
        UpdateLayoutMode();
        UpdateDocumentStats();
        UpdateApplyState();
        Loaded += Window_Loaded;
    }

    public string MarkdownText { get; private set; } = string.Empty;

    private bool IsLiveEditor => _liveViewModel is not null;
    private bool HasChanges => !string.Equals(SourceTextBox.Text, _appliedMarkdown, StringComparison.Ordinal);

    private DispatcherTimer CreatePreviewTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        timer.Tick += PreviewTimer_Tick;
        return timer;
    }

    private void ConfigureLiveEditorBindings(MainWindowViewModel viewModel)
    {
        BindingOperations.SetBinding(
            SourceTextBox,
            System.Windows.Controls.TextBox.TextProperty,
            new System.Windows.Data.Binding("Editor.InternalNote")
            {
                Source = viewModel,
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            });
        BindingOperations.SetBinding(
            SourceTextBox,
            System.Windows.Controls.TextBox.IsReadOnlyProperty,
            new System.Windows.Data.Binding(nameof(MainWindowViewModel.IsEditorReadOnly))
            {
                Source = viewModel,
                Mode = BindingMode.OneWay
            });
        BindingOperations.SetBinding(
            EditorStatusText,
            TextBlock.TextProperty,
            new System.Windows.Data.Binding(nameof(MainWindowViewModel.EditorSaveStatus))
            {
                Source = viewModel,
                Mode = BindingMode.OneWay
            });
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (SourceTextBox.IsReadOnly)
        {
            PreviewModeButton.IsChecked = true;
            PreviewViewer.Focus();
            return;
        }

        SourceTextBox.Focus();
        Keyboard.Focus(SourceTextBox);
    }

    private void LiveViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsEditorReadOnly)
            or nameof(MainWindowViewModel.EditorSubtitle)
            or nameof(MainWindowViewModel.EditorTitle))
        {
            RefreshLiveContext();
        }
    }

    private void LiveEditor_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_liveViewModel is null)
        {
            return;
        }

        if (e.PropertyName == nameof(WorkEntryEditorViewModel.IsDirty)
            && !_liveViewModel.Editor.IsDirty)
        {
            _appliedMarkdown = _liveViewModel.Editor.InternalNote ?? string.Empty;
        }
    }

    private void RefreshLiveContext()
    {
        if (_liveViewModel is null)
        {
            return;
        }

        var context = string.IsNullOrWhiteSpace(_liveViewModel.EditorSubtitle)
            ? _liveViewModel.EditorTitle
            : _liveViewModel.EditorSubtitle;
        ContextTextBlock.Text = $"{context} / Markdown";
        Title = $"Personal Note - {context}";
        ReadOnlyBadge.Visibility = _liveViewModel.IsEditorReadOnly
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void LiveWindow_Closed(object? sender, EventArgs e)
    {
        if (_liveViewModel is null)
        {
            return;
        }

        _liveViewModel.PropertyChanged -= LiveViewModel_PropertyChanged;
        _liveViewModel.Editor.PropertyChanged -= LiveEditor_PropertyChanged;
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
        if (ApplyButton is null || IsLiveEditor)
        {
            return;
        }

        ApplyButton.IsEnabled = _isFixedReadOnly || HasChanges;
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
        if (IsLiveEditor)
        {
            return;
        }

        if (_isFixedReadOnly)
        {
            _allowClose = true;
            Close();
            return;
        }

        MarkdownText = SourceTextBox.Text;
        _appliedMarkdown = MarkdownText;
        _allowClose = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _previewTimer.Stop();
        if (IsLiveEditor || _allowClose || _isFixedReadOnly || !HasChanges)
        {
            return;
        }

        var discard = AppDialogWindow.Confirm(
            "Discard Markdown changes?",
            "The Personal Note has changes that have not been applied.",
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

        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (_liveViewModel is not null)
            {
                if (_liveViewModel.SaveEntryCommand.CanExecute(null))
                {
                    _liveViewModel.SaveEntryCommand.Execute(null);
                }
            }
            else if (!_isFixedReadOnly)
            {
                ApplyButton_Click(ApplyButton, new RoutedEventArgs());
            }

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
