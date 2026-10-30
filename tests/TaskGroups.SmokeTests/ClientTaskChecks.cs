using System.Collections.ObjectModel;
using Avalonia.Threading;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Pages;
using Newtonsoft.Json;

internal static partial class Program
{
    private static void LoadClientTasks(TaskQueueViewModel queue, MaaInterface definition,
        IList<DragItemViewModel>? current = null)
    {
        Set(queue, "_currentResources", new ObservableCollection<MaaInterface.MaaInterfaceResource>());
        Set(queue, "_currentController", MaaControllerTypes.Adb);
        var source = new ObservableCollection<DragItemViewModel>();
        Set(queue.Processor, "<TasksSource>k__BackingField", source);
        var first = false;
        new TaskLoader(definition, queue).LoadTasks(definition.Task ?? [], source, ref first, current);
        Dispatcher.UIThread.RunJobs();
    }

    private static void CheckClientTasks()
    {
        ConfigurationManager.Current = new MFAConfiguration("client-tasks", "client-tasks", []);
        var original = MaaProcessor.Interface;
        try
        {
            var definition = JsonConvert.DeserializeObject<MaaInterface>("""
                {
                    "controller": [
                        {"name":"android", "type":"Adb", "task":["daily"]},
                        {"name":"desktop", "type":"Win32", "task":["daily"]}
                    ],
                    "resource": [{"name":"game", "path":["resource"], "task":["daily"]}],
                    "group": [{"name":"启动前执行"}, {"name":"__mfa_end_tasks", "label":"结束后执行"}, {"name":"daily"}],
                    "task": [
                        {"name":"daily", "entry":"daily", "group":["daily"]},
                        {"name":"game", "entry":"game", "group":["启动前执行"]},
                        {"name":"stop", "entry":"stop", "__mfa_end_action":"Pipeline", "group":["__mfa_end_tasks"]}
                    ]
                }
                """)!;
            SetPresetInterface(definition);
            var queue = Queue($"client-fresh-{Guid.NewGuid():N}");
            LoadClientTasks(queue, definition);
            Check(queue.TaskItemViewModels.Count(task => task.InterfaceItem!.IsClientTask) == 3 &&
                queue.TaskItemViewModels.Where(task => task.InterfaceItem!.IsClientTask).All(task => !task.IsChecked),
                "client creates all three unchecked tasks without resource declarations");
            Check(queue.TaskItemGroups.Select(group => group.Name)
                .SequenceEqual(new[] { "启动前执行", "__mfa_end_tasks", "daily" }) &&
                queue.TaskItemGroups[0].Items.First().InterfaceItem!.IsStartTask &&
                queue.TaskItemGroups[1].Items.Select(task => task.InterfaceItem!.EndAction)
                    .SequenceEqual(new[] { "Pipeline", "CloseEmulator", "CloseMFA" }),
                "default startup and end groups reuse resource groups in the first two positions");
            Check(definition.Task!.Count == 3 && definition.Task.All(task => !task.IsClientTask) &&
                definition.Task[0].Name == "daily" && queue.TaskItemViewModels.First().IsVisible,
                "client task injection leaves resource definitions untouched and exposes startup settings");
            var clientTasks = queue.TaskItemViewModels.Where(task => task.InterfaceItem!.IsClientTask).ToList();
            Check(clientTasks.All(task => task.SupportsResource("game") && task.SupportsController("android")) &&
                clientTasks.Where(task => task.InterfaceItem!.Entry != "__mfa_close_mfa")
                    .All(task => !task.SupportsController("desktop")) &&
                clientTasks.Single(task => task.InterfaceItem!.Entry == "__mfa_close_mfa").SupportsController("desktop"),
                "resource allowlists do not hide client tasks and only ADB supports emulator operations");

            var exit = clientTasks.Single(task => task.InterfaceItem!.Entry == "__mfa_close_mfa");
            exit.IsChecked = true;
            var other = Queue($"client-other-{Guid.NewGuid():N}");
            LoadClientTasks(other, definition);
            Check(other.TaskItemViewModels.Where(task => task.InterfaceItem!.IsClientTask).All(task => !task.IsChecked) &&
                other.Processor.TasksSource.Where(task => task.InterfaceItem!.IsClientTask).All(task => !task.IsChecked),
                "client tasks and their unchecked templates are independent across configurations");

            queue.TaskItemViewModels.Remove(exit);
            LoadClientTasks(queue, definition, queue.GetTaskItemsInDisplayOrder());
            Check(queue.TaskItemViewModels.All(task => task.InterfaceItem!.Entry != "__mfa_close_mfa") &&
                queue.Processor.TasksSource.Any(task => task.InterfaceItem!.Entry == "__mfa_close_mfa"),
                "refresh respects task deletion while retaining its Add Task template");
            var reopened = Queue(queue.Processor.InstanceId);
            LoadClientTasks(reopened, definition);
            Check(reopened.TaskItemViewModels.All(task => task.InterfaceItem!.Entry != "__mfa_close_mfa"),
                "deleted client tasks remain absent after reopening their configuration");

            var legacyExit = Task(new MaaInterface.MaaInterfaceTask
            {
                Name = "关闭程序", Entry = "__mfa_close_mfa", LocalId = "legacy-exit", Check = true,
                EndAction = "CloseMFA", Group = ["custom"], Remark = "my exit", DisplayNameOverride = "renamed exit"
            });
            var daily = Task(new MaaInterface.MaaInterfaceTask
                { Name = "daily", Entry = "daily", LocalId = "legacy-daily", Check = true, Group = ["daily"] });
            var migrated = Queue($"client-legacy-{Guid.NewGuid():N}", daily, legacyExit);
            migrated.Processor.InstanceConfiguration.SetValue(ConfigurationKeys.CurrentTasks,
                new List<string> { "关闭程序" + TaskLoader.NEW_SEPARATOR + "__mfa_close_mfa",
                    "启动模拟器" + TaskLoader.NEW_SEPARATOR + "__mfa_start_emulator" });
            migrated.Processor.InstanceConfiguration.SetValue(ConfigurationKeys.TaskGroupLayout, new TaskGroupLayout
                { TaskOrder = ["legacy-daily", "legacy-exit"], Groups = [new() { Name = "custom", Label = "My group" }] });
            LoadClientTasks(migrated, definition, migrated.GetTaskItemsInDisplayOrder());
            Check(migrated.TaskItemViewModels.Contains(legacyExit) && legacyExit.IsChecked &&
                legacyExit.InterfaceItem!.Name == "__mfa_close_mfa" && legacyExit.InterfaceItem.Remark == "my exit" &&
                legacyExit.InterfaceItem.DisplayNameOverride == "renamed exit" && legacyExit.InterfaceItem.Group!.Single() == "custom" &&
                migrated.TaskItemViewModels.IndexOf(daily) < migrated.TaskItemViewModels.IndexOf(legacyExit),
                "migration matches legacy entries and preserves identity, selection, names and relative position");
            Check(migrated.TaskItemViewModels.All(task => !task.InterfaceItem!.IsStartTask),
                "migration retains deletion history recorded with legacy display names");

            var emptyDefinition = new MaaInterface { Task = [], Controller = definition.Controller };
            SetPresetInterface(emptyDefinition);
            var empty = Queue($"client-empty-{Guid.NewGuid():N}");
            LoadClientTasks(empty, emptyDefinition);
            Check(empty.TaskItemGroups.Select(group => group.Name)
                .SequenceEqual(new[] { "__mfa_start_tasks", "__mfa_end_tasks" }),
                "client creates default groups when resources declare none");
            empty.TaskItemViewModels.Clear();
            empty.Processor.InstanceConfiguration.SetValue(ConfigurationKeys.TaskItems, new List<MaaInterface.MaaInterfaceTask>());
            LoadClientTasks(empty, emptyDefinition);
            Check(empty.TaskItemViewModels.Count == 0, "deleting every client task does not trigger a template fallback");
            empty.ApplyPresetCommand.Execute(new MaaInterface.MaaInterfacePreset { Name = "empty", Task = [] });
            Dispatcher.UIThread.RunJobs();
            Check(empty.TaskItemViewModels.Count == 3 && empty.TaskItemViewModels.All(task => !task.IsChecked),
                "applying a preset restores deleted client tasks unchecked even without resource tasks");

            SetPresetInterface(definition);
            var preset = new MaaInterface.MaaInterfacePreset { Name = "daily", Task = [new() { Name = "daily", Enabled = true }] };
            other.ApplyPresetCommand.Execute(preset);
            Dispatcher.UIThread.RunJobs();
            other.TaskItemViewModels.Single(task => task.InterfaceItem!.Entry == "__mfa_close_mfa").IsChecked = true;
            other.ApplyPresetCommand.Execute(preset);
            Dispatcher.UIThread.RunJobs();
            Check(other.TaskItemViewModels.Count(task => task.InterfaceItem!.IsClientTask) == 3 &&
                other.TaskItemViewModels.Where(task => task.InterfaceItem!.IsClientTask).All(task => !task.IsChecked) &&
                queue.TaskItemViewModels.All(task => task.InterfaceItem!.Entry != "__mfa_close_mfa"),
                "reapplying a preset resets current client tasks without changing another configuration");
            LoadClientTasks(other, definition, other.GetTaskItemsInDisplayOrder());
            Check(other.TaskItemViewModels.Count == 4 && other.TaskItemViewModels.Count(task => task.InterfaceItem!.IsClientTask) == 3,
                "preset reload retains client tasks even though the resource preset does not list them");

            var groupedOnlyByTasks = new MaaInterface
            {
                Controller = definition.Controller,
                Task = [new() { Name = "game", Entry = "game", Group = ["启动前执行"] },
                    new() { Name = "stop", Entry = "stop", Group = ["结束后执行"], EndAction = "Pipeline" }]
            };
            SetPresetInterface(groupedOnlyByTasks);
            var inferred = Queue($"client-inferred-{Guid.NewGuid():N}");
            LoadClientTasks(inferred, groupedOnlyByTasks);
            Check(inferred.TaskItemGroups.Select(group => group.Name)
                .SequenceEqual(new[] { "启动前执行", "结束后执行" }),
                "task-only group declarations are reused without duplicate client groups");
            var localGroups = new MaaInterface.MaaInterfacePreset
            {
                Name = "local-groups",
                Group = [new() { Name = "before", Label = "启动前执行" }, new() { Name = "after", Label = "结束后执行" }],
                Task = [new() { Name = "game", Group = ["before"] }, new() { Name = "stop", Group = ["after"] },
                    new() { Name = "__mfa_close_mfa", Enabled = true }]
            };
            groupedOnlyByTasks.Preset = [localGroups];
            inferred.ApplyPresetCommand.Execute(localGroups);
            Dispatcher.UIThread.RunJobs();
            Check(inferred.TaskItemGroups.Select(group => group.Name).SequenceEqual(new[] { "before", "after" }) &&
                inferred.TaskItemViewModels.Where(task => task.InterfaceItem!.IsClientTask).All(task => !task.IsChecked),
                "preset-local group labels are reused and resource presets cannot enable client close actions");
            LoadClientTasks(inferred, groupedOnlyByTasks, inferred.GetTaskItemsInDisplayOrder());
            Check(inferred.TaskItemGroups.Select(group => group.Name).SequenceEqual(new[] { "before", "after" }),
                "preset-local client groups survive task reload");
        }
        finally { SetPresetInterface(original); }
    }
}
