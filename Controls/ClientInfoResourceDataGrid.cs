using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
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
    public static readonly DependencyProperty ShowReviewColumnProperty =
        DependencyProperty.Register(
            nameof(ShowReviewColumn),
            typeof(bool),
            typeof(ClientInfoResourceDataGrid),
            new PropertyMetadata(true, OnShowReviewColumnChanged));

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
        ColumnHeaderHeight = 54;
        RowHeight = 42;
        Sorting += ResourceGrid_Sorting;
        DataContextChanged += (_, _) => AttachGroup();
        Loaded += (_, _) => AttachGroup();
        Unloaded += (_, _) => DetachResources();
    }

    public bool ShowReviewColumn
    {
        get => (bool)GetValue(ShowReviewColumnProperty);
        set => SetValue(ShowReviewColumnProperty, value);
    }

    private static void OnShowReviewColumnChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is ClientInfoResourceDataGrid grid)
        {
            grid.BuildColumns();
        }
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

        Columns.Add(TextColumn("Name", "Name", 220));
        Columns.Add(TextColumn(
            "Type",
            "TypeLabel",
            group.CategoryName == ClientInfoResourceCategories.ServersInfrastructure
                ? 120
                : 160,
            group.CategoryName == ClientInfoResourceCategories.ServersInfrastructure
                ? 95
                : 120));
        Columns.Add(TextColumn("Provider", "Provider", 160));
        Columns.Add(TextColumn(
            ClientInfoResourceFieldDefinitions.AddressLabelForCategory(
                group.CategoryName),
            "AddressOrUrl",
            220));

        // The lower table is the complete record view. Compact/ShowInGrid flags
        // belong to summaries and workbook layouts; they must not hide canonical
        // fields from technicians here.
        foreach (var field in ClientInfoResourceFieldDefinitions
                     .ForEditorCategory(group.CategoryName))
        {
            Columns.Add(FieldColumn(
                FieldHeader(group.CategoryName, field),
                field.FieldKey));
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

        Columns.Add(TextColumn("Location", "LocationName", 150));
        Columns.Add(TextColumn("Status", "Status", 120));
        Columns.Add(TextColumn("Notes", "Notes", 260));
        Columns.Add(TextColumn("Active", "IsActive", 80, 70));
        if (ShowReviewColumn)
        {
            Columns.Add(TextColumn("Review", "ReviewStatus", 120));
        }
        Columns.Add(TextColumn("Last verified", "LastVerifiedAtUtc", 150));
        Columns.Add(TextColumn("Updated", "UpdatedAtUtc", 150));

        if (TryFindResource("WrappedResourceColumnHeaderTemplate") is DataTemplate template)
        {
            foreach (var column in Columns)
            {
                column.HeaderTemplate = template;
            }
        }
    }

    private static string FieldHeader(
        string categoryName,
        ClientInfoResourceFieldDefinition field) =>
        categoryName == ClientInfoResourceCategories.ConnectionInternet
            ? field.FieldKey switch
            {
                "public_wan_ip" => "WAN IP",
                "ssl_vpn_port" => "VPN Port",
                "subnet_cidr" => "Subnet",
                "ip_assignment_type" => "IP Type",
                "usable_static_ip_count" => "Usable #",
                "static_ip_addresses" => "Static IPs",
                "static_ip_range_start" => "First IP",
                "static_ip_range_end" => "Last IP",
                "device_model" => "Model",
                "firmware_version" => "Firmware",
                "isp_provider" => "ISP",
                "support_phone" => "Support",
                "account_number" => "Account #",
                "service_type" => "Service",
                "support_contact" => "Contact",
                _ => field.FieldLabel
            }
            : field.FieldLabel;

    private static DataGridTextColumn TextColumn(
        string header,
        string path,
        double width,
        double minWidth = 90) =>
        new()
        {
            Header = header,
            Binding = new WpfBinding(path),
            Width = new DataGridLength(width, DataGridLengthUnitType.Pixel),
            MinWidth = minWidth
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
            SortMemberPath = $"$field:{fieldKey}",
            Width = new DataGridLength(165, DataGridLengthUnitType.Pixel),
            MinWidth = 120
        };

    private void ResourceGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        var view = ItemsSource as ListCollectionView
            ?? CollectionViewSource.GetDefaultView(ItemsSource)
                as ListCollectionView;
        if (!e.Column.SortMemberPath.StartsWith("$field:", StringComparison.Ordinal))
        {
            if (view?.CustomSort is not null)
            {
                view.CustomSort = null;
            }

            return;
        }

        e.Handled = true;
        var fieldKey = e.Column.SortMemberPath[7..];
        var direction = e.Column.SortDirection != ListSortDirection.Ascending
            ? ListSortDirection.Ascending
            : ListSortDirection.Descending;
        foreach (var column in Columns)
        {
            column.SortDirection = null;
        }

        e.Column.SortDirection = direction;
        if (view is not null)
        {
            view.CustomSort = new ResourceFieldComparer(fieldKey, direction);
        }
    }

    private sealed class ResourceFieldComparer(
        string fieldKey,
        ListSortDirection direction) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            var left = (x as ClientInfoResource)?.GetFieldValue(fieldKey) ?? string.Empty;
            var right = (y as ClientInfoResource)?.GetFieldValue(fieldKey) ?? string.Empty;
            var result = StringComparer.OrdinalIgnoreCase.Compare(left, right);
            return direction == ListSortDirection.Ascending ? result : -result;
        }
    }
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
