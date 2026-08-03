using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using TechBench.Models;
using TechBench.ViewModels;
using WpfBinding = System.Windows.Data.Binding;

namespace TechBench.Controls;

public sealed class ClientInfoResourceDataGrid : DataGrid
{
    private static readonly ClientInfoResourceFieldValueConverter
        FieldValueConverter = new();
    private INotifyCollectionChanged? _resources;

    public ClientInfoResourceDataGrid()
    {
        AutoGenerateColumns = false;
        IsReadOnly = true;
        CanUserAddRows = false;
        CanUserReorderColumns = true;
        CanUserResizeColumns = true;
        FrozenColumnCount = 2;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        DataContextChanged += (_, _) => AttachGroup();
        Loaded += (_, _) => AttachGroup();
        Unloaded += (_, _) => DetachResources();
    }

    private void AttachGroup()
    {
        DetachResources();
        if (DataContext is ClientInfoResourceGroup group)
        {
            _resources = group.Resources;
            _resources.CollectionChanged += Resources_CollectionChanged;
        }

        BuildColumns();
    }

    private void DetachResources()
    {
        if (_resources is not null)
        {
            _resources.CollectionChanged -= Resources_CollectionChanged;
            _resources = null;
        }
    }

    private void Resources_CollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e) =>
        BuildColumns();

    private void BuildColumns()
    {
        Columns.Clear();
        if (DataContext is not ClientInfoResourceGroup group)
        {
            return;
        }

        Columns.Add(TextColumn("Name", "Name", 1.2));
        Columns.Add(TextColumn("Type", "TypeLabel", 0.9));
        Columns.Add(TextColumn("Provider", "Provider", 0.9));
        Columns.Add(TextColumn(
            ClientInfoResourceFieldDefinitions.AddressLabelForCategory(
                group.CategoryName),
            "AddressOrUrl",
            1.2));

        // The lower table is the complete record view. Compact/ShowInGrid flags
        // belong to summaries and workbook layouts; they must not hide canonical
        // fields from technicians here.
        foreach (var field in ClientInfoResourceFieldDefinitions
                     .ForEditorCategory(group.CategoryName))
        {
            Columns.Add(FieldColumn(field.FieldLabel, field.FieldKey));
        }

        var customFields = group.Resources
            .SelectMany(resource => resource.Fields)
            .Where(field => !ClientInfoResourceFieldDefinitions.IsStandardField(
                group.CategoryName,
                field.FieldKey))
            .GroupBy(field => field.FieldKey, StringComparer.OrdinalIgnoreCase)
            .Select(fields => fields
                .OrderBy(field => field.SortOrder)
                .ThenBy(field => field.FieldLabel)
                .First())
            .OrderBy(field => field.SortOrder)
            .ThenBy(field => field.FieldLabel)
            .ToArray();
        foreach (var field in customFields)
        {
            Columns.Add(FieldColumn(field.FieldLabel, field.FieldKey));
        }

        Columns.Add(TextColumn("Location", "LocationName", 0.9));
        Columns.Add(TextColumn("Status", "Status", 0.7));
        Columns.Add(TextColumn("Notes", "Notes", 1.5));
        Columns.Add(TextColumn("Active", "IsActive", 0.6));
        Columns.Add(TextColumn("Review", "ReviewStatus", 0.8));
        Columns.Add(TextColumn("Last verified", "LastVerifiedAtUtc", 0.9));
        Columns.Add(TextColumn("Updated", "UpdatedAtUtc", 0.9));
    }

    private static DataGridTextColumn TextColumn(
        string header,
        string path,
        double width) =>
        new()
        {
            Header = header,
            Binding = new WpfBinding(path),
            Width = new DataGridLength(width, DataGridLengthUnitType.Star),
            MinWidth = 90
        };

    private static DataGridTextColumn FieldColumn(
        string header,
        string fieldKey) =>
        new()
        {
            Header = header,
            Binding = new WpfBinding
            {
                Converter = FieldValueConverter,
                ConverterParameter = fieldKey
            },
            Width = new DataGridLength(0.9, DataGridLengthUnitType.Star),
            MinWidth = 105
        };
}

internal sealed class ClientInfoResourceFieldValueConverter : IValueConverter
{
    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        value is ClientInfoResource resource && parameter is string fieldKey
            ? resource.GetFieldValue(fieldKey)
            : string.Empty;

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}
