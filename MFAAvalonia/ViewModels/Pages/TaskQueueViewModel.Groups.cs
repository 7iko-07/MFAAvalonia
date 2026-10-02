using CommunityToolkit.Mvvm.Input;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Other;
using System;
using System.Linq;

namespace MFAAvalonia.ViewModels.Pages;

public partial class TaskQueueViewModel
{
    // Keep layout alongside TaskItems; startup migrates legacy scoped keys into this file.
    private TaskGroupLayout ReadTaskGroupLayout() => Processor.InstanceConfiguration.GetValue(ConfigurationKeys.TaskGroupLayout, new TaskGroupLayout());
    private void SaveTaskGroupLayout(TaskGroupLayout layout) => Processor.InstanceConfiguration.SetValue(ConfigurationKeys.TaskGroupLayout, layout);

    private void PersistTaskGroupOrder()
    {
        var layout = ReadTaskGroupLayout();
        PreserveTaskGroups(layout);
        layout.TaskOrder = GetTaskItemsInDisplayOrder().Where(item => item.InterfaceItem?.LocalId != null)
            .Select(item => item.InterfaceItem!.LocalId!).ToList();
        SaveTaskGroupLayout(layout);
    }

    private void PreserveTaskGroups(TaskGroupLayout layout)
    {
        // Retain groups inferred from task metadata even after their last task is moved out.
        foreach (var group in TaskItemGroups.Where(group => !group.IsUngrouped))
            if (layout.Groups.All(saved => saved.Name != group.Name))
                layout.Groups.Add(new TaskGroupDefinition { Name = group.Name, Label = group.Label });
    }

    public bool MoveTaskToTop(DragItemViewModel item) => MoveTaskToListEnd(item, toBottom: false);

    public bool MoveTaskToBottom(DragItemViewModel item) => MoveTaskToListEnd(item, toBottom: true);

    private bool MoveTaskToListEnd(DragItemViewModel item, bool toBottom)
    {
        if (!Idle || item.IsResourceOptionItem || item.InterfaceItem?.LocalId is not { } id ||
            !TaskItemViewModels.Contains(item)) return false;

        var layout = ReadTaskGroupLayout();
        PreserveTaskGroups(layout);
        layout.TaskGroups[id] = string.Empty;
        layout.BottomTasks.Remove(id);
        if (toBottom) layout.BottomTasks.Add(id);

        var ordered = GetTaskItemsInDisplayOrder();
        ordered.Remove(item);
        // Settings rows stay before executable tasks.
        ordered.Insert(toBottom ? ordered.Count : ordered.TakeWhile(task => task.IsResourceOptionItem).Count(), item);
        layout.TaskOrder = ordered.Where(task => task.InterfaceItem?.LocalId != null)
            .Select(task => task.InterfaceItem!.LocalId!).ToList();
        SaveTaskGroupLayout(layout);
        RebuildTaskItemGroups();
        PersistTaskItemsInDisplayOrder();
        return true;
    }

    private void ResetTaskGroupLayout()
    {
        SaveTaskGroupLayout(new TaskGroupLayout());
        RebuildTaskItemGroups();
    }

    private void OnTaskGroupConfigurationSwitched(string name) => RebuildTaskItemGroups();

    [RelayCommand]
    private void AddTaskGroup()
    {
        if (!Idle) return;
        var layout = ReadTaskGroupLayout();
        var label = "TaskGroupNew".ToLocalization();
        var suffix = 1;
        while (TaskItemGroups.Any(group => group.Label == $"{label} {suffix}")) suffix++;
        layout.Groups.Add(new TaskGroupDefinition { Name = Guid.NewGuid().ToString("N"), Label = $"{label} {suffix}" });
        SaveTaskGroupLayout(layout);
        RebuildTaskItemGroups();
    }

    public void RenameTaskGroup(TaskItemGroupViewModel group, string label)
    {
        label = label.Trim();
        if (!Idle || group.IsUngrouped || !TaskItemGroups.Contains(group) || string.IsNullOrEmpty(label)) return;
        var layout = ReadTaskGroupLayout();
        var saved = layout.Groups.FirstOrDefault(entry => entry.Name == group.Name);
        if (saved == null)
        {
            saved = new TaskGroupDefinition { Name = group.Name };
            layout.Groups.Add(saved);
        }
        saved.Label = label;
        foreach (var segment in TaskItemGroups.Where(segment => !segment.IsUngrouped && segment.Name == group.Name))
            segment.Label = label;
        SaveTaskGroupLayout(layout);
    }

    internal void SynchronizeTaskGroupExpansion(TaskItemGroupViewModel group, bool expanded)
    {
        if (group.IsUngrouped) return;
        foreach (var segment in TaskItemGroups.Where(segment => !segment.IsUngrouped && segment.Name == group.Name))
            segment.IsExpanded = expanded;
    }

    internal void RefreshTaskGroupSelection(TaskItemGroupViewModel group)
    {
        foreach (var segment in TaskItemGroups.Where(segment => segment == group ||
                     !group.IsUngrouped && !segment.IsUngrouped && segment.Name == group.Name))
            segment.RefreshSelection();
    }

    public void DeleteTaskGroup(TaskItemGroupViewModel group)
    {
        if (!Idle || group.IsUngrouped || !TaskItemGroups.Contains(group)) return;
        var layout = ReadTaskGroupLayout();
        layout.Groups.RemoveAll(entry => entry.Name == group.Name);
        if (!layout.DeletedGroups.Contains(group.Name)) layout.DeletedGroups.Add(group.Name);
        foreach (var item in TaskItemGroups.Where(segment => !segment.IsUngrouped && segment.Name == group.Name)
                     .SelectMany(segment => segment.Items))
            if (item.InterfaceItem?.LocalId != null) layout.TaskGroups[item.InterfaceItem.LocalId] = string.Empty;
        SaveTaskGroupLayout(layout);
        RebuildTaskItemGroups();
        PersistTaskItemsInDisplayOrder();
        PersistTaskGroupOrder();
    }

    public bool MoveTaskToGroup(DragItemViewModel item, TaskItemGroupViewModel target, int index)
    {
        if (!Idle || item.IsResourceOptionItem || item.InterfaceItem == null ||
            !TaskItemViewModels.Contains(item) || !TaskItemGroups.Contains(target)) return false;
        var source = TaskItemGroups.FirstOrDefault(group => group.Items.Contains(item));
        if (source == null) return false;
        _rebuildingTaskGroups = true;
        try
        {
            var oldIndex = source.Items.IndexOf(item);
            source.Items.Remove(item);
            if (source == target && index > oldIndex) index--;
            target.Items.Insert(Math.Clamp(index, 0, target.Items.Count), item);
            var layout = ReadTaskGroupLayout();
            layout.TaskGroups[item.InterfaceItem.LocalId!] = target.IsUngrouped ? string.Empty : target.Name;
            layout.BottomTasks.Remove(item.InterfaceItem.LocalId!);
            SaveTaskGroupLayout(layout);
        }
        finally { _rebuildingTaskGroups = false; }
        _isApplyingGroupedTaskMove = true;
        try
        {
            var ordered = GetTaskItemsInDisplayOrder();
            for (var i = 0; i < ordered.Count; i++)
                TaskItemViewModels.Move(TaskItemViewModels.IndexOf(ordered[i]), i);
        }
        finally { _isApplyingGroupedTaskMove = false; }
        PersistTaskItemsInDisplayOrder();
        PersistTaskGroupOrder();
        // Refresh after the drop event: adjacent runs may have joined, and an
        // emptied segment is only retained when its entire logical group is empty.
        Avalonia.Threading.Dispatcher.UIThread.Post(RebuildTaskItemGroups);
        return true;
    }
}
