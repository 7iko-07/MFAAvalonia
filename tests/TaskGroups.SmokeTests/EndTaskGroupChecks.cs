using System.Reflection;
using Avalonia.Threading;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using Newtonsoft.Json;

internal static partial class Program
{
    private static void CheckCombinedShutdownTasks()
    {
        ConfigurationManager.Current = new MFAConfiguration("combined-ending", "combined-ending", []);
        var original = MaaProcessor.Interface;
        try
        {
            var definition = JsonConvert.DeserializeObject<MaaInterface>("""
                {"task":[
                    {"name":"start", "entry":"start", "__mfa_startup":true},
                    {"name":"game", "label":"结束后执行", "entry":"close-game", "__mfa_shutdown":true, "__mfa_end_action":"Pipeline"},
                    {"name":"daily", "entry":"daily", "group":["daily"]}
                ], "group":[{"name":"daily"}]}
                """)!;
            SetPresetInterface(definition);
            foreach (var closeGame in new[] { false, true })
            foreach (var closeEmulator in new[] { false, true })
            foreach (var closeApplication in new[] { false, true })
            {
                var start = Task(definition.Task![0].Clone());
                start.InterfaceItem!.LocalId = "start";
                var game = Task(new MaaInterface.MaaInterfaceTask
                {
                    Name = "game", Entry = "close-game", LocalId = "game", Check = closeGame,
                    EndAction = EndTaskPolicy.Pipeline, Group = [EndTaskPolicy.GroupName], Remark = "my end task"
                });
                var emulator = Task(new MaaInterface.MaaInterfaceTask
                {
                    Name = "关闭模拟器", Entry = "__mfa_close_emulator", EndAction = EndTaskPolicy.CloseEmulator,
                    LocalId = "emulator", Check = closeEmulator, Group = [EndTaskPolicy.GroupName]
                });
                var exit = Task(new MaaInterface.MaaInterfaceTask
                {
                    Name = "关闭本程序", Entry = "__mfa_close_mfa", EndAction = EndTaskPolicy.CloseMFA,
                    LocalId = "exit", Check = closeApplication, Group = [EndTaskPolicy.GroupName]
                });
                var daily = Task(definition.Task[2].Clone());
                daily.InterfaceItem!.LocalId = "daily";
                var queue = Queue($"combined-end-{Guid.NewGuid():N}", daily, exit, start, emulator, game);
                queue.Processor.InstanceConfiguration.SetValue(ConfigurationKeys.TaskGroupLayout, new TaskGroupLayout
                {
                    TaskOrder = ["daily", "exit", "start", "emulator", "game"],
                    TaskGroups = new() { ["game"] = EndTaskPolicy.GroupName },
                    Groups = [new() { Name = EndTaskPolicy.GroupName, Label = "结束后执行" }]
                });
                LoadClientTasks(queue, definition, queue.GetTaskItemsInDisplayOrder());
                var items = queue.GetTaskItemsInDisplayOrder();
                var combined = items[1];
                var model = combined.InterfaceItem!;
                Check(items[0] == start && combined == game && items.Count == 3 && model.IsShutdownTask &&
                    model.CloseGame == closeGame && model.CloseEmulator == closeEmulator &&
                    model.CloseApplication == closeApplication && model.Remark == "my end task" &&
                    combined.IsChecked == (closeGame || closeEmulator || closeApplication),
                    $"end migration retains all choices: game={closeGame}, emulator={closeEmulator}, app={closeApplication}");
                Check(queue.TaskItemGroups.All(group => group.Name != EndTaskPolicy.GroupName) &&
                    !queue.MoveTaskToTop(combined) && !queue.MoveTaskToBottom(combined) &&
                    !queue.MoveTaskToGroup(combined, queue.TaskItemGroups.Last(), 0) &&
                    !queue.CanMoveTaskGroup(queue.TaskItemGroups.Single(group => group.Name == "daily"), 1),
                    "merged ending task has no group and stays fixed below startup");
                Check(queue.MoveTaskToTop(daily) && queue.GetTaskItemsInDisplayOrder().Take(2).SequenceEqual(new[] { start, game }),
                    "pinning ordinary work respects both fixed tasks");
                var expanded = ((IEnumerable<DragItemViewModel>)typeof(MaaProcessor)
                    .GetMethod("ExpandEndTasks", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [new List<DragItemViewModel> { combined }])!).ToList();
                var expected = new List<string>();
                if (closeGame) expected.Add(EndTaskPolicy.Pipeline);
                if (closeEmulator) expected.Add(EndTaskPolicy.CloseEmulator);
                if (closeApplication) expected.Add(EndTaskPolicy.CloseMFA);
                Check(expanded.Select(item => item.InterfaceItem!.EndAction).SequenceEqual(expected) &&
                    model.RequiresPipeline == closeGame && model.RequiresDevice == (closeGame || closeEmulator) &&
                    model.HasEndActions == (expected.Count > 0),
                    "the selected switches expand into the original ordered end actions and connection requirements");
                model.CloseGame = !closeGame;
                model.CloseEmulator = !closeEmulator;
                model.CloseApplication = !closeApplication;
                LoadClientTasks(queue, definition, queue.GetTaskItemsInDisplayOrder());
                queue.Processor.InstanceConfiguration.ReloadFromDisk();
                var saved = queue.Processor.InstanceConfiguration.GetValue(ConfigurationKeys.TaskItems,
                    new List<MaaInterface.MaaInterfaceTask>()).Single(task => task.IsShutdownTask);
                Check(saved.CloseGame == !closeGame && saved.CloseEmulator == !closeEmulator &&
                    saved.CloseApplication == !closeApplication,
                    "edited end switches survive refresh and disk reload without repeating migration");
            }
            foreach (var continueOnError in new[] { false, true })
            foreach (var ordinaryFailed in new[] { false, true })
            {
                var coordinator = new EndTaskCoordinator(() => throw new Exception("must not close the real app"));
                var run = coordinator.Begin("result-check", "device",
                    [new(0, "failed-close", () => System.Threading.Tasks.Task.FromResult(false))],
                    false, () => { }, continueOnError)!;
                coordinator.CompleteWork(run, true);
                Check(run.DeviceCompletion.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(),
                    "a failed ending action completes all cleanup attempts");
                var queue = Queue($"combined-end-result-{Guid.NewGuid():N}");
                var processor = queue.Processor;
                Set(processor, "_endTaskRun", run);
                Set(processor, "Status", ordinaryFailed ? MFATask.MFATaskStatus.FAILED : MFATask.MFATaskStatus.SUCCEEDED);
                // Suppress the command worker: only inspect the actual result mapping,
                // without stopping devices, launching Agents or closing the application.
                using var commands = new CancellationTokenSource();
                commands.Cancel();
                Set(processor, "_commandThreadCts", commands);
                ((System.Threading.Tasks.Task)typeof(MaaProcessor)
                    .GetMethod("FinishEndTaskRunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(processor, [run, false, CancellationToken.None])!).GetAwaiter().GetResult();
                var status = (MFATask.MFATaskStatus)typeof(MaaProcessor)
                    .GetField("Status", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(processor)!;
                Check(status == (ordinaryFailed || !continueOnError ? MFATask.MFATaskStatus.FAILED : MFATask.MFATaskStatus.SUCCEEDED),
                    $"final run status follows continue-on-error without hiding ordinary failure: continue={continueOnError}, ordinaryFailed={ordinaryFailed}");
            }
            var fresh = Queue($"combined-end-new-{Guid.NewGuid():N}");
            LoadClientTasks(fresh, definition);
            Check(fresh.GetTaskItemsInDisplayOrder()[1].InterfaceItem is
                { IsShutdownTask: true, CloseGame: false, CloseEmulator: false, CloseApplication: false },
                "new configurations default all three ending switches to off");
            var exitOnly = Task(new MaaInterface.MaaInterfaceTask
                { Name = "exit", Entry = "__mfa_close_mfa", EndAction = EndTaskPolicy.CloseMFA, Check = true });
            var exitQueue = Queue($"combined-end-exit-only-{Guid.NewGuid():N}", exitOnly);
            LoadClientTasks(exitQueue, definition, exitQueue.GetTaskItemsInDisplayOrder());
            Check(exitQueue.GetTaskItemsInDisplayOrder()[1].InterfaceItem is
                { IsShutdownTask: true, CloseGame: false, CloseEmulator: false, CloseApplication: true },
                "an old exit-only configuration migrates without enabling device close");
        }
        finally { SetPresetInterface(original); }
    }

    private static void CheckEndTaskGroups()
    {
        CheckCombinedShutdownTasks();
        ConfigurationManager.Current = new MFAConfiguration("end-groups", "end-groups", []);
        var savedInterface = MaaProcessor.Interface;
        SetPresetInterface(null);
        try
        {
            var ordinary = Task("ordinary", "daily", true);
            var game = Task("game", EndTaskPolicy.GroupName, false);
            game.InterfaceItem!.EndAction = EndTaskPolicy.Pipeline;
            var emulator = Task("emulator", EndTaskPolicy.GroupName, false);
            emulator.InterfaceItem!.EndAction = EndTaskPolicy.CloseEmulator;
            var exit = Task("exit", EndTaskPolicy.GroupName, false);
            exit.InterfaceItem!.EndAction = EndTaskPolicy.CloseMFA;
            var id = $"end-groups-{Guid.NewGuid():N}";
            var queue = Queue(id, ordinary, exit, emulator, game);
            Check(queue.TaskItemGroups[1].Name == EndTaskPolicy.GroupName &&
                queue.TaskItemGroups[1].Items.SequenceEqual(new[] { exit, emulator, game }),
                "end group follows task-list position and member order instead of being pinned");
            var group = queue.TaskItemGroups[1];
            Check(group.CanSelect && group.CanEdit, "end group supports ordinary group interactions");
            group.ToggleSelectionCommand.Execute(null);
            Check(game.IsChecked && emulator.IsChecked && exit.IsChecked && group.SelectionState == true,
                "group select includes game, emulator and program close");
            group.ToggleSelectionCommand.Execute(null);
            Check(!game.IsChecked && !emulator.IsChecked && !exit.IsChecked && group.SelectionState == false,
                "second group click clears all three end tasks");
            queue.SelectAllCommand.Execute(null);
            Check(game.IsChecked && emulator.IsChecked && exit.IsChecked,
                "page select includes all three end tasks");
            group.IsExpanded = false;
            Check(queue.MoveTaskGroup(group, 0), "end group can be dragged upward");
            Dispatcher.UIThread.RunJobs();
            group = queue.TaskItemGroups[0];
            Check(group.Name == EndTaskPolicy.GroupName && !group.IsExpanded &&
                group.Items.SequenceEqual(new[] { exit, emulator, game }),
                "moving end group preserves members and expansion");
            Check(queue.MoveTaskGroup(queue.TaskItemGroups.Single(entry => entry.Name == "daily"), 0),
                "ordinary group can be dragged before end group");
            Dispatcher.UIThread.RunJobs();
            group = queue.TaskItemGroups.Single(entry => entry.Name == EndTaskPolicy.GroupName);
            Check(queue.MoveTaskGroup(group, 0), "end group can return to the top");
            Dispatcher.UIThread.RunJobs();
            group = queue.TaskItemGroups[0];
            Check(queue.MoveTaskGroup(group, queue.TaskItemGroups.Count), "end group can be dragged downward");
            Dispatcher.UIThread.RunJobs();
            var saved = new InstanceConfiguration(id);
            var reopened = Queue(id, saved.GetValue(ConfigurationKeys.TaskItems,
                new List<MaaInterface.MaaInterfaceTask>()).Select(Task).ToArray());
            Check(reopened.GetTaskItemsInDisplayOrder().Select(item => item.InterfaceItem!.LocalId)
                .SequenceEqual(new[] { "ordinary", "exit", "emulator", "game" }) &&
                reopened.TaskItemGroups.Last().Name == EndTaskPolicy.GroupName,
                "end group position and member order survive disk reopen");

            group = queue.TaskItemGroups.Last();
            queue.RenameTaskGroup(group, "renamed");
            queue.RebuildTaskItemGroups();
            group = queue.TaskItemGroups.Single(entry => entry.Name == EndTaskPolicy.GroupName);
            Check(group.Label == "renamed", "end group rename survives rebuild");
            Check(queue.MoveTaskToTop(game), "end task can be moved to the top");
            Check(queue.GetTaskItemsInDisplayOrder().First() == game && game.InterfaceItem.IsEndTask,
                "moving end task retains execution metadata");
            Check(queue.MoveTaskToBottom(game), "end task can be moved to the bottom");
            group = queue.TaskItemGroups.Single(entry => entry.Name == EndTaskPolicy.GroupName);
            Check(queue.MoveTaskToGroup(game, group, 0), "end task can rejoin end group");
            Dispatcher.UIThread.RunJobs();
            group = queue.TaskItemGroups.Single(entry => entry.Name == EndTaskPolicy.GroupName);
            Check(queue.MoveTaskToGroup(ordinary, group, 0), "ordinary task can join end group without becoming an end action");
            Dispatcher.UIThread.RunJobs();
            Check(!ordinary.InterfaceItem!.IsEndTask, "group membership does not change execution metadata");
            group = queue.TaskItemGroups.Single(entry => entry.Name == EndTaskPolicy.GroupName);
            queue.DeleteTaskGroup(group);
            queue.Processor.InstanceConfiguration.ReloadFromDisk();
            queue.RebuildTaskItemGroups();
            Check(queue.TaskItemGroups.All(entry => entry.Name != EndTaskPolicy.GroupName) &&
                queue.TaskItemViewModels.Count == 4 &&
                queue.TaskItemViewModels.Count(item => item.InterfaceItem!.IsEndTask) == 3,
                "deleting end group survives reload and retains all tasks and end actions");

            var layout = new TaskGroupLayout
            {
                TaskOrder = ["ordinary", "exit", "emulator", "game"],
                TaskGroups = new() { ["game"] = "daily", ["emulator"] = "daily" },
                DeletedGroups = [EndTaskPolicy.GroupName],
                Groups = [new() { Name = EndTaskPolicy.GroupName, Label = "old label" }]
            };
            queue.Processor.InstanceConfiguration.SetValue(ConfigurationKeys.TaskGroupLayout, layout);
            queue.RebuildTaskItemGroups();
            Dispatcher.UIThread.RunJobs();
            Check(queue.TaskItemGroups.All(entry => entry.Name != EndTaskPolicy.GroupName) &&
                queue.TaskItemGroups.Where(entry => entry.Name == "daily").SelectMany(entry => entry.Items)
                    .SequenceEqual(new[] { ordinary, emulator, game }),
                "saved end-task membership and group deletion are respected");

            var copied = game.InterfaceItem.Clone();
            Check(copied.EndAction == EndTaskPolicy.Pipeline && copied.Check == game.InterfaceItem.Check,
                "cloning preserves end metadata and user selection");
            var json = JsonConvert.SerializeObject(copied);
            Check(json.Contains("__mfa_end_action") && !json.Contains("IsEndTask"),
                "configuration serializes only the explicit end-action field");

            var ungrouped = Task("ungrouped-end", string.Empty, true);
            ungrouped.InterfaceItem!.EndAction = EndTaskPolicy.Pipeline;
            var custom = Task("custom-end", "custom", false);
            custom.InterfaceItem!.EndAction = EndTaskPolicy.CloseMFA;
            var authored = Queue($"authored-end-{Guid.NewGuid():N}", ungrouped, custom);
            Check(authored.TaskItemGroups.All(entry => entry.Name != EndTaskPolicy.GroupName) &&
                authored.TaskItemGroups[0].IsUngrouped && authored.TaskItemGroups[1].Name == "custom",
                "end metadata does not override authored groups or force an end group");

            // Exercise the actual reload path: template metadata migrates existing
            // task rows without overwriting their saved selection or remark.
            var old = Task("legacy", "daily", false);
            old.InterfaceItem!.Name = "close-game";
            old.InterfaceItem.Remark = "my game";
            var template = new MaaInterface.MaaInterfaceTask
            {
                Name = "close-game", Entry = "stop-game", Check = true,
                EndAction = EndTaskPolicy.Pipeline, Group = [EndTaskPolicy.GroupName]
            };
            var loader = new TaskLoader(new MaaInterface(), queue);
            typeof(TaskLoader).GetMethod("UpdateExistingItem", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(loader, [old, template, false]);
            Check(old.InterfaceItem.IsEndTask && !old.IsChecked && old.InterfaceItem.Remark == "my game",
                "reload migrates existing game task without changing saved selection or remark");
            template.EndAction = null;
            typeof(TaskLoader).GetMethod("UpdateExistingItem", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(loader, [old, template, false]);
            Check(!old.InterfaceItem.IsEndTask, "removing end metadata on resource update restores ordinary execution");

            var legacyExit = Task("legacy-exit", EndTaskPolicy.GroupName, true);
            legacyExit.InterfaceItem!.Name = "关闭助手";
            legacyExit.InterfaceItem.Entry = "__mfa_close_mfa";
            legacyExit.InterfaceItem.Remark = "my exit task";
            legacyExit.InterfaceItem.ExcludeFromSelectAll = true;
            var programExit = new MaaInterface.MaaInterfaceTask
            {
                Name = "关闭本程序", Entry = "__mfa_close_mfa", Check = false,
                EndAction = EndTaskPolicy.CloseMFA, Group = [EndTaskPolicy.GroupName]
            };
            typeof(TaskLoader).GetMethod("UpdateExistingItem", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(loader, [legacyExit, programExit, true]);
            Check(legacyExit.InterfaceItem.Name == "关闭本程序" && legacyExit.IsChecked &&
                legacyExit.InterfaceItem.Remark == "my exit task" &&
                legacyExit.InterfaceItem.ExcludeFromSelectAll != true,
                "resource reload renames legacy program close and clears select-all exclusion while preserving user state");
        }
        finally
        {
            SetPresetInterface(savedInterface);
        }
    }
}
