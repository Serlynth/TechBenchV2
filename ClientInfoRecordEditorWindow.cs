using System.Windows;
using System.Windows.Controls;
using TechBench.Models;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;
using Grid = System.Windows.Controls.Grid;
using PasswordBox = System.Windows.Controls.PasswordBox;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using StackPanel = System.Windows.Controls.StackPanel;
using TabControl = System.Windows.Controls.TabControl;
using TabItem = System.Windows.Controls.TabItem;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace TechBench;

public sealed record ClientInfoEditField(
    string Key,
    string Label,
    string Value = "",
    bool IsRequired = false,
    bool IsMultiline = false,
    bool IsSecret = false,
    IReadOnlyList<string>? Options = null,
    bool AllowCustomValue = false,
    string Tab = "",
    bool IsBoolean = false,
    string VisibleWhenKey = "",
    string VisibleWhenValue = "");

public sealed class ClientInfoRecordEditorWindow : Window
{
    private readonly Dictionary<string, Control> _editors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TabItem> _editorTabs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FrameworkElement> _fieldContainers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<ClientInfoEditField> _fields;

    public ClientInfoRecordEditorWindow(
        string title,
        IReadOnlyList<ClientInfoEditField> fields,
        string? description = null)
    {
        _fields = fields;
        Title = title;
        Icon = new System.Windows.Media.Imaging.BitmapImage(
            new Uri("pack://application:,,,/Assets/csri-techbench-icon.ico"));
        Width = 560;
        Height = Math.Min(760, 180 + fields.Count * 78);
        MinWidth = 480;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = (System.Windows.Media.Brush)FindResource(
            "WindowBackgroundBrush");

        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = GridLength.Auto
        });
        root.Children.Add(CreateEditorContent(description));
        HookConditionalVisibility();

        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 90,
            IsCancel = true,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var save = new Button
        {
            Content = "Save",
            MinWidth = 100,
            IsDefault = true
        };
        save.Click += Save_Click;
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);
        Content = root;
    }

    public IReadOnlyDictionary<string, string> Values { get; private set; } =
        new Dictionary<string, string>();

    private FrameworkElement CreateEditorContent(string? description)
    {
        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition
        {
            Height = GridLength.Auto
        });
        content.RowDefinitions.Add(new RowDefinition());

        if (!string.IsNullOrWhiteSpace(description))
        {
            var descriptionText = new TextBlock
            {
                Text = description,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
                Margin = new Thickness(0, 0, 0, 18)
            };
            content.Children.Add(descriptionText);
        }

        var tabGroups = _fields
            .GroupBy(field => string.IsNullOrWhiteSpace(field.Tab)
                ? "Details"
                : field.Tab.Trim())
            .ToArray();
        FrameworkElement editorContent;
        if (tabGroups.Length > 1)
        {
            var tabs = new TabControl
            {
                Background = (System.Windows.Media.Brush)FindResource(
                    "PanelBackgroundBrush"),
                Foreground = (System.Windows.Media.Brush)FindResource(
                    "PrimaryTextBrush"),
                BorderBrush = (System.Windows.Media.Brush)FindResource(
                    "BorderBrush"),
                BorderThickness = new Thickness(1)
            };
            foreach (var group in tabGroups)
            {
                var tab = new TabItem
                {
                    Header = group.Key,
                    Content = CreateFieldsScroll(group),
                    Background = (System.Windows.Media.Brush)FindResource(
                        "ControlAltBackgroundBrush"),
                    Foreground = (System.Windows.Media.Brush)FindResource(
                        "PrimaryTextBrush"),
                    Padding = new Thickness(14, 8, 14, 8)
                };
                tabs.Items.Add(tab);
                foreach (var field in group)
                {
                    _editorTabs[field.Key] = tab;
                }
            }

            editorContent = tabs;
        }
        else
        {
            editorContent = CreateFieldsScroll(_fields);
        }

        Grid.SetRow(editorContent, 1);
        content.Children.Add(editorContent);
        return content;
    }

    private ScrollViewer CreateFieldsScroll(
        IEnumerable<ClientInfoEditField> fields)
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(2, 12, 2, 0)
        };
        foreach (var field in fields)
        {
            var fieldPanel = new StackPanel();
            fieldPanel.Children.Add(new TextBlock
            {
                Text = field.IsRequired
                    ? $"{field.Label} *"
                    : field.Label,
                Margin = new Thickness(0, 0, 0, 5),
                FontWeight = FontWeights.SemiBold
            });
            var editor = CreateEditor(field);
            editor.Margin = new Thickness(0, 0, 0, 15);
            fieldPanel.Children.Add(editor);
            panel.Children.Add(fieldPanel);
            _editors[field.Key] = editor;
            _fieldContainers[field.Key] = fieldPanel;
        }

        return new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    private static Control CreateEditor(ClientInfoEditField field)
    {
        if (field.IsBoolean)
        {
            return new CheckBox
            {
                IsChecked = string.Equals(
                    field.Value,
                    "Yes",
                    StringComparison.OrdinalIgnoreCase),
                Content = "Use the AD username and password",
                MinHeight = 34,
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        if (field.Options is { Count: > 0 })
        {
            var combo = new ComboBox
            {
                IsEditable = field.AllowCustomValue,
                ItemsSource = field.Options,
                SelectedItem = field.Options.FirstOrDefault(value =>
                    string.Equals(
                        value,
                        field.Value,
                        StringComparison.OrdinalIgnoreCase))
                    ?? (field.AllowCustomValue ? null : field.Options[0]),
                Text = field.AllowCustomValue ? field.Value : string.Empty,
                MinHeight = 34
            };
            return combo;
        }

        if (field.IsSecret)
        {
            return new PasswordBox
            {
                Password = field.Value,
                MinHeight = 34,
                Padding = new Thickness(8, 5, 8, 5)
            };
        }

        return new TextBox
        {
            Text = field.Value,
            AcceptsReturn = field.IsMultiline,
            TextWrapping = field.IsMultiline
                ? TextWrapping.Wrap
                : TextWrapping.NoWrap,
            MinHeight = field.IsMultiline ? 90 : 34,
            VerticalScrollBarVisibility = field.IsMultiline
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Disabled,
            Padding = new Thickness(8, 5, 8, 5)
        };
    }

    private void HookConditionalVisibility()
    {
        foreach (var editor in _editors.Values)
        {
            switch (editor)
            {
                case ComboBox comboBox:
                    comboBox.SelectionChanged += (_, _) =>
                        RefreshConditionalVisibility();
                    comboBox.AddHandler(
                        TextBox.TextChangedEvent,
                        new TextChangedEventHandler((_, _) =>
                            RefreshConditionalVisibility()));
                    break;
                case TextBox textBox:
                    textBox.TextChanged += (_, _) =>
                        RefreshConditionalVisibility();
                    break;
                case PasswordBox passwordBox:
                    passwordBox.PasswordChanged += (_, _) =>
                        RefreshConditionalVisibility();
                    break;
                case CheckBox checkBox:
                    checkBox.Checked += (_, _) =>
                        RefreshConditionalVisibility();
                    checkBox.Unchecked += (_, _) =>
                        RefreshConditionalVisibility();
                    break;
            }
        }

        RefreshConditionalVisibility();
    }

    private void RefreshConditionalVisibility()
    {
        foreach (var field in _fields)
        {
            if (!_fieldContainers.TryGetValue(field.Key, out var container))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(field.VisibleWhenKey))
            {
                container.Visibility = Visibility.Visible;
                continue;
            }

            var isVisible = _editors.TryGetValue(
                    field.VisibleWhenKey,
                    out var controllingEditor)
                && string.Equals(
                    ReadValue(controllingEditor),
                    field.VisibleWhenValue,
                    StringComparison.OrdinalIgnoreCase);
            container.Visibility = isVisible
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var values = _fields.ToDictionary(
            field => field.Key,
            field => ReadValue(_editors[field.Key]),
            StringComparer.OrdinalIgnoreCase);
        var missing = _fields.FirstOrDefault(field =>
            field.IsRequired
            && string.IsNullOrWhiteSpace(values[field.Key]));
        if (missing is not null)
        {
            AppDialogWindow.Info(
                "Required field",
                $"{missing.Label} is required.");
            if (_editorTabs.TryGetValue(missing.Key, out var tab))
            {
                tab.IsSelected = true;
            }

            _editors[missing.Key].Focus();
            return;
        }

        Values = values;
        DialogResult = true;
    }

    private static string ReadValue(Control control) =>
        control switch
        {
            TextBox textBox => textBox.Text,
            PasswordBox passwordBox => passwordBox.Password,
            ComboBox comboBox => comboBox.IsEditable
                ? comboBox.Text
                : comboBox.SelectedItem?.ToString() ?? "",
            CheckBox checkBox => checkBox.IsChecked == true ? "Yes" : "No",
            _ => ""
        };
}

public sealed class ClientInfoSecretRevealWindow : Window
{
    private readonly PasswordBox _maskedValue;
    private readonly TextBox _visibleValue;

    public ClientInfoSecretRevealWindow(RevealedClientInfoSecret secret)
    {
        Title = $"{secret.CredentialName} · {secret.SecretLabel}";
        Width = 540;
        Height = 250;
        MinWidth = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = (System.Windows.Media.Brush)FindResource(
            "WindowBackgroundBrush");

        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = secret.SecretLabel,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        content.Children.Add(new TextBlock
        {
            Text = "Secret values are hidden by default. Reveal access is audited.",
            Margin = new Thickness(0, 0, 0, 12)
        });
        _maskedValue = new PasswordBox
        {
            Password = secret.SecretValue,
            MinHeight = 38,
            Padding = new Thickness(8, 6, 8, 6)
        };
        _visibleValue = new TextBox
        {
            Text = secret.SecretValue,
            IsReadOnly = true,
            MinHeight = 38,
            Padding = new Thickness(8, 6, 8, 6),
            Visibility = Visibility.Collapsed
        };
        content.Children.Add(_maskedValue);
        content.Children.Add(_visibleValue);
        root.Children.Add(content);

        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var reveal = new Button
        {
            Content = "Show",
            MinWidth = 88,
            Margin = new Thickness(0, 0, 8, 0)
        };
        reveal.Click += (_, _) =>
        {
            var showing = _visibleValue.Visibility == Visibility.Visible;
            _visibleValue.Visibility = showing
                ? Visibility.Collapsed
                : Visibility.Visible;
            _maskedValue.Visibility = showing
                ? Visibility.Visible
                : Visibility.Collapsed;
            reveal.Content = showing ? "Show" : "Hide";
        };
        var close = new Button
        {
            Content = "Close",
            MinWidth = 88,
            IsCancel = true
        };
        buttons.Children.Add(reveal);
        buttons.Children.Add(close);
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);
        Content = root;
        Closed += (_, _) =>
        {
            _maskedValue.Clear();
            _visibleValue.Clear();
        };
    }
}
