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
        => Task(new MaaInterface.MaaInterfaceTask { LocalId = id, Name = "repeatable", Entry = "entry", Group = [group], Check = selected });

    private static DragItemViewModel Task(MaaInterface.MaaInterfaceTask item)
    {
        var task = (DragItemViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DragItemViewModel));
        Set(task, "_interfaceItem", item);
        Set(task, "_isCheckedWithNull", item.Check);
        Set(task, "_isInitialized", true);
        return task;
    }
    public static void Main()
    {
        CheckDefaultTaskPositions();
        CheckSplitGroupOperations();
        CheckRestartPersistence();
        CheckMoveToListEnds();
        CheckCollectionChangeReentrancy();
        var profileA = new MFAConfiguration("group-smoke-a", "group-smoke-a", []);
        var profileB = new MFAConfiguration("group-smoke-b", "group-smoke-b", []);
        ConfigurationManager.Current = profileA;
        var first = Task("first", "one", true);
        var second = Task("second", "two", false);
        var third = Task("third", "two", true);
        var queue = Queue($"group-smoke-{Guid.NewGuid():N}", first, second, third);
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
        queue.Processor.InstanceConfiguration.ReloadFromDisk();
        queue.RebuildTaskItemGroups();
        Check(queue.TaskItemGroups.Single(g => g.Name == "two").Items.SequenceEqual(new[] { second, first, third }), "instance file round-trip preserves membership and order");
        ConfigurationManager.Current = profileB;
        queue.RebuildTaskItemGroups();
        Check(queue.TaskItemGroups.Single(g => g.Name == "two").Items.Contains(first), "global profile switch retains the current instance layout");
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
        var other = Queue($"other-instance-{Guid.NewGuid():N}", Task("first", "one", true));
        Check(other.TaskItemGroups.Any(g => g.Name == "one") && other.TaskItemGroups.All(g => g.Name != custom.Name), "instances keep independent layouts");
        Check(!queue.MoveTaskToGroup(first, other.TaskItemGroups[0], 0), "cross-instance move rejected");
        typeof(TaskQueueViewModel).GetMethod("ResetTaskGroupLayout", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(queue, null);
        Check(queue.TaskItemGroups.Any(g => g.Name == "one") && queue.TaskItemGroups.Any(g => g.Name == "two"), "reset restores default groups");
        Check(queue.TaskItemGroups.All(g => g.Name != custom.Name) && queue.TaskItemGroups.Single(g => g.Name == "one").Items.Contains(first), "reset clears custom groups and membership overrides");
        Console.WriteLine("All task-group smoke checks passed.");
    }

    private static void CheckDefaultTaskPositions()
    {
        ConfigurationManager.Current = new MFAConfiguration("group-default-order", "group-default-order", []);
        var originalInterface = MaaProcessor.Interface;
        try
        {
            typeof(MaaProcessor).GetProperty(nameof(MaaProcessor.Interface))!.SetValue(null, new MaaInterface
            {
                Group =
                [
                    new() { Name = "two", Label = "Second" },
                    new() { Name = "one", Label = "First" },
                ]
            });
            var settings = Task("settings", string.Empty, true);
            Set(settings, "_isResourceOptionItem", true);
            var leading = Task("leading", string.Empty, true);
            var first = Task("first", "one", true);
            var middle = Task("middle", string.Empty, false);
            var second = Task("second", "two", true);
            var trailing = Task("trailing", string.Empty, true);
            var expected = new[] { settings, leading, first, middle, second, trailing };
            var instanceId = $"group-default-order-{Guid.NewGuid():N}";
            var queue = Queue(instanceId, expected);
            Check(queue.GetTaskItemsInDisplayOrder().SequenceEqual(expected) &&
                queue.TaskItemViewModels.SequenceEqual(expected), "default ungrouped tasks retain leading, middle and trailing positions");
            Check(queue.TaskItemGroups.Where(group => !group.IsUngrouped).Select(group => group.Name)
                .SequenceEqual(new[] { "one", "two" }), "group declarations provide labels without overriding task order");
            Check(queue.TaskItemGroups.Single(group => group.Name == "one").Label == "First",
                "groups retain declared labels in task order");
            Check(queue.MoveTaskToGroup(trailing, queue.TaskItemGroups.Single(group => group.Items.Contains(middle)), 0),
                "ungrouped task can move into a middle segment");
            var movedOrder = new[] { "leading", "first", "trailing", "middle", "second" };
            queue.Processor.InstanceConfiguration.ReloadFromDisk();
            queue.RebuildTaskItemGroups();
            Check(queue.GetTaskItemsInDisplayOrder().Where(task => !task.IsResourceOptionItem)
                .Select(task => task.InterfaceItem!.LocalId).SequenceEqual(movedOrder),
                "middle ungrouped insertion survives layout rebuild");
            var saved = new InstanceConfiguration(instanceId);
            var reopened = Queue(instanceId, saved.GetValue(ConfigurationKeys.TaskItems,
                new List<MaaInterface.MaaInterfaceTask>()).Select(Task).ToArray());
            Check(reopened.GetTaskItemsInDisplayOrder().Select(task => task.InterfaceItem!.LocalId).SequenceEqual(movedOrder),
                "manual ungrouped positions survive disk reopen");

            // ResetTasks reloads the source array after clearing the saved layout.
            typeof(TaskQueueViewModel).GetMethod("ResetTaskGroupLayout", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(queue, null);
            queue.TaskItemViewModels = new ObservableCollection<DragItemViewModel>(expected);
            Dispatcher.UIThread.RunJobs();
            Check(queue.GetTaskItemsInDisplayOrder().SequenceEqual(expected), "reset source tasks restores their authored positions");
        }
        finally { typeof(MaaProcessor).GetProperty(nameof(MaaProcessor.Interface))!.SetValue(null, originalInterface); }
        Dispatcher.UIThread.RunJobs();
    }

    private static void CheckSplitGroupOperations()
    {
        ConfigurationManager.Current = new MFAConfiguration("group-split", "group-split", []);
        var first = Task("first", "one", true);
        var middle = Task("middle", string.Empty, false);
        var second = Task("second", "one", false);
        var other = Task("other", "two", true);
        var third = Task("third", "one", true);
        var expected = new[] { first, middle, second, other, third };
        var instanceId = $"group-split-{Guid.NewGuid():N}";
        var queue = Queue(instanceId, expected);
        var segments = queue.TaskItemGroups.Where(group => group.Name == "one").ToList();
        Check(segments.Count == 3 && queue.GetTaskItemsInDisplayOrder().SequenceEqual(expected),
            "nonconsecutive tasks in the same group remain separated by authored tasks");
        Check(segments.All(group => group.SelectionState == null), "split group selection includes every segment");
        segments[1].ToggleSelectionCommand.Execute(null);
        Check(first.IsChecked && second.IsChecked && third.IsChecked && !middle.IsChecked,
            "split group selection updates all group tasks without selecting the ungrouped gap");
        second.IsChecked = false;
        Check(segments.All(group => group.SelectionState == null), "split group selection reacts to changes in other segments");
        segments[1].IsExpanded = false;
        Check(segments.All(group => !group.IsExpanded), "collapse applies to every segment of the same group");
        queue.RenameTaskGroup(segments[1], "Renamed split");
        Check(segments.All(group => group.Label == "Renamed split"), "rename applies to every segment of the same group");
        queue.RebuildTaskItemGroups();
        Check(queue.TaskItemGroups.Where(group => group.Name == "one").All(group =>
                group.Label == "Renamed split" && !group.IsExpanded) && queue.GetTaskItemsInDisplayOrder().SequenceEqual(expected),
            "split group rebuild retains labels, expansion and task order");
        Check(queue.MoveTaskToGroup(third, queue.TaskItemGroups.First(group => group.Name == "one"), 0),
            "task can move between two segments of the same group");
        var movedOrder = new[] { "third", "first", "middle", "second", "other" };
        queue.RebuildTaskItemGroups();
        Check(queue.GetTaskItemsInDisplayOrder().Select(task => task.InterfaceItem!.LocalId).SequenceEqual(movedOrder) &&
            queue.TaskItemGroups.Count(group => group.Name == "one") == 2,
            "same-group segment move preserves gaps and coalesces adjacent tasks");
        var saved = new InstanceConfiguration(instanceId);
        var reopened = Queue(instanceId, saved.GetValue(ConfigurationKeys.TaskItems,
            new List<MaaInterface.MaaInterfaceTask>()).Select(Task).ToArray());
        Check(reopened.GetTaskItemsInDisplayOrder().Select(task => task.InterfaceItem!.LocalId).SequenceEqual(movedOrder) &&
            reopened.TaskItemGroups.Count(group => group.Name == "one") == 2,
            "split groups and dragged positions survive disk reopen");
        reopened.DeleteTaskGroup(reopened.TaskItemGroups.Last(group => group.Name == "one"));
        Check(reopened.TaskItemGroups.All(group => group.Name != "one") &&
            reopened.GetTaskItemsInDisplayOrder().Select(task => task.InterfaceItem!.LocalId).SequenceEqual(movedOrder),
            "deleting a split group detaches all its tasks in place");
        reopened.Processor.InstanceConfiguration.ReloadFromDisk();
        reopened.RebuildTaskItemGroups();
        Check(reopened.TaskItemGroups.All(group => group.Name != "one") &&
            reopened.GetTaskItemsInDisplayOrder().Select(task => task.InterfaceItem!.LocalId).SequenceEqual(movedOrder),
            "split group deletion and positions survive disk reload");
        var mergeFirst = Task("merge-first", "one", true);
        var mergeMiddle = Task("merge-middle", string.Empty, false);
        var mergeSecond = Task("merge-second", "one", true);
        var merging = Queue($"group-merge-{Guid.NewGuid():N}", mergeFirst, mergeMiddle, mergeSecond);
        Check(merging.MoveTaskToGroup(mergeMiddle, merging.TaskItemGroups.First(group => group.Name == "one"), 1),
            "gap task can join the neighboring group");
        Dispatcher.UIThread.RunJobs();
        Check(merging.TaskItemGroups.Count == 1 && merging.TaskItemGroups[0].Name == "one" &&
            merging.GetTaskItemsInDisplayOrder().SequenceEqual(new[] { mergeFirst, mergeMiddle, mergeSecond }),
            "drop refresh merges adjacent segments and removes the empty ungrouped gap");
        Dispatcher.UIThread.RunJobs();
    }

    private static void CheckRestartPersistence()
    {
        ConfigurationManager.Current = new MFAConfiguration("group-restart", "group-restart", []);
        var instanceId = $"group-restart-{Guid.NewGuid():N}";
        var first = Task("first", "kept", true);
        var special = Task("special", string.Empty, true);
        special.InterfaceItem!.Name = "Countdown";
        special.InterfaceItem.Entry = "CountdownAction";
        var removed = Task("removed", "removed-group", false);
        var instance = new InstanceConfiguration(instanceId);
        instance.SetValue(ConfigurationKeys.TaskItems,
            new[] { removed, first, special }.Select(task => task.InterfaceItem).ToList());

        // Reproduce startup migrating the layout written by the old grouping code.
        var legacyKey = $"Instance.{instanceId}.{ConfigurationKeys.TaskGroupLayout}";
        ConfigurationManager.Current.SetValue(legacyKey, new TaskGroupLayout
        {
            Groups = [new TaskGroupDefinition { Name = "kept", Label = "Kept" }],
            DeletedGroups = ["removed-group"],
            TaskGroups = new() { ["special"] = "kept", ["removed"] = string.Empty },
            TaskOrder = ["removed", "first", "special"]
        });
        var manager = (MaaProcessorManager)RuntimeHelpers.GetUninitializedObject(typeof(MaaProcessorManager));
        typeof(MaaProcessorManager).GetMethod("MigrateScopedKeysToFiles", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(manager, [Path.GetDirectoryName(instance.GetConfigFilePath())!]);
        Check(!ConfigurationManager.Current.ContainsKey(legacyKey), "startup migration removes the legacy layout key");
        instance.ReloadFromDisk();
        var savedTasks = instance.GetValue(ConfigurationKeys.TaskItems, new List<MaaInterface.MaaInterfaceTask>());
        var queue = Queue(instanceId, savedTasks.Select(Task).ToArray());
        Check(queue.TaskItemGroups.Single(group => group.Name == "kept").Items.Select(task => task.InterfaceItem!.LocalId)
            .SequenceEqual(new[] { "first", "special" }), "startup migration retains built-in action group and position");
        Check(queue.TaskItemGroups.All(group => group.Name != "removed-group") && queue.TaskItemViewModels.Count == 3,
            "startup migration retains deleted groups and all their tasks");
        Check(queue.TaskItemGroups.Single(group => group.IsUngrouped).Items.Single().InterfaceItem!.LocalId == "removed",
            "deleted group tasks reopen in the ungrouped area");

        var builtIn = queue.TaskItemViewModels.Single(task => task.InterfaceItem!.LocalId == "special");
        var kept = queue.TaskItemGroups.Single(group => group.Name == "kept");
        Check(queue.MoveTaskToGroup(builtIn, kept, 0) && queue.MoveTaskToGroup(builtIn, kept, 2),
            "built-in action can move below an ordinary task");
        var moved = new InstanceConfiguration(instanceId);
        var movedQueue = Queue(instanceId, moved.GetValue(ConfigurationKeys.TaskItems, new List<MaaInterface.MaaInterfaceTask>())
            .Select(Task).ToArray());
        Check(movedQueue.TaskItemGroups.Single(group => group.Name == "kept").Items.Select(task => task.InterfaceItem!.LocalId)
            .SequenceEqual(new[] { "first", "special" }), "disk reopen retains built-in action below an ordinary task");
        queue.DeleteTaskGroup(queue.TaskItemGroups.Single(group => group.Name == "kept"));
        queue.TaskItemViewModels.Move(queue.TaskItemViewModels.IndexOf(builtIn), 0);
        queue.TaskItemViewModels.Move(0, 2);
        var expectedOrder = queue.GetTaskItemsInDisplayOrder().Select(task => task.InterfaceItem!.LocalId).ToList();
        Check(expectedOrder.SequenceEqual(new[] { "removed", "first", "special" }),
            "flat list keeps the built-in action after ordinary tasks");
        var reopened = new InstanceConfiguration(instanceId);
        var reloaded = Queue(instanceId, reopened.GetValue(ConfigurationKeys.TaskItems, new List<MaaInterface.MaaInterfaceTask>())
            .Select(Task).ToArray());
        Check(reloaded.TaskItemGroups.All(group => group.IsUngrouped) && reloaded.TaskItemViewModels.Count == 3,
            "disk reopen keeps newly deleted groups removed without losing tasks");
        Check(reloaded.GetTaskItemsInDisplayOrder().Select(task => task.InterfaceItem!.LocalId).SequenceEqual(expectedOrder),
            "disk reopen retains built-in action order after moving and deleting groups");
        Check(!ConfigurationManager.Current.ContainsKey(legacyKey), "layout edits use the instance file instead of recreating legacy keys");
        Check(new InstanceConfiguration(instanceId).GetValue(ConfigurationKeys.TaskGroupLayout, new TaskGroupLayout())
            .DeletedGroups.Contains("kept"), "latest group deletion is saved to the instance file");

        var other = Queue($"group-restart-other-{Guid.NewGuid():N}", Task("first", "kept", true));
        Check(other.TaskItemGroups.Any(group => group.Name == "kept"), "opening another instance does not inherit deleted groups");
        reloaded.RebuildTaskItemGroups();
        Check(reloaded.TaskItemGroups.All(group => group.IsUngrouped), "returning to the original instance preserves its deleted groups");
        Dispatcher.UIThread.RunJobs();
    }

    private static void CheckMoveToListEnds()
    {
        ConfigurationManager.Current = new MFAConfiguration("group-ends", "group-ends", []);
        var instanceId = $"group-ends-{Guid.NewGuid():N}";
        var settings = Task("settings", string.Empty, true);
        Set(settings, "_isResourceOptionItem", true);
        var first = Task("first", "one", true);
        var second = Task("second", "one", false);
        var third = Task("third", "two", true);
        var special = Task("special", string.Empty, true);
        special.InterfaceItem!.Entry = "CountdownAction";
        var queue = Queue(instanceId, settings, special, first, second, third);

        Check(queue.MoveTaskToTop(second), "move to top accepts a grouped task");
        Check(queue.GetTaskItemsInDisplayOrder().SequenceEqual(new[] { settings, second, special, first, third }),
            "move to top places task before all groups and after settings");
        Check(queue.TaskItemGroups.Single(group => group.Items.Contains(second)).IsUngrouped && !second.IsChecked,
            "move to top leaves the original group and retains selection");
        Check(queue.MoveTaskToBottom(first), "move to bottom accepts a grouped task");
        Check(queue.GetTaskItemsInDisplayOrder().SequenceEqual(new[] { settings, second, special, third, first }) &&
            queue.TaskItemViewModels.SequenceEqual(queue.GetTaskItemsInDisplayOrder()),
            "move to bottom synchronizes display and execution order after all groups");
        Check(queue.TaskItemGroups.Last().IsUngrouped && queue.TaskItemGroups.Last().Items.Contains(first) &&
            queue.TaskItemGroups.Single(group => group.Name == "one").Items.Count == 0,
            "bottom task is ungrouped and its empty source group remains");
        Check(queue.MoveTaskToBottom(third) && queue.MoveTaskToTop(first) && queue.MoveTaskToBottom(second),
            "repeated end moves accept tasks already outside groups");
        Check(queue.GetTaskItemsInDisplayOrder().SequenceEqual(new[] { settings, first, special, third, second }) &&
            queue.TaskItemViewModels.Distinct().Count() == 5,
            "repeated end moves keep every task exactly once in requested order");
        Check(queue.MoveTaskToBottom(special) && queue.MoveTaskToTop(special),
            "built-in actions support both global end moves");

        var expected = queue.GetTaskItemsInDisplayOrder().Where(task => !task.IsResourceOptionItem)
            .Select(task => task.InterfaceItem!.LocalId).ToList();
        var saved = new InstanceConfiguration(instanceId);
        var reopenedSettings = Task("settings", string.Empty, true);
        Set(reopenedSettings, "_isResourceOptionItem", true);
        var reopened = Queue(instanceId, new[] { reopenedSettings }.Concat(saved.GetValue(ConfigurationKeys.TaskItems,
            new List<MaaInterface.MaaInterfaceTask>()).Select(Task)).ToArray());
        Check(reopened.GetTaskItemsInDisplayOrder().Where(task => !task.IsResourceOptionItem)
            .Select(task => task.InterfaceItem!.LocalId).SequenceEqual(expected), "global end moves survive disk reopen");
        var layout = saved.GetValue(ConfigurationKeys.TaskGroupLayout, new TaskGroupLayout());
        Check(layout.TaskGroups.Values.All(string.IsNullOrEmpty) && layout.Groups.All(group => !string.IsNullOrEmpty(group.Label)) &&
            reopened.TaskItemGroups.Where(group => group.Items.Any(task => !task.IsResourceOptionItem)).All(group => group.IsUngrouped),
            "end moves persist detached membership without creating user groups");

        var movedSecond = reopened.TaskItemViewModels.Single(task => task.InterfaceItem!.LocalId == "second");
        Check(reopened.MoveTaskToGroup(movedSecond, reopened.TaskItemGroups.Single(group => group.Name == "one"), 0),
            "a bottom task can be dragged back into a named group");
        var movedSpecial = reopened.TaskItemViewModels.Single(task => task.InterfaceItem!.LocalId == "special");
        var bottom = reopened.TaskItemGroups.Last();
        Check(reopened.MoveTaskToGroup(movedSpecial, bottom, 0) && reopened.GetTaskItemsInDisplayOrder().Last() != movedSpecial,
            "standalone bottom tasks remain freely reorderable");
        reopened.Processor.InstanceConfiguration.ReloadFromDisk();
        reopened.RebuildTaskItemGroups();
        Check(reopened.TaskItemGroups.Single(group => group.Name == "one").Items.Contains(movedSecond) &&
            !reopened.Processor.InstanceConfiguration.GetValue(ConfigurationKeys.TaskGroupLayout, new TaskGroupLayout())
                .BottomTasks.Contains("second"), "dragging back to a group clears saved bottom placement");

        reopened.IsRunning = true;
        Check(!reopened.MoveTaskToTop(movedSecond) && !reopened.MoveTaskToBottom(movedSpecial),
            "running blocks global end moves");
        reopened.IsRunning = false;
        Check(!reopened.MoveTaskToTop(reopenedSettings) && !reopened.MoveTaskToBottom(reopenedSettings) &&
            !reopened.MoveTaskToTop(Task("foreign", "one", true)), "settings and foreign tasks cannot move to list ends");

        var flatId = $"flat-ends-{Guid.NewGuid():N}";
        var flatFirst = Task("flat-first", string.Empty, true);
        var flatSecond = Task("flat-second", string.Empty, true);
        var flatThird = Task("flat-third", string.Empty, false);
        var flat = Queue(flatId, flatFirst, flatSecond, flatThird);
        Check(flat.MoveTaskToBottom(flatFirst) && flat.MoveTaskToTop(flatThird) && !flat.HasTaskGroups &&
            flat.GetTaskItemsInDisplayOrder().SequenceEqual(new[] { flatThird, flatSecond, flatFirst }),
            "global end moves work in a flat list without adding groups");
        flat.TaskItemViewModels.Move(2, 1);
        Dispatcher.UIThread.RunJobs();
        var flatSaved = new InstanceConfiguration(flatId);
        var flatReopened = Queue(flatId, flatSaved.GetValue(ConfigurationKeys.TaskItems, new List<MaaInterface.MaaInterfaceTask>())
            .Select(Task).ToArray());
        Check(flatReopened.GetTaskItemsInDisplayOrder().Select(task => task.InterfaceItem!.LocalId)
            .SequenceEqual(new[] { "flat-third", "flat-first", "flat-second" }) &&
            !flatSaved.GetValue(ConfigurationKeys.TaskGroupLayout, new TaskGroupLayout()).BottomTasks.Contains("flat-first"),
            "manual drag after moving to bottom persists the new position instead of fixing task to bottom");
    }

    private static void CheckCollectionChangeReentrancy()
    {
        ConfigurationManager.Current = new MFAConfiguration("group-reentrancy", "group-reentrancy", []);
        var first = Task("first", "one", true);
        var second = Task("second", "two", false);
        var added = Task("added", "one", true);
        var queue = Queue($"group-reentrancy-{Guid.NewGuid():N}", first, second);
        added.OwnerViewModel = queue;

        queue.TaskItemViewModels.Add(added);
        Check(queue.GetTaskItemsInDisplayOrder().SequenceEqual(new[] { first, second, added }) &&
            queue.TaskItemGroups.Count(group => group.Name == "one") == 2,
            "adding a nonconsecutive group task retains insertion position without collection reentrancy");
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
