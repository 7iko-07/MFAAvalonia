using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Avalonia.Threading;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Pages;

namespace MFAAvalonia.Views.Pages;

public partial class TaskQueueView
{
    private void SelectingItemsControl_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: DragItemViewModel itemViewModel } listBox)
        {
            if (!itemViewModel.IsTaskSupported)
            {
                itemViewModel.EnableSetting = false;
                Dispatcher.UIThread.Post(ClearUnsupportedSelection, DispatcherPriority.Background);
                return;
            }

            SelectTaskForSettings(itemViewModel, listBox);
        }
    }

    private void ClearUnsupportedSelection()
    {
        UpdateTaskItemContainerVisibility();

        // The refresh restores the original selection after all items are loaded.
        if (_taskListViewport != null) return;

        if (DataContext is not TaskQueueViewModel vm || TaskListBox == null)
        {
            return;
        }

        var selectedItem = vm.HasTaskGroups
            ? vm.TaskItemViewModels.FirstOrDefault(item => item.EnableSetting)
            : TaskListBox.SelectedItem as DragItemViewModel;
        if (selectedItem == null)
        {
            var initialItem = vm.TaskItemViewModels.FirstOrDefault(item => item.EnableSetting && (item.IsResourceOptionItem || item.IsTaskSupported))
                ?? vm.TaskItemViewModels.FirstOrDefault(item => item.IsTaskSupported);
            if (initialItem != null)
            {
                SelectTaskForSettings(initialItem, null);
            }
            return;
        }

        if (selectedItem.IsTaskSupported)
        {
            SelectTaskForSettings(selectedItem, null);
            return;
        }

        selectedItem.EnableSetting = false;

        var fallbackItem = vm.TaskItemViewModels
            .FirstOrDefault(item => item.IsTaskSupported);

        if (fallbackItem != null)
        {
            SelectTaskForSettings(fallbackItem, null);
        }
        else
        {
            TaskListBox.SelectedItem = null;
        }
    }

    private void UpdateTaskItemContainerVisibility()
    {
        if (TaskListBox == null)
        {
            return;
        }

        foreach (var container in TaskListBox.GetVisualDescendants().OfType<ListBoxItem>())
        {
            container.IsVisible = container.DataContext is not DragItemViewModel item || item.IsTaskSupported;
        }
    }

    private void SelectTaskForSettings(DragItemViewModel item, Control? source)
    {
        if (!item.IsTaskSupported)
        {
            item.EnableSetting = false;
            return;
        }

        var owningListBox = source as ListBox ?? source?
            .GetVisualAncestors()
            .OfType<ListBox>()
            .FirstOrDefault();
        owningListBox ??= DataContext is TaskQueueViewModel { HasTaskGroups: true }
            ? TaskGroupList.GetVisualDescendants().OfType<ListBox>()
                .FirstOrDefault(list => list.Items.Contains(item))
            : TaskListBox;

        // Each group has its own ListBox, but the task area has one current item.
        foreach (var list in TaskGroupList.GetVisualDescendants().OfType<ListBox>().Append(TaskListBox))
        {
            if (!ReferenceEquals(list, owningListBox) && list.SelectedItem != null)
                list.SetCurrentValue(SelectingItemsControl.SelectedItemProperty, null);
        }

        if (DataContext is TaskQueueViewModel vm)
        {
            foreach (var task in vm.TaskItemViewModels.Where(task => !ReferenceEquals(task, item) && task.EnableSetting))
            {
                task.EnableSetting = false;
            }
        }

        if (!item.EnableSetting)
            item.EnableSetting = true;

        owningListBox?.SetCurrentValue(SelectingItemsControl.SelectedItemProperty, item);
    }
}
