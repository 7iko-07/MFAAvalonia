using System.Collections.ObjectModel;
using System.Reflection;
using Avalonia.Threading;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper.ValueType;

internal static partial class Program
{
    private static void CheckTaskReload()
    {
        ConfigurationManager.Current = new MFAConfiguration("reload-smoke", "reload-smoke", []);
        var first = Task("reload-first", "daily", true);
        var second = Task("reload-second", "daily", false);
        var queue = Queue($"reload-{Guid.NewGuid():N}", first, second);
        Set(queue, "_currentResources", new ObservableCollection<MaaInterface.MaaInterfaceResource>());
        Set(first, "_enableSetting", true);
        var originalGroup = queue.TaskItemGroups.Single();
        var changes = 0;
        queue.TaskItemViewModels.CollectionChanged += (_, _) => changes++;
        var loader = new TaskLoader(null, queue);
        var update = typeof(TaskLoader).GetMethod("UpdateViewModels", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var source = new ObservableCollection<DragItemViewModel>();
        update.Invoke(loader, [new List<DragItemViewModel> { first, second }, new List<MaaInterface.MaaInterfaceTask>(), source]);
        Dispatcher.UIThread.RunJobs();
        Check(changes == 0 && ReferenceEquals(queue.TaskItemGroups.Single(), originalGroup) && first.EnableSetting,
            "reloading unchanged tasks retains list containers, groups and the active task");

        var capturedBeforeReset = false;
        queue.TaskListChanging += () => capturedBeforeReset |= queue.TaskItemViewModels.Count == 2 &&
            ReferenceEquals(queue.TaskItemViewModels[0], first);
        update.Invoke(loader, [new List<DragItemViewModel> { second, first }, new List<MaaInterface.MaaInterfaceTask>(), source]);
        Dispatcher.UIThread.RunJobs();
        Check(changes > 0 && capturedBeforeReset && queue.TaskItemViewModels.Contains(first) && queue.TaskItemViewModels.Contains(second),
            "a changed reload captures the viewport before resetting the task collection");

        var groupCaptured = false;
        var groupBeforeRebuild = queue.TaskItemGroups.Single();
        queue.TaskListChanging += () => groupCaptured |= ReferenceEquals(queue.TaskItemGroups.Single(), groupBeforeRebuild);
        queue.RebuildTaskItemGroups();
        Check(groupCaptured && !ReferenceEquals(queue.TaskItemGroups.Single(), groupBeforeRebuild),
            "group refresh notifies the view before discarding its old containers");

        var originalInterface = MaaProcessor.Interface;
        try
        {
            var definition = new MaaInterface { Group = [new() { Name = "empty-reload" }] };
            SetPresetInterface(definition);
            var emptyQueue = Queue($"empty-reload-{Guid.NewGuid():N}");
            Set(emptyQueue, "_currentResources", new ObservableCollection<MaaInterface.MaaInterfaceResource>());
            emptyQueue.TaskItemGroups.Clear();
            update.Invoke(new TaskLoader(definition, emptyQueue),
                [new List<DragItemViewModel>(), new List<MaaInterface.MaaInterfaceTask>(), source]);
            Check(emptyQueue.HasTaskGroups && emptyQueue.TaskItemGroups.Single().Name == "empty-reload",
                "initial loading still creates declared groups when the task list is empty");
        }
        finally { SetPresetInterface(originalInterface); }
    }
}
