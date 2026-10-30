using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Pages;
using MFAAvalonia.ViewModels.Windows;
using Newtonsoft.Json;
using Serilog;
using SukiUI.Dialogs;

internal static class OptionConfirmationChecks
{
    private const string Warning = "非活动场景请勿开启全屏点击，否则可能误触并消耗资源！";
    private static readonly RootViewModel Root = (RootViewModel)RuntimeHelpers.GetUninitializedObject(typeof(RootViewModel));
    private static readonly TestDialogManager Dialogs = new();

    public static IEnumerable<(string Name, Action Run)> Create()
    {
        // Resolve actual control dependencies without starting app services or connecting a device.
        AppBuilder.Configure<Application>()
            .UseStandardRuntimePlatformSubsystem()
            .UseWindowingSubsystem(() => { }, "Test")
            .UseSkia().SetupWithoutStarting();
        Root.Idle = true;
        typeof(MFAAvalonia.App).GetProperty(nameof(MFAAvalonia.App.Services))!.SetValue(null, new TestServices());
        typeof(LoggerHelper).GetField("_logger", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, new LoggerConfiguration().WriteTo.Console().CreateLogger());

        yield return ("confirmation metadata survives parsing and split-interface merges", () =>
        {
            var definition = JsonConvert.DeserializeObject<MaaInterface.MaaInterfaceOption>(
                """{"__mfa_confirm_on_enable":"初始提醒"}""")!;
            Check(definition.ConfirmOnEnable == "初始提醒", "confirmation text was not parsed");
            definition.Merge(new() { Label = "新名称" });
            Check(definition.ConfirmOnEnable == "初始提醒", "an absent extension erased the text");
            definition.Merge(new() { ConfirmOnEnable = Warning });
            Check(definition.ConfirmOnEnable == Warning, "a split interface did not override the text");
            definition.Merge(new() { ConfirmOnEnable = "" });
            Check(definition.ConfirmOnEnable == "", "an explicit empty override did not clear the text");
        });

        yield return ("enabling waits for confirmation, preserves No subtree and displays red large text", () =>
        {
            var state = NewSwitch();
            Check(Dialogs.Shows == 0 && state.Saves == 0 && !state.Toggle.IsChecked!.Value,
                "initialization opened a dialog or saved configuration");
            state.Toggle.IsChecked = true;
            Check(state.Option.Index == 1 && state.Toggle.IsChecked == false && state.Saves == 0,
                "the unconfirmed state was committed");
            Check(state.Panel.IsEnabled == false && state.Option.SubOptions!.Single().Name == "noChild",
                "the panel or subtree changed before confirmation");
            var content = (TextBlock)Dialogs.Active!.Content!;
            Check(content.Text == Warning && Equals(content.Foreground, Brushes.Red)
                  && content.FontSize >= 20 && content.FontWeight == FontWeight.Bold,
                "the warning content or emphasis was lost");
            Check(Dialogs.Active.CanDismissWithBackgroundClick, "background dismissal is not allowed");
            state.Toggle.IsChecked = true;
            Check(Dialogs.Shows == 1 && state.Saves == 0, "repeated input opened another confirmation");
            ClickButton(0);
            Check(state.Option.Index == 1 && state.Toggle.IsChecked == false && state.Saves == 0
                  && state.Panel.IsEnabled, "cancel did not restore the closed switch");
        });

        yield return ("confirmation saves exactly once, updates children, and prompts again after disabling", () =>
        {
            var state = NewSwitch();
            state.Toggle.IsChecked = true;
            ClickButton(1);
            Check(state.Option.Index == 0 && state.Toggle.IsChecked == true && state.Saves == 1,
                "confirmation did not save exactly once");
            Check(state.Option.SubOptions!.Any(o => o.Name == "yesChild") && state.Panel.IsEnabled,
                "the enabled subtree was not created");
            state.Toggle.IsChecked = false;
            Check(state.Option.Index == 1 && state.Saves == 2 && Dialogs.Active == null,
                "disabling did not save directly");
            state.Toggle.IsChecked = true;
            Check(Dialogs.Shows == 2 && state.Option.Index == 1 && state.Saves == 2,
                "re-enabling bypassed confirmation");
            Dialogs.DismissDialog();
        });

        yield return ("closing a dialog keeps No and allows a later attempt", () =>
        {
            var state = NewSwitch();
            state.Toggle.IsChecked = true;
            Dialogs.DismissDialog();
            Check(state.Option.Index == 1 && state.Toggle.IsChecked == false && state.Saves == 0
                  && state.Panel.IsEnabled, "dismissal changed the stored state or left the panel disabled");
            state.Toggle.IsChecked = true;
            Check(Dialogs.Shows == 2, "dismissal left the re-entry guard set");
            Dialogs.DismissDialog();
        });

        yield return ("another open dialog rejects enabling without saving", () =>
        {
            var state = NewSwitch();
            Check(Dialogs.TryShowDialog(new SukiUI.Controls.SukiDialog()), "test dialog did not open");
            var existing = Dialogs.Active;
            state.Toggle.IsChecked = true;
            Check(ReferenceEquals(existing, Dialogs.Active) && state.Option.Index == 1
                  && state.Toggle.IsChecked == false && state.Saves == 0 && state.Panel.IsEnabled,
                "a failed dialog show enabled the option or blocked the switch");
            Dialogs.DismissDialog();
            state.Toggle.IsChecked = true;
            Check(Dialogs.Active != null, "the switch could not retry after a failed show");
            Dialogs.DismissDialog();
        });

        yield return ("a dialog exception keeps the switch off and permits retry", () =>
        {
            var state = NewSwitch();
            Dialogs.ThrowOnShow = true;
            state.Toggle.IsChecked = true;
            Check(state.Option.Index == 1 && state.Toggle.IsChecked == false && state.Saves == 0
                  && state.Panel.IsEnabled, "a dialog exception changed the state or blocked the switch");
            Dialogs.ThrowOnShow = false;
            state.Toggle.IsChecked = true;
            Check(Dialogs.Active != null, "the switch could not retry after a dialog exception");
            Dialogs.DismissDialog();
        });

        foreach (var warning in new string?[] { null, "", "  " })
        {
            yield return ($"ordinary switch with {warning?.Length.ToString() ?? "absent"} warning chars works directly", () =>
            {
                var state = NewSwitch(warning);
                state.Toggle.IsChecked = true;
                Check(state.Option.Index == 0 && state.Saves == 1 && Dialogs.Shows == 0,
                    "an ordinary switch was gated");
                state.Toggle.IsChecked = false;
                Check(state.Option.Index == 1 && state.Saves == 2, "an ordinary switch did not turn off");
            });
        }

        yield return ("a saved Yes value loads without a prompt or configuration write", () =>
        {
            var state = NewSwitch(index: 0);
            Check(state.Toggle.IsChecked == true && state.Saves == 0 && Dialogs.Shows == 0,
                "loading an enabled switch prompted or rewrote the saved choice");
            state.Toggle.IsChecked = false;
            state.Toggle.IsChecked = true;
            Check(state.Option.Index == 1 && state.Saves == 1 && Dialogs.Active != null,
                "a saved Yes value skipped a later enable confirmation");
            Dialogs.DismissDialog();
        });

        yield return ("case order does not change the enable-confirmation behavior", () =>
        {
            var state = NewSwitch(index: 0, reverse: true);
            state.Toggle.IsChecked = true;
            Check(state.Option.Index == 0 && state.Toggle.IsChecked == false, "reversed cases enabled before confirmation");
            ClickButton(1);
            Check(state.Option.Index == 1 && state.Saves == 1, "reversed cases saved the wrong index");
        });

        yield return ("starting a task before confirmation prevents enabling", () =>
        {
            var state = NewSwitch();
            state.Toggle.IsChecked = true;
            typeof(TaskQueueViewModel).GetField("_isRunning", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(state.Queue, true);
            ClickButton(1);
            Check(state.Option.Index == 1 && state.Toggle.IsChecked == false && state.Saves == 0,
                "confirmation enabled the option while the task was running");
        });
    }

    private static SwitchState NewSwitch(string? warning = Warning, int index = 1, bool reverse = false)
    {
        Dialogs.DismissDialog();
        Dialogs.Shows = 0;
        Dialogs.ThrowOnShow = false;
        Root.Idle = true;
        var queue = (TaskQueueViewModel)RuntimeHelpers.GetUninitializedObject(typeof(TaskQueueViewModel));
        var state = new SwitchState { Queue = queue };
        var definition = new MaaInterface.MaaInterfaceOption
        {
            Type = "switch", ConfirmOnEnable = warning,
            Cases = [new() { Name = "Yes", Option = ["yesChild"] }, new() { Name = "No", Option = ["noChild"] }]
        };
        if (reverse) definition.Cases.Reverse();
        state.Option = new() { Name = "testSwitch", Index = index };
        var generator = new TaskOptionGenerator(queue, () => state.Saves++);
        state.Panel = (StackPanel)typeof(TaskOptionGenerator)
            .GetMethod("CreateToggleControl", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(generator, [state.Option, reverse ? 1 : 0, reverse ? 0 : 1, definition, null])!;
        state.Toggle = ((Grid)state.Panel.Children[0]).Children.OfType<ToggleSwitch>().Single();
        return state;
    }

    private static void ClickButton(int index)
    {
        ((Button)Dialogs.Active!.ActionButtons[index]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class SwitchState
    {
        public TaskQueueViewModel Queue = null!;
        public MaaInterface.MaaInterfaceSelectOption Option = null!;
        public StackPanel Panel = null!;
        public ToggleSwitch Toggle = null!;
        public int Saves;
    }

    private sealed class TestServices : IServiceProvider
    {
        public object GetService(Type type) => type == typeof(RootViewModel) ? Root
            : type == typeof(ISukiDialogManager) ? Dialogs
            : throw new InvalidOperationException($"Unexpected service: {type}");
    }

    private sealed class TestDialogManager : ISukiDialogManager
    {
        private readonly SukiDialogManager _inner = new();
        public ISukiDialog? Active { get; private set; }
        public int Shows;
        public bool ThrowOnShow;
        public event SukiDialogManagerEventHandler? OnDialogShown
        {
            add => _inner.OnDialogShown += value;
            remove => _inner.OnDialogShown -= value;
        }
        public event SukiDialogManagerEventHandler? OnDialogDismissed
        {
            add => _inner.OnDialogDismissed += value;
            remove => _inner.OnDialogDismissed -= value;
        }
        public TestDialogManager()
        {
            _inner.OnDialogShown += (_, e) => Active = e.Dialog;
            _inner.OnDialogDismissed += (_, _) => Active = null;
        }
        public bool TryShowDialog(ISukiDialog dialog)
        {
            Shows++;
            if (ThrowOnShow) throw new InvalidOperationException("Expected dialog show failure");
            return _inner.TryShowDialog(dialog);
        }
        public bool TryDismissDialog(ISukiDialog dialog) => _inner.TryDismissDialog(dialog);
        public void DismissDialog() => _inner.DismissDialog();
    }
}
