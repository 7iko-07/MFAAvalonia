using System;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MFAAvalonia.Extensions;
using MFAAvalonia.ViewModels.Other;
using MFAAvalonia.ViewModels.Pages;
using MFAAvalonia.Views.UserControls;
using SukiUI;

namespace MFAAvalonia.Views.Pages;

public partial class TaskQueueView
{
    private const int GroupHoldMilliseconds = 200;
    private const double GroupDragThreshold = 4;
    private Grid? _pressedGroupHeader;
    private IPointer? _groupPointer;
    private Point _groupPressPosition;
    private long _groupPressTime;
    private bool _groupPressMoved;
    private TaskItemGroupViewModel? _draggedGroup;
    private string? _groupDragToken;
    private DragAdorner? _groupDropAdorner;
    private AdornerLayer? _groupAdornerLayer;

    private void ConfigureTaskGroupDragDrop()
    {
        // Avalonia drag events only bubble. Nested task lists mark them handled,
        // so also observe handled events, processing only our own group token.
        TaskGroupList.AddHandler(DragDrop.DragOverEvent, TaskGroups_OnDragOver, RoutingStrategies.Bubble, handledEventsToo: true);
        TaskGroupList.AddHandler(DragDrop.DropEvent, TaskGroups_OnDrop, RoutingStrategies.Bubble, handledEventsToo: true);
        TaskGroupList.AddHandler(DragDrop.DragLeaveEvent, TaskGroups_OnDragLeave, RoutingStrategies.Bubble, handledEventsToo: true);
        Unloaded += (_, _) => ClearGroupDragState();
        DataContextChanged += (_, _) => ClearGroupDragState();
    }

    private void TaskGroupHeader_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_draggedGroup != null || sender is not Grid { DataContext: TaskItemGroupViewModel } header ||
            !e.GetCurrentPoint(header).Properties.IsLeftButtonPressed) return;
        // Preserve checkbox, rename editor and action-button interactions.
        for (var visual = e.Source as Visual; visual != null && visual != header; visual = visual.GetVisualParent())
            if (visual is Button or TextBox) return;

        _pressedGroupHeader = header;
        _groupPointer = e.Pointer;
        _groupPressPosition = e.GetPosition(header);
        _groupPressTime = Stopwatch.GetTimestamp();
        _groupPressMoved = false;
        e.Pointer.Capture(header);
        e.Handled = true;
    }

    private async void TaskGroupHeader_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Grid { DataContext: TaskItemGroupViewModel group } header ||
            header != _pressedGroupHeader || !e.GetCurrentPoint(header).Properties.IsLeftButtonPressed) return;
        var delta = e.GetPosition(header) - _groupPressPosition;
        if (delta.X * delta.X + delta.Y * delta.Y < GroupDragThreshold * GroupDragThreshold) return;
        _groupPressMoved = true;
        if (Stopwatch.GetElapsedTime(_groupPressTime).TotalMilliseconds < GroupHoldMilliseconds ||
            !group.CanEdit || group.Owner != DataContext || group.IsUngrouped) return;

        _draggedGroup = group;
        _groupDragToken = Guid.NewGuid().ToString("N");
        _pressedGroupHeader = null;
        var transfer = new DataTransfer();
        var item = new DataTransferItem();
        item.SetText(_groupDragToken);
        transfer.Add(item);
        e.Handled = true;
        try
        {
            await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move);
        }
        finally
        {
            e.Pointer.Capture(null);
            ClearGroupDragState();
        }
    }

    private void TaskGroupHeader_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender != _pressedGroupHeader) return;
        var header = _pressedGroupHeader;
        _pressedGroupHeader = null;
        var delta = e.GetPosition(header!) - _groupPressPosition;
        if (!_groupPressMoved && delta.X * delta.X + delta.Y * delta.Y < GroupDragThreshold * GroupDragThreshold &&
            header?.DataContext is TaskItemGroupViewModel group)
            group.IsExpanded = !group.IsExpanded;
        e.Pointer.Capture(null);
        _groupPointer = null;
        e.Handled = true;
    }

    private void TaskGroupHeader_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => _pressedGroupHeader = null;

    private bool IsGroupDrag(DragEventArgs e) => _groupDragToken != null &&
        e.DataTransfer.TryGetText(out var token) && token == _groupDragToken;

    private bool TryGetGroupDrop(DragEventArgs e, out int index, out Border? card, out bool after)
    {
        index = -1;
        card = null;
        after = false;
        if (_draggedGroup == null || DataContext is not TaskQueueViewModel owner ||
            owner != _draggedGroup.Owner) return false;
        // Use only outer cards; inner task rows also have group data contexts.
        var cards = TaskGroupList.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("TaskGroupCard") && border.IsVisible).ToList();
        foreach (var candidate in cards)
        {
            var point = e.GetPosition(candidate);
            card = candidate;
            after = point.Y >= candidate.Bounds.Height / 2;
            if (!after) break;
        }
        if (card?.DataContext is not TaskItemGroupViewModel target) return false;
        index = owner.TaskItemGroups.IndexOf(target) + (after ? 1 : 0);
        return owner.CanMoveTaskGroup(_draggedGroup, index);
    }

    private void TaskGroups_OnDragOver(object? sender, DragEventArgs e)
    {
        if (!IsGroupDrag(e)) return;
        e.Handled = true;
        var valid = TryGetGroupDrop(e, out _, out var card, out var after);
        e.DragEffects = valid ? DragDropEffects.Move : DragDropEffects.None;
        if (valid) ShowGroupDropAdorner(card!, after);
        else ClearGroupDropAdorner();

        // Keep destinations reachable when the group list exceeds the viewport.
        var y = e.GetPosition(TaskGroupScrollViewer).Y;
        var height = TaskGroupScrollViewer.Bounds.Height;
        var scroll = y < 32 ? -16 : y > height - 32 ? 16 : 0;
        if (scroll != 0)
            TaskGroupScrollViewer.Offset = new Vector(TaskGroupScrollViewer.Offset.X,
                Math.Clamp(TaskGroupScrollViewer.Offset.Y + scroll, 0,
                    Math.Max(0, TaskGroupScrollViewer.Extent.Height - TaskGroupScrollViewer.Viewport.Height)));
    }

    private void TaskGroups_OnDrop(object? sender, DragEventArgs e)
    {
        if (!IsGroupDrag(e)) return;
        e.Handled = true;
        e.DragEffects = DragDropEffects.None;
        if (TryGetGroupDrop(e, out var index, out _, out _) &&
            _draggedGroup!.Owner!.MoveTaskGroup(_draggedGroup, index))
            e.DragEffects = DragDropEffects.Move;
        ClearGroupDropAdorner();
    }

    private void TaskGroups_OnDragLeave(object? sender, DragEventArgs e)
    {
        if (!IsGroupDrag(e)) return;
        var point = e.GetPosition(TaskGroupList);
        if (!new Rect(TaskGroupList.Bounds.Size).Contains(point)) ClearGroupDropAdorner();
    }

    private void ShowGroupDropAdorner(Border card, bool after)
    {
        var layer = AdornerLayer.GetAdornerLayer(TaskGroupList);
        if (layer == null || card.TranslatePoint(default, layer) is not { } position) return;
        if (_groupDropAdorner == null || _groupAdornerLayer != layer)
        {
            ClearGroupDropAdorner();
            _groupAdornerLayer = layer;
            _groupDropAdorner = new DragAdorner(position.X, card.Bounds.Width,
                SukiTheme.GetInstance().ActiveColorTheme.PrimaryBrush);
            layer.Children.Add(_groupDropAdorner);
        }
        _groupDropAdorner.UpdateXPosition(position.X);
        _groupDropAdorner.UpdateWidth(card.Bounds.Width);
        _groupDropAdorner.UpdatePosition(position.Y + (after ? card.Bounds.Height : 0));
    }

    private void ClearGroupDropAdorner()
    {
        if (_groupDropAdorner != null) _groupAdornerLayer?.Children.Remove(_groupDropAdorner);
        _groupDropAdorner = null;
        _groupAdornerLayer = null;
    }

    private void ClearGroupDragState()
    {
        var header = _pressedGroupHeader;
        _pressedGroupHeader = null;
        if (header != null && _groupPointer?.Captured == header) _groupPointer.Capture(null);
        _groupPointer = null;
        _draggedGroup = null;
        _groupDragToken = null;
        ClearGroupDropAdorner();
    }
}
