using System.Collections;
using System.Windows;
using TechBench.Models;

namespace TechBench.Controls;

public partial class EquipmentDetailsContent : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty EquipmentProperty =
        DependencyProperty.Register(
            nameof(Equipment),
            typeof(EquipmentItem),
            typeof(EquipmentDetailsContent));

    public static readonly DependencyProperty AssignmentHistoryProperty =
        DependencyProperty.Register(
            nameof(AssignmentHistory),
            typeof(IEnumerable),
            typeof(EquipmentDetailsContent));

    public static readonly DependencyProperty ShowSensitiveValueProperty =
        DependencyProperty.Register(
            nameof(ShowSensitiveValue),
            typeof(bool),
            typeof(EquipmentDetailsContent),
            new FrameworkPropertyMetadata(
                false,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public EquipmentDetailsContent()
    {
        InitializeComponent();
    }

    public EquipmentItem? Equipment
    {
        get => (EquipmentItem?)GetValue(EquipmentProperty);
        set => SetValue(EquipmentProperty, value);
    }

    public IEnumerable? AssignmentHistory
    {
        get => (IEnumerable?)GetValue(AssignmentHistoryProperty);
        set => SetValue(AssignmentHistoryProperty, value);
    }

    public bool ShowSensitiveValue
    {
        get => (bool)GetValue(ShowSensitiveValueProperty);
        set => SetValue(ShowSensitiveValueProperty, value);
    }
}
