using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Pages;
using MFAAvalonia.ViewModels.UsersControls.Settings;

internal static class StartupOptionLayoutChecks
{
    public static IEnumerable<(string Name, Action Run)> Create()
    {
        foreach (var commonOnly in new[] { false, true })
            yield return ($"combined startup switches persist independently ({commonOnly})", () =>
            {
                Instances.RootViewModel.Idle = true;
                var queue = (TaskQueueViewModel)RuntimeHelpers.GetUninitializedObject(typeof(TaskQueueViewModel));
                var task = (DragItemViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DragItemViewModel));
                var model = new MaaInterface.MaaInterfaceTask { Name = "启动闪耀暖暖", IsStartupTask = true };
                Set(task, "_interfaceItem", model);
                var panel = new StackPanel();
                var saves = 0;
                var generator = new TaskOptionGenerator(queue, () => saves++);
                if (commonOnly) generator.GenerateCommonPanelContent(panel, task);
                else generator.GeneratePanelContent(panel, task);
                var switches = panel.Children.OfType<Grid>().SelectMany(row => row.Children.OfType<ToggleSwitch>()).ToArray();
                Check(switches.Length == 2 && switches.All(control => control.IsChecked == false),
                    "new startup switches must both be off");
                switches[1].IsChecked = true;
                Check(model.StartGame && !model.StartEmulator && saves == 1,
                    "game switch must update its saved flag independently");
                switches[1].IsChecked = false;
                Check(!model.StartGame && saves == 2 && panel.Children.OfType<Border>().All(border => !border.IsVisible),
                    "disabled startup options must be hidden");
                Check(switches.All(control => control.Classes.Contains("Switch")),
                    "combined startup must reuse the existing switch style");
            });
        foreach (var commonOnly in new[] { false, true })
            yield return ($"ending task has three independent saved switches ({commonOnly})", () =>
            {
                Instances.RootViewModel.Idle = true;
                var queue = (TaskQueueViewModel)RuntimeHelpers.GetUninitializedObject(typeof(TaskQueueViewModel));
                var source = (DragItemViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DragItemViewModel));
                var model = new MaaInterface.MaaInterfaceTask { Name = "关闭闪耀暖暖", IsShutdownTask = true };
                Set(source, "_interfaceItem", model);
                var panel = new StackPanel();
                var saves = 0;
                var generator = new TaskOptionGenerator(queue, () => saves++);
                if (commonOnly) generator.GenerateCommonPanelContent(panel, source);
                else generator.GeneratePanelContent(panel, source);
                var switches = panel.Children.OfType<Grid>().SelectMany(row => row.Children.OfType<ToggleSwitch>()).ToArray();
                Check(switches.Length == 3 && switches.All(control => control.IsChecked == false),
                    "all three ending switches must default off");
                for (var selected = 0; selected < switches.Length; selected++)
                {
                    switches[selected].IsChecked = true;
                    Check(model.CloseGame == (selected == 0) && model.CloseEmulator == (selected == 1) &&
                        model.CloseApplication == (selected == 2), "ending switches must be independent");
                    switches[selected].IsChecked = false;
                }
                Check(saves == 6 && switches.All(control => control.Classes.Contains("Switch")),
                    "ending switches must persist each edit and reuse the existing style");
            });
        foreach (var width in new[] { 260, 360, 720 })
        foreach (var commonOnly in new[] { false, true })
        {
            yield return ($"startup options share task styles and fit {width}px ({(commonOnly ? "common" : "combined")})", () =>
            {
                var cache = (System.Collections.Concurrent.ConcurrentDictionary<Type, Lazy<object>>)typeof(Instances)
                    .GetField("ServiceCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                var type = typeof(StartSettingsUserControlModel);
                cache.TryGetValue(type, out var original);
                var settings = (StartSettingsUserControlModel)RuntimeHelpers.GetUninitializedObject(typeof(StartSettingsUserControlModel));
                Set(settings, "_softwarePath", "C:/Emulator/emulator.exe");
                Set(settings, "_emulatorConfig", "--instance 1");
                Set(settings, "_waitSoftwareTime", 60.0);
                cache[type] = new Lazy<object>(() => settings);
                try
                {
                    Instances.RootViewModel.Idle = true;
                    var queue = (TaskQueueViewModel)RuntimeHelpers.GetUninitializedObject(typeof(TaskQueueViewModel));
                    Set(queue, "_currentController", MaaControllerTypes.Adb);
                    var task = (DragItemViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DragItemViewModel));
                    Set(task, "_interfaceItem", new MaaInterface.MaaInterfaceTask
                    {
                        Name = "startup", Entry = MaaInterface.MaaInterfaceTask.StartEmulatorEntry
                    });
                    var panel = new StackPanel();
                    var generator = new TaskOptionGenerator(queue, () => throw new Exception("Editing startup options must use the shared settings"));
                    if (commonOnly) generator.GenerateCommonPanelContent(panel, task);
                    else generator.GeneratePanelContent(panel, task);
                    Dispatcher.UIThread.RunJobs();
                    var rows = panel.Children.OfType<Grid>().ToArray();
                    // Supply the screenshot's translated labels without starting the language service.
                    var labels = new[] { "游戏路径", "附加命令", "启动后连接尝试时长（秒）" };
                    for (var i = 0; i < rows.Length; i++)
                        ((Grid)rows[i].Children[0]).Children.OfType<TextBlock>().Single().Text = labels[i];
                    panel.Measure(new Size(width, double.PositiveInfinity));
                    panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));

                    Check(rows.Length == 3 && rows.All(row => row.GetType().Name == "OptionRowGrid"),
                        "startup fields did not reuse ordinary task rows");
                    Check(((TextBox)rows[0].Children[1]).Text == settings.SoftwarePath &&
                        ((TextBox)rows[1].Children[1]).Text == settings.EmulatorConfig &&
                        ((NumericUpDown)rows[2].Children[1]).Value == 60,
                        "fields are not bound to the shared startup settings");
                    Check(panel.Children.OfType<Button>().Single().Command == settings.QuickSettingsCommand,
                        "multi-instance quick settings were lost");
                    foreach (var row in rows)
                    {
                        var label = (Grid)row.Children[0];
                        var text = label.Children.OfType<TextBlock>().Single();
                        Check(text.FontSize == 14 && text.FontWeight == FontWeight.Normal && text.TextWrapping == TextWrapping.Wrap,
                            "startup labels differ from ordinary task styles");
                        Check(label.Bounds.Right <= row.Bounds.Width + 0.1 &&
                            row.Children[1].Bounds.Right <= row.Bounds.Width + 0.1,
                            "a label or input extends beyond the row");
                    }
                    if (width <= 360)
                    {
                        var waitRow = rows[2];
                        Check(Grid.GetRow(waitRow.Children[1]) == 1 &&
                            waitRow.Children[1].Bounds.Top >= waitRow.Children[0].Bounds.Bottom,
                            $"the long wait-time label was not placed above its input: rows={waitRow.RowDefinitions.Count}, label={waitRow.Children[0].Bounds}, input={waitRow.Children[1].Bounds}");
                    }
                }
                finally
                {
                    if (original == null) cache.TryRemove(type, out _);
                    else cache[type] = original;
                }
            });
        }
    }

    private static void Set(object target, string field, object value) => target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
