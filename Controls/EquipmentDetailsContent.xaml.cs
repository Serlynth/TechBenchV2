using System.Collections;
using System.Windows;
using System.Windows.Input;
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

    public static readonly DependencyProperty LaunchAnyDeskCommandProperty =
        DependencyProperty.Register(
            nameof(LaunchAnyDeskCommand),
            typeof(ICommand),
            typeof(EquipmentDetailsContent));

    public static readonly DependencyProperty AttachmentsProperty =
        DependencyProperty.Register(
            nameof(Attachments),
            typeof(IEnumerable),
            typeof(EquipmentDetailsContent));

    public static readonly DependencyProperty OpenAttachmentCommandProperty =
        DependencyProperty.Register(
            nameof(OpenAttachmentCommand),
            typeof(ICommand),
            typeof(EquipmentDetailsContent));

    public static readonly DependencyProperty CopyAttachmentCommandProperty =
        DependencyProperty.Register(
            nameof(CopyAttachmentCommand),
            typeof(ICommand),
            typeof(EquipmentDetailsContent));

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

    public ICommand? LaunchAnyDeskCommand
    {
        get => (ICommand?)GetValue(LaunchAnyDeskCommandProperty);
        set => SetValue(LaunchAnyDeskCommandProperty, value);
    }

    public IEnumerable? Attachments
    {
        get => (IEnumerable?)GetValue(AttachmentsProperty);
        set => SetValue(AttachmentsProperty, value);
    }

    public ICommand? OpenAttachmentCommand
    {
        get => (ICommand?)GetValue(OpenAttachmentCommandProperty);
        set => SetValue(OpenAttachmentCommandProperty, value);
    }

    public ICommand? CopyAttachmentCommand
    {
        get => (ICommand?)GetValue(CopyAttachmentCommandProperty);
        set => SetValue(CopyAttachmentCommandProperty, value);
    }
}
