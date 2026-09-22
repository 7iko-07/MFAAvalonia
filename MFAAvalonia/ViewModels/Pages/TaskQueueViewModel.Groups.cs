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
    private string TaskGroupLayoutKey => $"Instance.{Processor.InstanceId}.{ConfigurationKeys.TaskGroupLayout}";
    private TaskGroupLayout ReadTaskGroupLayout() => ConfigurationManager.Current.GetValue(TaskGroupLayoutKey, new TaskGroupLayout());
    private void SaveTaskGroupLayout(TaskGroupLayout layout) => ConfigurationManager.Current.SetValue(TaskGroupLayoutKey, layout);

    private void PersistTaskGroupOrder()
    {
        var layout = ReadTaskGroupLayout();
        // Retain groups inferred from task metadata even after their last task is moved out.
        foreach (var group in TaskItemGroups.Where(group => !group.IsUngrouped))
            if (layout.Groups.All(saved => saved.Name != group.Name))
                layout.Groups.Add(new TaskGroupDefinition { Name = group.Name, Label = group.Label });
        layout.TaskOrder = GetTaskItemsInDisplayOrder().Where(item => item.InterfaceItem?.LocalId != null)
            .Select(item => item.InterfaceItem!.LocalId!).ToList();
        SaveTaskGroupLayout(layout);
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
        group.Label = label;
        SaveTaskGroupLayout(layout);
    }

    public void DeleteTaskGroup(TaskItemGroupViewModel group)
    {
        if (!Idle || group.IsUngrouped || !TaskItemGroups.Contains(group)) return;
        var layout = ReadTaskGroupLayout();
        layout.Groups.RemoveAll(entry => entry.Name == group.Name);
        if (!layout.DeletedGroups.Contains(group.Name)) layout.DeletedGroups.Add(group.Name);
        foreach (var item in group.Items)
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
        return true;
    }
}
