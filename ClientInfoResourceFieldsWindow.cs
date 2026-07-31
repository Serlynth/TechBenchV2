using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TechBench.Data;
using TechBench.Models;
using TechBench.Services;
using Button = System.Windows.Controls.Button;
using DataGrid = System.Windows.Controls.DataGrid;
using Grid = System.Windows.Controls.Grid;
using Orientation = System.Windows.Controls.Orientation;
using TextBlock = System.Windows.Controls.TextBlock;

namespace TechBench;

public sealed class ClientInfoResourceFieldsWindow : Window
{
    private static readonly string[] ValueTypes =
        ["Text", "Number", "Boolean", "Date", "Url", "IpAddress"];
    private readonly ClientInfoResource _resource;
    private readonly ITechBenchRepository _repository;
    private readonly IUserDialogService _dialogs;
    private readonly ObservableCollection<ClientInfoResourceField> _fields;
    private readonly DataGrid _grid;
    private readonly Button _editButton;
    private readonly Button _deleteButton;

    public ClientInfoResourceFieldsWindow(
        ClientInfoResource resource,
        ITechBenchRepository repository,
        IUserDialogService dialogs)
    {
        _resource = resource;
        _repository = repository;
        _dialogs = dialogs;
        _fields = new ObservableCollection<ClientInfoResourceField>(
            resource.Fields
                .Where(field => !ClientInfoResourceFieldDefinitions.IsStandardField(
                    resource.Category,
                    field.FieldKey))
                .OrderBy(field => field.SortOrder)
                .ThenBy(field => field.FieldLabel));

        Title = $"{resource.Name} - Custom Fields";
        Icon = new System.Windows.Media.Imaging.BitmapImage(
            new Uri("pack://application:,,,/Assets/csri-techbench-icon.ico"));
        Width = 900;
        Height = 560;
        MinWidth = 720;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = (System.Windows.Media.Brush)FindResource(
            "WindowBackgroundBrush");

        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock
        {
            Text = "Custom fields",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold
        });
        header.Children.Add(new TextBlock
        {
            Text = "Add client-specific details that do not belong in the standard category fields. Each field becomes a visible column in this category.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });
        root.Children.Add(header);

        _grid = new DataGrid
        {
            ItemsSource = _fields,
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            SelectionMode = DataGridSelectionMode.Single
        };
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Field",
            Binding = new System.Windows.Data.Binding("FieldLabel"),
            Width = new DataGridLength(1.1, DataGridLengthUnitType.Star)
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Value",
            Binding = new System.Windows.Data.Binding("ValueText"),
            Width = new DataGridLength(1.8, DataGridLengthUnitType.Star)
        });
        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Type",
            Binding = new System.Windows.Data.Binding("ValueType"),
            Width = new DataGridLength(0.7, DataGridLengthUnitType.Star)
        });
        _grid.SelectionChanged += (_, _) => RefreshButtons();
        _grid.MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left
                && _grid.SelectedItem is ClientInfoResourceField field)
            {
                EditField(field);
                e.Handled = true;
            }
        };
        Grid.SetRow(_grid, 1);
        root.Children.Add(_grid);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var addButton = new Button
        {
            Content = "Add Field",
            MinWidth = 96,
            Margin = new Thickness(0, 0, 8, 0)
        };
        addButton.Click += (_, _) => EditField(null);
        _editButton = new Button
        {
            Content = "Edit",
            MinWidth = 84,
            Margin = new Thickness(0, 0, 8, 0)
        };
        _editButton.Click += (_, _) =>
            EditField(_grid.SelectedItem as ClientInfoResourceField);
        _deleteButton = new Button
        {
            Content = "Delete",
            MinWidth = 84,
            Margin = new Thickness(0, 0, 18, 0)
        };
        _deleteButton.Click += (_, _) => DeleteSelectedField();
        var closeButton = new Button
        {
            Content = "Close",
            MinWidth = 90,
            IsCancel = true
        };
        buttons.Children.Add(addButton);
        buttons.Children.Add(_editButton);
        buttons.Children.Add(_deleteButton);
        buttons.Children.Add(closeButton);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        Content = root;
        RefreshButtons();
    }

    private void EditField(ClientInfoResourceField? current)
    {
        var editor = new ClientInfoRecordEditorWindow(
            current is null ? "Add custom field" : "Edit custom field",
            [
                new("label", "Column name", current?.FieldLabel ?? "", true),
                new("value", "Value", current?.ValueText ?? "", IsMultiline: true),
                new(
                    "type",
                    "Value type",
                    current?.ValueType ?? "Text",
                    Options: ValueTypes)
            ])
        {
            Owner = this
        };
        if (editor.ShowDialog() != true)
        {
            return;
        }

        var label = editor.Values["label"].Trim();
        var fieldKey = current?.FieldKey
            ?? ClientInfoResourceFieldDefinitions.CustomFieldKey(label);
        if (current is null && _fields.Any(field => string.Equals(
                field.FieldKey,
                fieldKey,
                StringComparison.OrdinalIgnoreCase)))
        {
            _dialogs.Error(
                "Duplicate custom field",
                $"A custom field named '{label}' already exists for this resource.");
            return;
        }

        try
        {
            var saved = _repository.SaveClientInfoResourceField(
                (current ?? new ClientInfoResourceField
                {
                    ResourceId = _resource.ResourceId,
                    FieldKey = fieldKey,
                    SortOrder = (_fields.LastOrDefault()?.SortOrder ?? 90) + 10
                }) with
                {
                    FieldLabel = label,
                    ValueText = editor.Values["value"],
                    ValueType = editor.Values["type"]
                });
            if (current is not null)
            {
                _fields.Remove(current);
            }

            _fields.Add(saved);
            SortFields();
            _grid.SelectedItem = saved;
        }
        catch (Exception exception)
        {
            _dialogs.Error("Custom field could not be saved", exception.Message);
        }
    }

    private void DeleteSelectedField()
    {
        if (_grid.SelectedItem is not ClientInfoResourceField field
            || !_dialogs.Confirm(
                "Delete custom field",
                $"Delete '{field.FieldLabel}' from {_resource.Name}?",
                "Delete",
                "Cancel"))
        {
            return;
        }

        try
        {
            _repository.DeleteClientInfoResourceField(field);
            _fields.Remove(field);
        }
        catch (Exception exception)
        {
            _dialogs.Error("Custom field could not be deleted", exception.Message);
        }
    }

    private void SortFields()
    {
        var ordered = _fields
            .OrderBy(field => field.SortOrder)
            .ThenBy(field => field.FieldLabel)
            .ToArray();
        _fields.Clear();
        foreach (var field in ordered)
        {
            _fields.Add(field);
        }
    }

    private void RefreshButtons()
    {
        var hasSelection = _grid.SelectedItem is ClientInfoResourceField;
        _editButton.IsEnabled = hasSelection;
        _deleteButton.IsEnabled = hasSelection;
    }
}
