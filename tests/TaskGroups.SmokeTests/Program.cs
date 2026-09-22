using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Avalonia.Threading;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Other;
using MFAAvalonia.ViewModels.Pages;
using Newtonsoft.Json;

internal static class Program
{
    [ModuleInitializer]
    internal static void ResolveDependencies()
    {
        Environment.CurrentDirectory = AppContext.BaseDirectory;
        var build = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../bin/AnyCPU/Debug"));
        AssemblyLoadContext.Default.Resolving += (_, name) =>
            File.Exists(Path.Combine(build, name.Name + ".dll"))
                ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(build, name.Name + ".dll")) : null;
        Environment.SetEnvironmentVariable("PATH", Path.Combine(build, "runtimes/win-x64/native") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
    }

    private static void Set(object value, string field, object? data) =>
        value.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(value, data);
    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
        Console.WriteLine("PASS " + description);
    }
    // Bypass startup workers and device access; exercise the real grouping methods and persistence.
    private static TaskQueueViewModel Queue(string instanceId, params DragItemViewModel[] tasks)
    {
        var processor = (MaaProcessor)RuntimeHelpers.GetUninitializedObject(typeof(MaaProcessor));
        Set(processor, "<InstanceId>k__BackingField", instanceId);
        Set(processor, "<InstanceConfiguration>k__BackingField", new InstanceConfiguration(instanceId));
        var queue = (TaskQueueViewModel)RuntimeHelpers.GetUninitializedObject(typeof(TaskQueueViewModel));
        Set(queue, "_processorField", processor);
        Set(queue, "_taskItemViewModels", new ObservableCollection<DragItemViewModel>(tasks));
        Set(queue, "_taskItemGroups", new ObservableCollection<TaskItemGroupViewModel>());
        foreach (var task in tasks) task.OwnerViewModel = queue;
        queue.RebuildTaskItemGroups();
        typeof(TaskQueueViewModel).GetMethod("SubscribeTaskItemCollection", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(queue, [queue.TaskItemViewModels]);
        // Model the additional subscription made by the bound view.
        queue.TaskItemViewModels.CollectionChanged += (_, _) => { };
        return queue;
    }
    private static DragItemViewModel Task(string id, string group, bool selected)
    {
        var task = (DragItemViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DragItemViewModel));
        Set(task, "_interfaceItem", new MaaInterface.MaaInterfaceTask { LocalId = id, Name = "repeatable", Entry = "entry", Group = [group], Check = selected });
        Set(task, "_isCheckedWithNull", (bool?)selected);
        Set(task, "_isInitialized", true);
        return task;
    }
    public static void Main()
    {
        CheckCollectionChangeReentrancy();
        var profileA = new MFAConfiguration("group-smoke-a", "group-smoke-a", []);
        var profileB = new MFAConfiguration("group-smoke-b", "group-smoke-b", []);
        ConfigurationManager.Current = profileA;
        var first = Task("first", "one", true);
        var second = Task("second", "two", false);
        var third = Task("third", "two", true);
        var queue = Queue("group-smoke", first, second, third);
        var one = queue.TaskItemGroups.Single(g => g.Name == "one");
        var two = queue.TaskItemGroups.Single(g => g.Name == "two");
        Check(one.SelectionState == true && two.SelectionState == null, "selection distinguishes all and partial");
        Check(queue.MoveTaskToGroup(first, two, 1), "cross-group move accepted");
        Check(two.Items.SequenceEqual(new[] { second, first, third }) && one.Items.Count == 0, "move preserves tasks and insertion order");
        Check(first.IsChecked && !second.IsChecked, "move preserves selection");
        queue.RebuildTaskItemGroups();
        Check(queue.TaskItemGroups.Single(g => g.Name == "one").Items.Count == 0, "empty source group retained");
        two = queue.TaskItemGroups.Single(g => g.Name == "two");
        Check(queue.MoveTaskToGroup(third, two, 0), "same-group reorder accepted");
        Check(two.Items.SequenceEqual(new[] { third, second, first }), "same-group reorder preserves all tasks");
        Check(queue.MoveTaskToGroup(third, two, 3), "same-group move to end accepted");
        queue.RenameTaskGroup(two, "Renamed");
        queue.RenameTaskGroup(two, "   ");
        Check(two.Label == "Renamed", "blank group name rejected");
        queue.RebuildTaskItemGroups();
        Check(queue.TaskItemGroups.Single(g => g.Name == "two").Label == "Renamed", "rename survives rebuild");
        var serialized = JsonConvert.SerializeObject(profileA.Config);
        profileA.SetConfig(JsonConvert.DeserializeObject<Dictionary<string, object>>(serialized)!);
        queue.RebuildTaskItemGroups();
        Check(queue.TaskItemGroups.Single(g => g.Name == "two").Items.SequenceEqual(new[] { second, first, third }), "JSON round-trip preserves membership and order");
        ConfigurationManager.Current = profileB;
        queue.RebuildTaskItemGroups();
        Check(queue.TaskItemGroups.Single(g => g.Name == "one").Items.Contains(first), "profile switch restores independent membership");
        ConfigurationManager.Current = profileA;
        queue.RebuildTaskItemGroups();
        two = queue.TaskItemGroups.Single(g => g.Name == "two");
        queue.IsRunning = true;
        Check(!two.CanSelect && !queue.MoveTaskToGroup(first, queue.TaskItemGroups[0], 0), "running blocks selection and drag");
        queue.DeleteTaskGroup(two);
        Check(queue.TaskItemGroups.Contains(two), "running blocks deletion");
        queue.IsRunning = false;
        queue.DeleteTaskGroup(two);
        Check(queue.TaskItemViewModels.Count == 3 && queue.TaskItemGroups.Single(g => g.IsUngrouped).Items.Count == 3, "deletion retains and flattens every task");
        queue.RebuildTaskItemGroups();
        Check(queue.TaskItemGroups.All(g => g.Name != "two"), "deleted resource group does not return");
        queue.AddTaskGroupCommand.Execute(null);
        var custom = queue.TaskItemGroups.Last(g => !g.IsUngrouped);
        Check(custom.Items.Count == 0 && !custom.CanSelect, "new empty group is visible and cannot select");
        Check(queue.MoveTaskToGroup(second, custom, 0) && custom.SelectionState == false, "unchecked task enters empty group with empty checkbox");
        second.IsChecked = true;
        Check(custom.SelectionState == true, "checking task updates group checkbox");
        custom.ToggleSelectionCommand.Execute(null);
        Check(!second.IsChecked && first.IsChecked, "group deselect does not affect other groups");
        custom.ToggleSelectionCommand.Execute(null);
        Check(second.IsChecked, "group select checks eligible tasks");
        queue.MoveTaskToGroup(third, custom, 1);
        third.InterfaceItem!.ExcludeFromSelectAll = true;
        custom.ToggleSelectionCommand.Execute(null);
        custom.ToggleSelectionCommand.Execute(null);
        Check(second.IsChecked && !third.IsChecked && custom.SelectionState == true, "select-all honors excluded tasks and remains toggleable");
        custom.ToggleSelectionCommand.Execute(null);
        Check(!second.IsChecked && !third.IsChecked && custom.SelectionState == false, "second click clears selection");
        var other = Queue("other-instance", Task("first", "one", true));
        Check(other.TaskItemGroups.Any(g => g.Name == "one") && other.TaskItemGroups.All(g => g.Name != custom.Name), "instances keep independent layouts");
        Check(!queue.MoveTaskToGroup(first, other.TaskItemGroups[0], 0), "cross-instance move rejected");
        typeof(TaskQueueViewModel).GetMethod("ResetTaskGroupLayout", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(queue, null);
        Check(queue.TaskItemGroups.Any(g => g.Name == "one") && queue.TaskItemGroups.Any(g => g.Name == "two"), "reset restores default groups");
        Check(queue.TaskItemGroups.All(g => g.Name != custom.Name) && queue.TaskItemGroups.Single(g => g.Name == "one").Items.Contains(first), "reset clears custom groups and membership overrides");
        Console.WriteLine("All task-group smoke checks passed.");
    }

    private static void CheckCollectionChangeReentrancy()
    {
        ConfigurationManager.Current = new MFAConfiguration("group-reentrancy", "group-reentrancy", []);
        var first = Task("first", "one", true);
        var second = Task("second", "two", false);
        var added = Task("added", "one", true);
        var queue = Queue("group-reentrancy", first, second);
        added.OwnerViewModel = queue;

        queue.TaskItemViewModels.Add(added);
        Check(queue.GetTaskItemsInDisplayOrder().SequenceEqual(new[] { first, added, second }),
            "adding to an earlier group updates the projection without collection reentrancy");
        Check(queue.TaskItemViewModels.SequenceEqual(new[] { first, second, added }),
            "canonical reorder waits for collection notifications to finish");
        Dispatcher.UIThread.RunJobs();
        Check(queue.TaskItemViewModels.SequenceEqual(queue.GetTaskItemsInDisplayOrder()),
            "deferred reorder synchronizes canonical and display order");

        queue.TaskItemViewModels.Move(1, 2);
        Dispatcher.UIThread.RunJobs();
        Check(queue.TaskItemViewModels.SequenceEqual(queue.GetTaskItemsInDisplayOrder()),
            "flat move across groups does not reenter collection notifications");
        var saved = queue.Processor.InstanceConfiguration.GetValue(ConfigurationKeys.TaskItems,
            new List<MaaInterface.MaaInterfaceTask>());
        Check(saved.Select(task => task.LocalId).SequenceEqual(
            queue.GetTaskItemsInDisplayOrder().Select(task => task.InterfaceItem!.LocalId)),
            "flat move persists display order");

        var temporary = Task("temporary", "one", false);
        temporary.OwnerViewModel = queue;
        queue.TaskItemViewModels.Add(temporary);
        queue.TaskItemViewModels.Remove(added);
        queue.TaskItemViewModels.Remove(temporary);
        Dispatcher.UIThread.RunJobs();
        Check(queue.TaskItemViewModels.SequenceEqual(new[] { first, second }),
            "batched changes synchronize the latest projection without restoring removed tasks");

        queue.TaskItemViewModels.Add(added);
        var replacement = Task("replacement", "three", true);
        replacement.OwnerViewModel = queue;
        queue.TaskItemViewModels = new ObservableCollection<DragItemViewModel> { replacement };
        queue.TaskItemViewModels.CollectionChanged += (_, _) => { };
        Dispatcher.UIThread.RunJobs();
        Check(queue.TaskItemViewModels.SequenceEqual(new[] { replacement }) &&
            queue.GetTaskItemsInDisplayOrder().SequenceEqual(new[] { replacement }),
            "pending synchronization respects a replacement task collection");

        queue.TaskItemViewModels.Add(first);
        queue.TaskItemViewModels.Clear();
        Dispatcher.UIThread.RunJobs();
        Check(queue.TaskItemViewModels.Count == 0 && queue.GetTaskItemsInDisplayOrder().Count == 0,
            "clearing before synchronization does not restore stale tasks");
    }
}
