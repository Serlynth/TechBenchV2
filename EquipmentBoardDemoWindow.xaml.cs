using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TechBench.Controls;
using TechBench.Models;
using TechBench.ViewModels;

namespace TechBench;

public partial class EquipmentBoardDemoWindow : Window
{
    private System.Windows.Point _equipmentDragStartPoint;
    private EquipmentItem? _pendingEquipmentDragItem;
    private bool _equipmentDragStarted;
    private EquipmentDragPreview? _equipmentDragPreview;
    private GridLength _expandedEquipmentDeploymentHeight =
        new(245, GridUnitType.Pixel);

    public EquipmentBoardDemoWindow()
    {
        InitializeComponent();
        DataContext = new EquipmentBoardDemoViewModel();
    }

    private void EquipmentDeploymentExpander_Collapsed(
        object sender,
        RoutedEventArgs e)
    {
        if (EquipmentDeploymentRow is null
            || EquipmentDeploymentSplitter is null)
        {
            return;
        }

        if (EquipmentDeploymentRow.ActualHeight > 90)
        {
            _expandedEquipmentDeploymentHeight =
                new GridLength(
                    EquipmentDeploymentRow.ActualHeight,
                    GridUnitType.Pixel);
        }

        EquipmentDeploymentSplitter.Visibility = Visibility.Collapsed;
        EquipmentDeploymentRow.Height = GridLength.Auto;
    }

    private void EquipmentDeploymentExpander_Expanded(
        object sender,
        RoutedEventArgs e)
    {
        if (EquipmentDeploymentRow is null
            || EquipmentDeploymentSplitter is null)
        {
            return;
        }

        EquipmentDeploymentRow.Height = _expandedEquipmentDeploymentHeight;
        EquipmentDeploymentSplitter.Visibility = Visibility.Visible;
    }

    private void EquipmentLaneListBox_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        _equipmentDragStartPoint = e.GetPosition(null);
        _equipmentDragStarted = false;
        _pendingEquipmentDragItem =
            FindVisualAncestor<System.Windows.Controls.ListBoxItem>(
                e.OriginalSource as DependencyObject)?.DataContext as EquipmentItem;
        if (_pendingEquipmentDragItem is not null
            && sender is System.Windows.Controls.ListBox listBox)
        {
            listBox.CaptureMouse();
        }
    }

    private void EquipmentLaneListBox_PreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox listBox
            && listBox.IsMouseCaptured)
        {
            listBox.ReleaseMouseCapture();
        }

        if (!_equipmentDragStarted
            && _pendingEquipmentDragItem is { } equipment
            && DataContext is EquipmentBoardDemoViewModel viewModel)
        {
            viewModel.SelectedEquipment = equipment;
        }

        _pendingEquipmentDragItem = null;
        _equipmentDragStarted = false;
    }

    private void EquipmentLaneListBox_PreviewMouseMove(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed
            || sender is not System.Windows.Controls.ListBox listBox)
        {
            return;
        }

        var currentPosition = e.GetPosition(null);
        if (Math.Abs(currentPosition.X - _equipmentDragStartPoint.X)
                < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(currentPosition.Y - _equipmentDragStartPoint.Y)
                < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (_pendingEquipmentDragItem is not { } equipment)
        {
            return;
        }

        _equipmentDragStarted = true;
        if (listBox.IsMouseCaptured)
        {
            listBox.ReleaseMouseCapture();
        }
        listBox.SelectedItem = equipment;
        using var preview = new EquipmentDragPreview(listBox, equipment);
        _equipmentDragPreview = preview;
        listBox.GiveFeedback += EquipmentDragSource_GiveFeedback;
        preview.Show();
        try
        {
            System.Windows.DragDrop.DoDragDrop(
                listBox,
                new System.Windows.DataObject(typeof(EquipmentItem), equipment),
                System.Windows.DragDropEffects.Move);
        }
        finally
        {
            listBox.GiveFeedback -= EquipmentDragSource_GiveFeedback;
            _equipmentDragPreview = null;
            _pendingEquipmentDragItem = null;
            _equipmentDragStarted = false;
        }
    }

    private void EquipmentDragSource_GiveFeedback(
        object sender,
        System.Windows.GiveFeedbackEventArgs e)
    {
        _equipmentDragPreview?.UpdatePosition();
        e.UseDefaultCursors = true;
        e.Handled = true;
    }

    private void EquipmentLaneListBox_DragOver(
        object sender,
        System.Windows.DragEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox listBox
            && listBox.DataContext is EquipmentLane
            && e.Data.GetDataPresent(typeof(EquipmentItem)))
        {
            e.Effects = System.Windows.DragDropEffects.Move;
            listBox.Background =
                System.Windows.Application.Current.TryFindResource("AccentSoftBrush")
                    as System.Windows.Media.Brush
                ?? System.Windows.Media.Brushes.Transparent;
            listBox.BorderBrush =
                System.Windows.Application.Current.TryFindResource("AccentBrush")
                    as System.Windows.Media.Brush
                ?? System.Windows.Media.Brushes.DodgerBlue;
            listBox.BorderThickness = new Thickness(2);
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void EquipmentLaneListBox_DragLeave(
        object sender,
        System.Windows.DragEventArgs e) =>
        ResetDropTarget(sender);

    private void EquipmentLaneListBox_Drop(
        object sender,
        System.Windows.DragEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (sender is not System.Windows.Controls.ListBox listBox
            || listBox.DataContext is not EquipmentLane targetLane
            || e.Data.GetData(typeof(EquipmentItem)) is not EquipmentItem equipment
            || DataContext is not EquipmentBoardDemoViewModel viewModel)
        {
            return;
        }

        e.Handled = true;
        ResetDropTarget(sender);
        viewModel.AssignEquipment(
            equipment,
            targetLane,
            GetEquipmentDropIndex(listBox, e, targetLane));
    }

    private static int GetEquipmentDropIndex(
        System.Windows.Controls.ListBox listBox,
        System.Windows.DragEventArgs e,
        EquipmentLane targetLane)
    {
        var hit = listBox.InputHitTest(e.GetPosition(listBox)) as DependencyObject;
        var container =
            FindVisualAncestor<System.Windows.Controls.ListBoxItem>(hit);
        if (container?.DataContext is not EquipmentItem targetEquipment)
        {
            return targetLane.Items.Count;
        }

        var index = targetLane.Items.IndexOf(targetEquipment);
        if (index < 0)
        {
            return targetLane.Items.Count;
        }

        var position = e.GetPosition(container);
        return position.Y > container.ActualHeight / 2
            ? index + 1
            : index;
    }

    private static void ResetDropTarget(object sender)
    {
        if (sender is not System.Windows.Controls.ListBox listBox)
        {
            return;
        }

        listBox.Background = System.Windows.Media.Brushes.Transparent;
        listBox.BorderBrush = System.Windows.Media.Brushes.Transparent;
        listBox.BorderThickness = new Thickness(0);
    }

    private static T? FindVisualAncestor<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
            {
                return match;
            }

            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return null;
    }
}
