using System.Windows;
using System.Windows.Controls;
using TechBench.Models;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;
using Grid = System.Windows.Controls.Grid;
using PasswordBox = System.Windows.Controls.PasswordBox;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using StackPanel = System.Windows.Controls.StackPanel;
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
    IReadOnlyList<string>? Options = null);

public sealed class ClientInfoRecordEditorWindow : Window
{
    private readonly Dictionary<string, Control> _editors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<ClientInfoEditField> _fields;

    public ClientInfoRecordEditorWindow(
        string title,
        IReadOnlyList<ClientInfoEditField> fields)
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
        var panel = new StackPanel();
        foreach (var field in fields)
        {
            panel.Children.Add(new TextBlock
            {
                Text = field.IsRequired
                    ? $"{field.Label} *"
                    : field.Label,
                Margin = new Thickness(0, 0, 0, 5),
                FontWeight = FontWeights.SemiBold
            });
            var editor = CreateEditor(field);
            editor.Margin = new Thickness(0, 0, 0, 15);
            panel.Children.Add(editor);
            _editors[field.Key] = editor;
        }

        var scroll = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        root.Children.Add(scroll);

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

    private static Control CreateEditor(ClientInfoEditField field)
    {
        if (field.Options is { Count: > 0 })
        {
            var combo = new ComboBox
            {
                IsEditable = false,
                ItemsSource = field.Options,
                SelectedItem = field.Options.FirstOrDefault(value =>
                    string.Equals(
                        value,
                        field.Value,
                        StringComparison.OrdinalIgnoreCase))
                    ?? field.Options[0],
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
            ComboBox comboBox => comboBox.SelectedItem?.ToString() ?? "",
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
