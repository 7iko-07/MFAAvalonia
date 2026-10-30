using System.Reflection;
using Avalonia.Threading;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper.ValueType;
using Newtonsoft.Json;

internal static partial class Program
{
    private static void CheckCombinedStartupTasks()
    {
        ConfigurationManager.Current = new MFAConfiguration("combined-startup", "combined-startup", []);
        var original = MaaProcessor.Interface;
        try
        {
            var definition = JsonConvert.DeserializeObject<MaaInterface>("""
                {"task":[
                    {"name":"game", "label":"启动前执行", "entry":"game", "__mfa_startup":true},
                    {"name":"daily", "entry":"daily", "group":["daily"]}
                ], "group":[{"name":"daily"}]}
                """)!;
            SetPresetInterface(definition);
            foreach (var startEmulator in new[] { false, true })
            foreach (var startGame in new[] { false, true })
            {
                var game = Task(new MaaInterface.MaaInterfaceTask
                {
                    Name = "game", Entry = "game", LocalId = "game", Check = startGame,
                    Group = ["启动前执行"], Remark = "my game",
                    Option = [new() { Name = "account", Index = 1,
                        SubOptions = [new() { Name = "phone", Data = new() { ["tail"] = "1234" } }] }]
                });
                var emulator = Task(new MaaInterface.MaaInterfaceTask
                {
                    Name = "启动模拟器", Entry = MaaInterface.MaaInterfaceTask.StartEmulatorEntry,
                    LocalId = "emulator", Check = startEmulator, Group = ["启动前执行"]
                });
                // Keep real option instances while exercising the existing option merge.
                definition.Task![0].Option = [new() { Name = "account" }];
                var savedOption = game.InterfaceItem!.Option![0];
                var daily = Task(definition.Task[1].Clone());
                daily.InterfaceItem!.LocalId = "daily";
                var queue = Queue($"combined-startup-{Guid.NewGuid():N}", daily, emulator, game);
                queue.Processor.InstanceConfiguration.SetValue(ConfigurationKeys.TaskGroupLayout, new TaskGroupLayout
                {
                    TaskOrder = [daily.InterfaceItem!.LocalId!, "emulator", "game"],
                    TaskGroups = new() { ["game"] = "启动前执行", ["emulator"] = "启动前执行" },
                    Groups = [new() { Name = "启动前执行", Label = "启动前执行" }]
                });
                LoadClientTasks(queue, definition, queue.GetTaskItemsInDisplayOrder());
                var combined = queue.GetTaskItemsInDisplayOrder().First();
                Check(ReferenceEquals(combined, game) && combined.InterfaceItem!.IsStartupTask &&
                    combined.InterfaceItem.StartEmulator == startEmulator && combined.InterfaceItem.StartGame == startGame &&
                    combined.IsChecked == (startEmulator || startGame),
                    $"migration preserves both old choices: emulator={startEmulator}, game={startGame}");
                Check(ReferenceEquals(combined.InterfaceItem!.Option![0], savedOption) &&
                    savedOption.SubOptions![0].Data!["tail"] == "1234" && combined.InterfaceItem.Remark == "my game" &&
                    queue.TaskItemGroups.All(group => group.Name != "启动前执行") &&
                    queue.TaskItemViewModels.All(item => item.InterfaceItem?.IsStartTask != true),
                    "migration retains account settings and removes the old task and group");
                Check(!queue.MoveTaskToBottom(combined) &&
                    !queue.MoveTaskToGroup(combined, queue.TaskItemGroups.Last(), 0) &&
                    !queue.MoveTaskToGroup(daily, queue.TaskItemGroups.First(), 0) &&
                    !queue.CanMoveTaskGroup(queue.TaskItemGroups.Single(group => group.Name == "daily"), 0),
                    "startup cannot be moved or displaced by another task or group");
                Check(queue.MoveTaskToTop(daily) && queue.GetTaskItemsInDisplayOrder().First() == combined,
                    "ordinary task pinning keeps startup first");

                var tasks = new ObservableQueue<MFATask>();
                Set(queue.Processor, "<TaskQueue>k__BackingField", tasks);
                typeof(MaaProcessor).GetMethod("InitializeConnectionTasksAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(queue.Processor, [CancellationToken.None, new List<DragItemViewModel> { combined }]);
                Check(tasks.Count == (startEmulator ? 4 : 3),
                    "the emulator switch controls the actual pre-connection queue");
                tasks.Clear();
                var roundTrip = JsonConvert.DeserializeObject<MaaInterface.MaaInterfaceTask>(combined.InterfaceItem.ToString())!;
                Check(roundTrip.StartEmulator == startEmulator && roundTrip.StartGame == startGame &&
                    roundTrip.IsStartupTask && roundTrip.ShouldRunPipeline == startGame && roundTrip.Entry == "game",
                    "switches and the original game entry survive serialization");

                combined.InterfaceItem.StartEmulator = !startEmulator;
                combined.InterfaceItem.StartGame = !startGame;
                LoadClientTasks(queue, definition, queue.GetTaskItemsInDisplayOrder());
                Check(combined.InterfaceItem.StartEmulator == !startEmulator && combined.InterfaceItem.StartGame == !startGame,
                    "refresh does not reapply the legacy checkbox migration");
            }
            var fresh = Queue($"combined-new-{Guid.NewGuid():N}");
            LoadClientTasks(fresh, definition);
            Check(fresh.TaskItemViewModels.First().InterfaceItem is
                { IsStartupTask: true, StartGame: false, StartEmulator: false },
                "new configurations default both startup switches to off");

            var onlyEmulator = Task(new MaaInterface.MaaInterfaceTask
                { Name = "emulator", Entry = MaaInterface.MaaInterfaceTask.StartEmulatorEntry, Check = true });
            var emulatorQueue = Queue($"combined-emulator-only-{Guid.NewGuid():N}", onlyEmulator);
            LoadClientTasks(emulatorQueue, definition, emulatorQueue.GetTaskItemsInDisplayOrder());
            Check(emulatorQueue.TaskItemViewModels.First().InterfaceItem is
                { IsStartupTask: true, StartEmulator: true, StartGame: false },
                "a configuration with only the old emulator task migrates without enabling game startup");
        }
        finally { SetPresetInterface(original); }
    }

    private static void CheckStartupTasks()
    {
        CheckCombinedStartupTasks();
        ConfigurationManager.Current = new MFAConfiguration("startup-tasks", "startup-tasks", []);
        var original = MaaProcessor.Interface;
        try
        {
            var definition = JsonConvert.DeserializeObject<MaaInterface>("""
                {
                    "group": [{"name":"startup"}, {"name":"daily"}],
                    "task": [
                        {"name":"emulator", "entry":"__mfa_start_emulator", "default_check":false, "group":["startup"]},
                        {"name":"game", "entry":"game", "group":["startup"]},
                        {"name":"daily", "entry":"daily", "group":["daily"]}
                    ]
                }
                """)!;
            SetPresetInterface(definition);
            var game = Task(new MaaInterface.MaaInterfaceTask
            {
                LocalId = "game", Name = "game", Entry = "game", Group = ["startup"], Check = true, Remark = "my game"
            });
            var daily = Task(new MaaInterface.MaaInterfaceTask
            {
                LocalId = "daily", Name = "daily", Entry = "daily", Group = ["daily"], Check = false
            });
            var queue = Queue($"startup-{Guid.NewGuid():N}", daily, game);
            var loader = new TaskLoader(definition, queue);
            var sync = typeof(TaskLoader).GetMethod("SynchronizeTaskItems", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var input = new object[] { new List<string>(), new List<DragItemViewModel> { daily, game }, definition.Task! };
            var (items, _) = ((List<DragItemViewModel>, List<DragItemViewModel>))sync.Invoke(loader, input)!;
            Check(items.Select(item => item.InterfaceItem!.Name).SequenceEqual(new[] { "daily", "emulator", "game" }) &&
                !items[1].IsChecked && game.IsChecked && game.InterfaceItem!.Remark == "my game",
                "new startup task joins the existing group without moving existing tasks");
            Check(game.InterfaceItem!.Group!.SequenceEqual(new[] { "startup" }),
                "existing game retains its startup group");
            foreach (var item in items) Set(item, "_name", item.InterfaceItem!.Name);
            Set(queue, "_taskItemViewModels", new System.Collections.ObjectModel.ObservableCollection<DragItemViewModel>(items));
            queue.RebuildTaskItemGroups();
            var startup = queue.TaskItemGroups.Single(group => group.Name == "startup");
            Check(startup.Name == "startup" && startup.Items.Count == 2,
                "startup group keeps the existing configuration's position");
            queue.MoveTaskGroup(startup, 0);
            Dispatcher.UIThread.RunJobs();
            var ordered = queue.GetTaskItemsInDisplayOrder();
            input = [input[0], ordered, definition.Task!];
            (items, _) = ((List<DragItemViewModel>, List<DragItemViewModel>))sync.Invoke(loader, input)!;
            Check(items.Select(item => item.InterfaceItem!.Name).SequenceEqual(new[] { "emulator", "game", "daily" }),
                "later reload retains the user's startup group position");

            var customGame = Task(new MaaInterface.MaaInterfaceTask
            {
                LocalId = "custom-game", Name = "game", Entry = "game", Group = ["custom"], Check = true
            });
            var customQueue = Queue($"startup-custom-{Guid.NewGuid():N}", customGame);
            customQueue.Processor.InstanceConfiguration.SetValue(ConfigurationKeys.TaskGroupLayout, new TaskGroupLayout
            {
                TaskGroups = new() { ["custom-game"] = "custom" },
                TaskOrder = ["custom-game"]
            });
            input = [new List<string>(), new List<DragItemViewModel> { customGame }, definition.Task!];
            sync.Invoke(new TaskLoader(definition, customQueue), input);
            Check(customGame.InterfaceItem!.Group!.SequenceEqual(new[] { "custom" }),
                "startup migration preserves the user's explicit game group");

            var processor = queue.Processor;
            var tasks = new ObservableQueue<MFATask>();
            Set(processor, "<TaskQueue>k__BackingField", tasks);
            var initialize = typeof(MaaProcessor).GetMethod("InitializeConnectionTasksAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            initialize.Invoke(processor, [CancellationToken.None, items]);
            var preparationCount = tasks.Count;
            tasks.Dequeue();
            Check(preparationCount == 4 && tasks.Dequeue().Name == items.Single(item => item.InterfaceItem!.IsStartTask).Name,
                "emulator preparation runs before device connection regardless of display order");
            tasks.Clear();
            initialize.Invoke(processor, [CancellationToken.None, new List<DragItemViewModel> { game }]);
            Check(tasks.Count == 3, "unselected emulator task adds no startup preparation");
            tasks.Clear();

            var launch = typeof(MaaProcessor).GetMethod("StartRunnableFile", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var path in new[] { "", Path.Combine(Path.GetTempPath(), $"missing-emulator-{Guid.NewGuid():N}.exe") })
            {
                var rejected = false;
                try
                {
                    ((System.Threading.Tasks.Task)launch.Invoke(processor, [path, 0.0, CancellationToken.None, true])!)
                        .GetAwaiter().GetResult();
                }
                catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "explicit startup rejects empty or missing game paths");
            }

            var continued = false;
            tasks.Enqueue(new MFATask { Action = () => throw new InvalidOperationException("startup failed") });
            tasks.Enqueue(new MFATask { Action = () => { continued = true; return System.Threading.Tasks.Task.CompletedTask; } });
            var execute = typeof(MaaProcessor).GetMethod("ExecuteTasks", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ((System.Threading.Tasks.Task)execute.Invoke(processor, [CancellationToken.None])!).GetAwaiter().GetResult();
            var status = (MFATask.MFATaskStatus)typeof(MaaProcessor)
                .GetField("Status", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(processor)!;
            Check(status == MFATask.MFATaskStatus.FAILED && !continued,
                "a failed startup preparation stops the queue before ordinary tasks");
            var copy = definition.Task![0].Clone();
            Check(copy.IsStartTask && !copy.IsEndTask && !copy.ToString().Contains("IsStartTask"),
                "startup task survives cloning and uses only its reserved entry in JSON");
        }
        finally { SetPresetInterface(original); }
    }
}
