using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Pages;

namespace MFAAvalonia.Views.Pages;

public partial class TaskQueueView
{
    private sealed record TaskListViewport(TaskQueueViewModel Owner, DragItemViewModel? Selected,
        DragItemViewModel? Anchor, double AnchorY, Vector Offset);

    private TaskListViewport? _taskListViewport;

    private ScrollViewer? GetTaskListScrollViewer() => DataContext is TaskQueueViewModel { HasTaskGroups: true }
        ? TaskGroupScrollViewer
        : TaskListBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private ListBoxItem[] GetVisibleTaskContainers() =>
        (DataContext is TaskQueueViewModel { HasTaskGroups: true } ? TaskGroupList : (Control)TaskListBox)
        .GetVisualDescendants().OfType<ListBoxItem>()
        .Where(item => item.IsEffectivelyVisible && item.Bounds.Height > 0 && item.DataContext is DragItemViewModel)
        .ToArray();

    private void OnTaskListChanging()
    {
        // Several collection/group changes can belong to the same refresh.
        if (_taskListViewport != null || DataContext is not TaskQueueViewModel vm ||
            GetTaskListScrollViewer() is not { IsEffectivelyVisible: true } scroll || scroll.Viewport.Height <= 0) return;

        var selected = vm.TaskItemViewModels.FirstOrDefault(item => item.EnableSetting);
        var visible = GetVisibleTaskContainers()
            .Select(container => (Item: (DragItemViewModel)container.DataContext!,
                Y: container.TranslatePoint(default, scroll)?.Y, Height: container.Bounds.Height))
            .Where(row => row.Y.HasValue && row.Y.Value < scroll.Viewport.Height && row.Y.Value + row.Height > 0)
            .OrderBy(row => row.Y).ToList();
        var anchor = visible.FirstOrDefault(row => ReferenceEquals(row.Item, selected));
        if (anchor.Item == null) anchor = visible.FirstOrDefault();
        var state = new TaskListViewport(vm, selected, anchor.Item, anchor.Y ?? 0, scroll.Offset);
        _taskListViewport = state;

        // Run after collection notifications, selection cleanup and layout.
        Dispatcher.UIThread.Post(() => RestoreTaskListViewport(state), DispatcherPriority.ContextIdle);
    }

    private static bool IsSameTask(DragItemViewModel current, DragItemViewModel previous) =>
        ReferenceEquals(current, previous) ||
        (!string.IsNullOrEmpty(previous.InterfaceItem?.LocalId) &&
            current.InterfaceItem?.LocalId == previous.InterfaceItem.LocalId) ||
        (previous.IsResourceOptionItem && current.IsResourceOptionItem &&
            !string.IsNullOrEmpty(previous.ResourceItem?.Name) && current.ResourceItem?.Name == previous.ResourceItem.Name);

    private void RestoreTaskListViewport(TaskListViewport state)
    {
        if (!ReferenceEquals(_taskListViewport, state) || !ReferenceEquals(DataContext, state.Owner)) return;

        ListBox? list = null;
        var autoScroll = false;
        try
        {
            var selected = state.Selected == null ? null : state.Owner.TaskItemViewModels
                .FirstOrDefault(item => IsSameTask(item, state.Selected) && item.IsTaskSupported);
            if (selected != null)
            {
                list = state.Owner.HasTaskGroups
                    ? TaskGroupList.GetVisualDescendants().OfType<ListBox>()
                        .FirstOrDefault(box => box.Items.Contains(selected))
                    : TaskListBox;
                autoScroll = list?.AutoScrollToSelectedItem ?? false;
                list?.SetCurrentValue(ListBox.AutoScrollToSelectedItemProperty, false);
                SelectTaskForSettings(selected, list);
            }

            UpdateLayout();
            if (GetTaskListScrollViewer() is not { } scroll) return;

            var anchor = state.Anchor == null ? null : GetVisibleTaskContainers()
                .FirstOrDefault(container => IsSameTask((DragItemViewModel)container.DataContext!, state.Anchor));
            var offset = state.Offset;
            if (anchor?.TranslatePoint(default, scroll) is { } position)
                offset = new Vector(offset.X, scroll.Offset.Y + position.Y - state.AnchorY);
            scroll.Offset = offset;
            UpdateLayout();
        }
        finally
        {
            list?.SetCurrentValue(ListBox.AutoScrollToSelectedItemProperty, autoScroll);
            _taskListViewport = null;
        }
    }
}
