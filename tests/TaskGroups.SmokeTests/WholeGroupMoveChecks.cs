using Avalonia.Threading;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.ViewModels.Other;

internal static partial class Program
{
    private static void CheckWholeGroupMoves()
    {
        ConfigurationManager.Current = new MFAConfiguration("whole-groups", "whole-groups", []);
        var original = MaaProcessor.Interface;
        SetPresetInterface(null);
        try
        {
            var id = $"whole-groups-{Guid.NewGuid():N}";
            var settings = Task("settings", string.Empty, true);
            Set(settings, "_isResourceOptionItem", true);
            var cleanup = Task("cleanup", string.Empty, true);
            cleanup.InterfaceItem!.EndAction = EndTaskPolicy.Pipeline;
            var a1 = Task("a1", "a", true);
            var a2 = Task("a2", "a", false);
            var b = Task("b", "b", true);
            var c = Task("c", "c", false);
            var queue = Queue(id, settings, a1, a2, b, c, cleanup);
            var a = queue.TaskItemGroups.Single(group => group.Name == "a");
            a.IsExpanded = false;
            Check(queue.MoveTaskGroup(a, queue.TaskItemGroups.Count), "whole group accepts downward drop");
            Dispatcher.UIThread.RunJobs();
            Check(queue.GetTaskItemsInDisplayOrder().SequenceEqual(new[] { settings, b, c, cleanup, a1, a2 }) &&
                queue.TaskItemViewModels.SequenceEqual(queue.GetTaskItemsInDisplayOrder()),
                "whole group moves all tasks and synchronizes execution order");
            a = queue.TaskItemGroups.Single(group => group.Name == "a");
            Check(!a.IsExpanded && a1.IsChecked && !a2.IsChecked,
                "whole-group move retains expansion and individual selections");
            Check(!queue.MoveTaskGroup(a, 0) &&
                !queue.MoveTaskGroup(queue.TaskItemGroups[0], queue.TaskItemGroups.Count),
                "whole-group move cannot displace resource settings");
            Check(queue.MoveTaskGroup(a, 1), "whole group accepts upward drop after settings");
            Dispatcher.UIThread.RunJobs();
            Check(queue.GetTaskItemsInDisplayOrder().SequenceEqual(new[] { settings, a1, a2, b, c, cleanup }),
                "upward whole-group move preserves internal order");
            var target = queue.TaskItemGroups.Single(group => group.Name == "b");
            Check(queue.MoveTaskToGroup(a2, target, 0), "cross-group task drag remains available after whole-group move");
            Dispatcher.UIThread.RunJobs();
            target = queue.TaskItemGroups.Single(group => group.Name == "b");
            Check(queue.MoveTaskGroup(target, queue.TaskItemGroups.Count), "destination group can move after accepting a task");
            Dispatcher.UIThread.RunJobs();
            Check(queue.GetTaskItemsInDisplayOrder().SequenceEqual(new[] { settings, a1, c, cleanup, a2, b }),
                "new member moves with destination group at the inserted position");
            var saved = new InstanceConfiguration(id);
            var reopened = Queue(id, saved.GetValue(ConfigurationKeys.TaskItems,
                new List<MaaInterface.MaaInterfaceTask>()).Select(Task).ToArray());
            Check(reopened.GetTaskItemsInDisplayOrder().Select(item => item.InterfaceItem!.LocalId)
                .SequenceEqual(new[] { "a1", "c", "cleanup", "a2", "b" }) &&
                reopened.TaskItemGroups.Single(group => group.Name == "b").Items.Select(item => item.InterfaceItem!.LocalId)
                    .SequenceEqual(new[] { "a2", "b" }),
                "disk reopen retains whole-group order and reassigned task membership");
            target = queue.TaskItemGroups.Single(group => group.Name == "b");
            queue.IsRunning = true;
            Check(!queue.MoveTaskGroup(target, 2), "whole-group drag is blocked while running");
            queue.IsRunning = false;
            Check(!queue.MoveTaskGroup(new TaskItemGroupViewModel { Name = "b" }, 2) &&
                !queue.MoveTaskGroup(target, -1) && !queue.MoveTaskGroup(target, queue.TaskItemGroups.Count + 1),
                "whole-group move rejects foreign groups and invalid destinations");

            // A saved interleaved layout must still move every task belonging to the header.
            var splitId = $"whole-split-{Guid.NewGuid():N}";
            var split = Queue(splitId, Task("s1", "same", true), Task("gap", "other", false), Task("s2", "same", false));
            Check(split.MoveTaskGroup(split.TaskItemGroups.Last(group => group.Name == "same"), split.TaskItemGroups.Count),
                "dragging any legacy group header moves the entire logical group");
            Dispatcher.UIThread.RunJobs();
            Check(split.TaskItemGroups.Count(group => group.Name == "same") == 1 &&
                split.GetTaskItemsInDisplayOrder().Select(item => item.InterfaceItem!.LocalId)
                    .SequenceEqual(new[] { "gap", "s1", "s2" }),
                "whole-group move gathers legacy members without changing their relative order");

            CheckEmptyGroupMoves();
        }
        finally
        {
            SetPresetInterface(original);
        }
    }

    private static void CheckEmptyGroupMoves()
    {
        var id = $"whole-empty-{Guid.NewGuid():N}";
        new InstanceConfiguration(id).SetValue(ConfigurationKeys.TaskGroupLayout, new TaskGroupLayout
        {
            Groups = [new() { Name = "empty1", Label = "Empty 1" }, new() { Name = "empty2", Label = "Empty 2" }]
        });
        var first = Task("first", "full", true);
        var last = Task("last", string.Empty, false);
        var queue = Queue(id, first, last);
        Check(queue.MoveTaskGroup(queue.TaskItemGroups.Single(group => group.Name == "empty2"), 0),
            "empty group supports upward drag");
        Dispatcher.UIThread.RunJobs();
        Check(queue.MoveTaskGroup(queue.TaskItemGroups.Single(group => group.Name == "empty1"), 0),
            "another empty group can be inserted before it");
        Dispatcher.UIThread.RunJobs();
        var saved = new InstanceConfiguration(id);
        var reopened = Queue(id, saved.GetValue(ConfigurationKeys.TaskItems,
            new List<MaaInterface.MaaInterfaceTask>()).Select(Task).ToArray());
        Check(reopened.TaskItemGroups.Take(3).Select(group => group.Name).SequenceEqual(new[] { "empty1", "empty2", "full" }),
            "empty groups retain order and position across disk reopen");
        Check(reopened.MoveTaskToGroup(reopened.TaskItemViewModels.Single(item => item.InterfaceItem!.LocalId == "last"),
            reopened.TaskItemGroups[0], 0), "task can join a moved empty group");
        Dispatcher.UIThread.RunJobs();
        Check(reopened.TaskItemGroups[0].Name == "empty1" && reopened.TaskItemGroups[0].Items.Count == 1,
            "filling a moved empty group keeps its chosen position");
        Check(reopened.MoveTaskGroup(reopened.TaskItemGroups.Single(group => group.Name == "empty2"), reopened.TaskItemGroups.Count),
            "empty group supports drop at the end");
        Dispatcher.UIThread.RunJobs();
        reopened.Processor.InstanceConfiguration.ReloadFromDisk();
        reopened.RebuildTaskItemGroups();
        Check(reopened.TaskItemGroups.Last().Name == "empty2" && reopened.TaskItemGroups.Last().Items.Count == 0,
            "empty group at the end survives rebuild and disk reload");
    }
}
