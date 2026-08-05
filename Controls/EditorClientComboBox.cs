using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using TechBench.Models;
using TechBench.Services;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace TechBench.Controls;

public sealed class EditorClientCommittedEventArgs(Client client) : EventArgs
{
    public Client Client { get; } = client;
}

public sealed class EditorClientComboBox : WpfComboBox
{
    private const string KeyboardHighlightTag = "KeyboardHighlight";
    private int _keyboardIndex = -1;

    public event EventHandler<EditorClientCommittedEventArgs>? KeyboardClientCommitted;

    public void ResetKeyboardHighlight()
    {
        _keyboardIndex = -1;
        ClearRealizedHighlights();
    }

    protected override void OnPreviewKeyDown(WpfKeyEventArgs e)
    {
        if (e.Key == Key.Enter
            && _keyboardIndex >= 0
            && _keyboardIndex < Items.Count
            && Items[_keyboardIndex] is Client client)
        {
            e.Handled = true;
            ResetKeyboardHighlight();
            KeyboardClientCommitted?.Invoke(
                this,
                new EditorClientCommittedEventArgs(client));
            return;
        }

        if (e.Key == Key.Escape && IsDropDownOpen)
        {
            e.Handled = true;
            ResetKeyboardHighlight();
            SetCurrentValue(IsDropDownOpenProperty, false);
            return;
        }

        if (e.Key is Key.Up or Key.Down)
        {
            // ComboBox implements arrow keys by changing SelectedItem, which
            // rewrites the editable text and closes/rebuilds the dropdown.
            // Do not call the ComboBox base implementation for these keys.
            e.Handled = true;
            SetCurrentValue(IsDropDownOpenProperty, true);
            if (Items.Count == 0)
            {
                return;
            }

            _keyboardIndex = KeyboardListNavigation.GetNextIndex(
                Items.Count,
                _keyboardIndex,
                moveDown: e.Key == Key.Down);
            var nextIndex = _keyboardIndex;
            Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                () => HighlightOption(nextIndex));
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnKeyDown(WpfKeyEventArgs e)
    {
        if (e.Handled && e.Key is Key.Up or Key.Down or Key.Enter or Key.Escape)
        {
            return;
        }

        base.OnKeyDown(e);
    }

    private void HighlightOption(int index)
    {
        if (!IsKeyboardFocusWithin || index < 0 || index >= Items.Count)
        {
            return;
        }

        SetCurrentValue(IsDropDownOpenProperty, true);
        ApplyTemplate();
        UpdateLayout();
        ClearRealizedHighlights();
        var container = ItemContainerGenerator.ContainerFromIndex(index) as ComboBoxItem;
        if (container is null
            && Template.FindName("PART_Popup", this)
                is Popup { Child: { } popupChild })
        {
            var scrollViewer = FindVisualDescendant<ScrollViewer>(popupChild);
            scrollViewer?.ScrollToVerticalOffset(index);
            popupChild.UpdateLayout();
            UpdateLayout();
            ClearRealizedHighlights();
            container = ItemContainerGenerator.ContainerFromIndex(index) as ComboBoxItem;
        }

        if (container is null)
        {
            return;
        }

        container.BringIntoView();
        container.SetCurrentValue(TagProperty, KeyboardHighlightTag);
    }

    private void ClearRealizedHighlights()
    {
        for (var index = 0; index < Items.Count; index++)
        {
            if (ItemContainerGenerator.ContainerFromIndex(index) is ComboBoxItem container
                && Equals(container.Tag, KeyboardHighlightTag))
            {
                container.ClearValue(TagProperty);
            }
        }
    }

    private static T? FindVisualDescendant<T>(DependencyObject? source)
        where T : DependencyObject
    {
        if (source is null)
        {
            return null;
        }

        for (var index = 0;
             index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(source);
             index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(source, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualDescendant<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}
