using System.Collections.ObjectModel;
using System.Reflection;
using Avalonia.Threading;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper.Converters;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Pages;
using Newtonsoft.Json;

internal static partial class Program
{
    // Keep these offline checks independent of the app's global UI refresh and DI services.
    private static void SetPresetInterface(MaaInterface? value) => typeof(MaaProcessor)
        .GetField("<Interface>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, value);

    private static void CheckPresetGroups(string? interfacePath)
    {
        static IEnumerable<DragItemViewModel> ResourceItems(TaskQueueViewModel queue) =>
            queue.GetTaskItemsInDisplayOrder().Where(task => task.InterfaceItem?.IsClientTask != true);
        static IEnumerable<string> ResourceGroups(TaskQueueViewModel queue) => queue.TaskItemGroups
            .Where(group => !group.IsUngrouped && group.Name is not "__mfa_start_tasks" and not "__mfa_end_tasks")
            .Select(group => group.Name);
        ConfigurationManager.Current = new MFAConfiguration("preset-groups", "preset-groups", []);
        var original = MaaProcessor.Interface;
        try
        {
            var definition = JsonConvert.DeserializeObject<MaaInterface>("""
                {
                    "group": [{"name":"daily"}, {"name":"shop"}, {"name":"unused"}],
                    "task": [
                        {"name":"first", "entry":"first", "group":["daily"]},
                        {"name":"second", "entry":"second", "group":["daily"]},
                        {"name":"shop", "entry":"shop", "group":["shop"]}
                    ],
                    "preset": [
                        {"name":"full", "group":[{"name":"home", "label":"Home"}], "task":[
                            {"name":"first", "enabled":true, "group":["home"]},
                            {"name":"second", "enabled":false, "group":["home"]},
                            {"name":"shop", "enabled":true, "group":[]}
                        ]},
                        {"name":"quick", "group":[], "task":[{"name":"shop", "enabled":true, "group":[]}]},
                        {"name":"legacy", "task":[{"name":"first"}, {"name":"second"}]}
                    ]
                }
                """)!;
            SetPresetInterface(definition);
            var special = Task("special", "custom", true);
            special.InterfaceItem!.Entry = "CountdownAction";
            var id = $"preset-groups-{Guid.NewGuid():N}";
            var queue = Queue(id, special);
            Set(queue.Processor, "<TasksSource>k__BackingField", new ObservableCollection<DragItemViewModel>(
                definition.Task!.Select(task => new DragItemViewModel(task) { OwnerViewModel = queue })));
            queue.Processor.InstanceConfiguration.SetValue(ConfigurationKeys.TaskGroupLayout, new TaskGroupLayout
            {
                Groups = [new() { Name = "obsolete", Label = "Old group" }, new() { Name = "custom", Label = "Custom" }],
                TaskGroups = new() { ["special"] = "custom" },
                DeletedGroups = ["home"],
            });

            queue.ApplyPresetCommand.Execute(definition.Preset![0]);
            Dispatcher.UIThread.RunJobs();
            Check(ResourceItems(queue).Select(task => task.InterfaceItem!.Name)
                .SequenceEqual(new[] { "first", "second", "shop", "repeatable" }),
                "preset overrides retain authored execution order and user-added special tasks");
            Check(ResourceGroups(queue)
                .SequenceEqual(new[] { "home", "custom" }),
                "preset declares its own groups without inherited empty or obsolete groups");
            Check(queue.TaskItemGroups.Single(group => group.Name == "home").Label == "Home" &&
                queue.TaskItemGroups.Single(group => group.Items.Any(task => task.InterfaceItem!.Name == "shop")).IsUngrouped,
                "preset group labels and explicit ungrouped task overrides are applied");
            Check(ResourceItems(queue).First().IsChecked && !ResourceItems(queue).Skip(1).First().IsChecked &&
                definition.Task![0].Group!.SequenceEqual(new[] { "daily" }),
                "preset selection is applied without mutating task templates");

            var saved = new InstanceConfiguration(id);
            var reopened = Queue(id, saved.GetValue(ConfigurationKeys.TaskItems, new List<MaaInterface.MaaInterfaceTask>())
                .Select(Task).ToArray());
            Check(ResourceGroups(reopened)
                .SequenceEqual(new[] { "home", "custom" }) &&
                ResourceItems(reopened).Skip(2).First().InterfaceItem!.Group!.Count == 0,
                "preset-local groups and independent tasks survive disk reopen");
            queue.ApplyPresetCommand.Execute(definition.Preset[1]);
            Dispatcher.UIThread.RunJobs();
            Check(ResourceGroups(queue)
                .SequenceEqual(new[] { "custom" }) && ResourceItems(queue).Count() == 2,
                "switching to an empty group list removes prior preset groups and keeps special tasks");
            queue.ApplyPresetCommand.Execute(definition.Preset[2]);
            Dispatcher.UIThread.RunJobs();
            Check(queue.TaskItemGroups.Any(group => group.Name == "daily") &&
                ResourceItems(queue).Take(2).All(task => task.InterfaceItem!.Group!.SequenceEqual(new[] { "daily" })),
                "legacy presets continue to inherit top-level task groups");

            if (string.IsNullOrWhiteSpace(interfacePath)) return;
            definition = JsonConvert.DeserializeObject<MaaInterface>(File.ReadAllText(interfacePath),
                new MaaInterfaceSelectAdvancedConverter(false), new MaaInterfaceSelectOptionConverter(false))!;
            SetPresetInterface(definition);
            var projectQueue = Queue($"project-presets-{Guid.NewGuid():N}");
            Set(projectQueue, "_currentController", MaaControllerTypes.Adb);
            Set(projectQueue.Processor, "<TasksSource>k__BackingField", new ObservableCollection<DragItemViewModel>(
                definition.Task!.Select(task => new DragItemViewModel(task) { OwnerViewModel = projectQueue })));
            foreach (var preset in definition.Preset!)
            {
                projectQueue.ApplyPresetCommand.Execute(preset);
                Dispatcher.UIThread.RunJobs();
                Check(ResourceItems(projectQueue).Select(task => task.InterfaceItem!.Name)
                    .SequenceEqual(preset.Task!.Select(task => task.Name)), $"{preset.Name}: exact task order and membership");
                Check(projectQueue.TaskItemGroups.Where(group => !group.IsUngrouped).Select(group => group.Name)
                    .SequenceEqual(preset.Group!.Select(group => group.Name)), $"{preset.Name}: exact preset group list");
                Check(projectQueue.TaskItemGroups.Where(group => !group.IsUngrouped).All(group => group.Items.Count > 1),
                    $"{preset.Name}: singleton tasks and omitted groups have no group header");
                var startTasks = projectQueue.TaskItemGroups.First().Items;
                Check(projectQueue.TaskItemGroups.First().IsUngrouped && startTasks.Count == 1 &&
                    startTasks[0].InterfaceItem is { IsStartupTask: true, StartEmulator: false, StartGame: false } &&
                    startTasks[0].IsChecked,
                    $"{preset.Name}: one ungrouped startup task is first with both switches off");
                var ending = projectQueue.TaskItemGroups[1];
                Check(ending.IsUngrouped && ending.Items.Single().InterfaceItem is
                    { IsShutdownTask: true, CloseGame: false, CloseEmulator: false, CloseApplication: false } &&
                    projectQueue.TaskItemViewModels.All(task => task.InterfaceItem?.IsClientTask != true),
                    $"{preset.Name}: one ungrouped ending task follows startup with all switches off");
                projectQueue.SelectNoneCommand.Execute(null);
                projectQueue.SelectAllCommand.Execute(null);
                Check(ending.Items.Single().IsChecked && !ending.Items.Single().InterfaceItem!.HasEndActions,
                    $"{preset.Name}: page select includes the task without enabling its ending switches");
                var reloaded = Queue(projectQueue.Processor.InstanceId,
                    new InstanceConfiguration(projectQueue.Processor.InstanceId)
                        .GetValue(ConfigurationKeys.TaskItems, new List<MaaInterface.MaaInterfaceTask>()).Select(Task).ToArray());
                Check(ResourceItems(reloaded).Select(task => task.InterfaceItem!.Name)
                    .SequenceEqual(preset.Task!.Select(task => task.Name)) &&
                    reloaded.TaskItemGroups.Where(group => !group.IsUngrouped).Select(group => group.Name)
                        .SequenceEqual(preset.Group!.Select(group => group.Name)), $"{preset.Name}: layout survives disk reopen");
            }
        }
        finally
        {
            SetPresetInterface(original);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
