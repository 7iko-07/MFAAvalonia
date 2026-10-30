using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using MFAAvalonia.Configuration;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Other;
using MFAAvalonia.ViewModels.Pages;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using MFAAvalonia.Views.UserControls;
using SukiUI.Controls;
using SukiUI.Toasts;

internal static class Program
{
    [ModuleInitializer]
    internal static void ResolveDependencies()
    {
        Environment.CurrentDirectory = AppContext.BaseDirectory;
    }

    private static void Set(object value, string field, object? data) =>
        value.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(value, data);

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception(description);
        Console.WriteLine("PASS " + description);
    }

    public static async Task Main()
    {
        AppBuilder.Configure<Application>()
            .UseStandardRuntimePlatformSubsystem()
            .UseWindowingSubsystem(() => { }, "Test")
            .UseSkia().SetupWithoutStarting();
        var now = DateTimeOffset.Parse("2026-10-22T10:00:00+08:00");
        var document = RedeemCodeDocument.Parse("""
            {"schema_version":1,"codes":[
              {"code":"旧码"},
              {"code":"刚开启","starts_at":"2026-10-22T10:00:00+08:00"},
              {"code":"未来","starts_at":"2026-10-22T10:00:01+08:00"},
              {"code":"过期","expires_at":"2026-10-22T10:00:00+08:00"},
              {"code":"旧码"}]}
            """);
        Check(document.Available(now).Select(c => c.Code).SequenceEqual(new[] { "旧码", "刚开启" }), "start/expiry boundary, missing dates and deduplication");
        Check(document.NextChange(now) == now.AddSeconds(1), "next local wakeup is the first future boundary");
        Check(new RedeemCodeDocument().NextChange(now) == null, "empty feed needs no timer");

        // Run the real feed and panel against in-memory data, without startup workers or devices.
        var feed = RedeemCodeFeed.Instance;
        var future = new RedeemCode { Code = "B", StartsAt = DateTimeOffset.UtcNow.AddHours(1) };
        Set(feed, "<Settings>k__BackingField", new RedeemCodeSettings { Url = "https://example.invalid/codes", Entry = "redeem" });
        Set(feed, "_initialization", Task.CompletedTask);
        Set(feed, "_seenKey", "RedeemCodes.SmokeTest");
        Set(feed, "_document", new RedeemCodeDocument { Codes = [new() { Code = "A" }, future] });
        feed.Recalculate();
        Check(feed.UnreadCount == 1 && feed.ShowNotice && feed.Available.Count == 1, "future codes are hidden and unread count includes available codes only");
        feed.Dismiss();
        feed.Recalculate();
        Check(!feed.ShowNotice && feed.UnreadCount == 0, "dismiss acknowledges this batch without removing redeemable codes");
        Check(GlobalConfiguration.GetValue("RedeemCodes.SmokeTest", "") == "[\"A\"]", "dismissal persists across restarts");
        feed.MarkSeen();
        Check(feed.UnreadCount == 0 && !feed.ShowNotice, "viewing details acknowledges the notification");
        Check(GlobalConfiguration.GetValue("RedeemCodes.SmokeTest", "") == "[\"A\"]", "seen codes persist independently from redemption results");
        future.StartsAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        feed.Recalculate();
        Check(feed.Available.Count == 2 && feed.UnreadCount == 1 && feed.ShowNotice, "newly opened code becomes visible and unread without fetching");

        var owner = (TaskQueueViewModel)RuntimeHelpers.GetUninitializedObject(typeof(TaskQueueViewModel));
        Set(owner, "_taskItemViewModels", new ObservableCollection<DragItemViewModel>());
        using var panel = new RedeemCodePanelViewModel(owner);
        await panel.InitializeAsync();
        var single = JObject.Parse(await panel.PrepareRunAsync("""
            {"redeem":{"action":{"param":{"custom_action_param":{"selected_code":"B"}}}},"other":{"enabled":false}}
            """));
        var arguments = single["redeem"]!["action"]!["param"]!["custom_action_param"]!;
        Check(arguments["codes"]!.Values<string>().SequenceEqual(new[] { "B" }), "single redemption passes only the selected currently available code");
        Check(single["other"]!["enabled"]!.Value<bool>() == false, "execution preserves unrelated pipeline parameters");
        var runId = arguments["run_id"]!.Value<string>();
        panel.HandleMessage(JObject.FromObject(new { run_id = "old-batch", kind = "result", code = "B", message = "wrong" }));
        Check(panel.Codes.Single(c => c.Code == "B").Result == "", "previous batch cannot overwrite this run");
        panel.FinishRun();
        panel.HandleMessage(JObject.FromObject(new { run_id = runId, kind = "result", code = "B", success = true, message = "兑换成功" }));
        Check(panel.Codes.Single(c => c.Code == "B").Succeeded, "late result after task completion still updates this run");
        var all = JObject.Parse(await panel.PrepareRunAsync(null));
        Check(all["redeem"]!["action"]!["param"]!["custom_action_param"]!["codes"]!.Count() == 2, "bulk redemption includes all available codes regardless of previous result");
        Check(panel.Codes.All(c => c.Result == "" && !c.Succeeded), "new run clears prior results");
        await CheckPipelineOverrides(panel);
        future.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        feed.Recalculate();
        Check(panel.Codes.Count == 1 && feed.UnreadCount == 0, "expiry removes the row and pending notification locally");
        CheckNotifications(feed, panel);
    }

    private static void CheckNotifications(RedeemCodeFeed feed, RedeemCodePanelViewModel panel)
    {
        Check(feed.NotificationsEnabled, "new installations enable notifications by default");
        var firstPanel = new RedeemCodePanel { DataContext = panel };
        var secondPanel = new RedeemCodePanel { DataContext = panel };
        var firstSwitch = firstPanel.GetLogicalDescendants().OfType<ToggleSwitch>().Single();
        var secondSwitch = secondPanel.GetLogicalDescendants().OfType<ToggleSwitch>().Single();
        Check(firstSwitch.IsChecked == true && secondSwitch.IsChecked == true, "redemption panels initially show the shared enabled switch");
        Set(feed, "_seen", new HashSet<string>(StringComparer.Ordinal));
        var a = new RedeemCode { Code = "notice-A" };
        var b = new RedeemCode { Code = "notice-B", StartsAt = DateTimeOffset.UtcNow.AddHours(1) };
        var document = new RedeemCodeDocument { Codes = [a, b] };
        Set(feed, "_document", document);
        feed.Recalculate();

        using var manager = new SukiToastManager();
        var queued = new List<ISukiToast>();
        manager.OnToastQueued += (_, e) => queued.Add(e.Toast);
        var opened = 0;
        using var notification = new RedeemCodeNotification(feed, manager, () => opened++);
        Check(queued.Count == 0, "notification waits until its host is ready");
        notification.ShowPending();
        var toast = queued.Single();
        Check(toast.CanDismissByClicking && !toast.CanDismissByTime, "new-code toast can be clicked away and has no timeout");
        Check(toast.ActionButtons.Count == 1, "notification contains only View and no extra close button");
        feed.Recalculate();
        b.StartsAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        feed.Recalculate();
        Check(queued.Count == 1 && feed.UnreadCount == 2 && !manager.IsDismissed(toast), "refresh and newly available codes update a single shared toast");
        manager.Dismiss(toast, SukiToastDismissSource.Click);
        Check(!feed.ShowNotice && feed.Available.Count == 2, "clicking the card acknowledges both codes without affecting redemption");
        Set(feed, "_seen", JsonConvert.DeserializeObject<HashSet<string>>(GlobalConfiguration.GetValue("RedeemCodes.SmokeTest"))!);
        feed.Recalculate();
        Check(queued.Count == 1 && !feed.ShowNotice, "reloading persisted state does not notify the dismissed batch again");

        document.Codes.Add(new RedeemCode { Code = "notice-C" });
        feed.Recalculate();
        var viewToast = queued.Last();
        var viewButton = (Button)viewToast.ActionButtons.Single();
        // Wire the real toast's lifecycle handlers, then click its actual action button.
        typeof(SukiToast).GetMethod("OnLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewToast, [new RoutedEventArgs(Control.LoadedEvent)]);
        viewButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(opened == 1 && !feed.ShowNotice && manager.IsDismissed(viewToast), "View opens the panel and acknowledges the notification");

        document.Codes.Add(new RedeemCode { Code = "notice-D" });
        feed.Recalculate();
        var beforeMute = queued.Last();
        firstSwitch.IsChecked = false;
        Check(secondSwitch.IsChecked == false && !feed.NotificationsEnabled, "turning off one panel's switch updates every panel");
        Check(!feed.ShowNotice && manager.IsDismissed(beforeMute), "turning notifications off immediately closes the current toast");
        Check(GlobalConfiguration.GetValue(RedeemCodeFeed.NotificationsEnabledKey) == bool.FalseString, "the shared switch is persisted globally");
        document.Codes.Add(new RedeemCode { Code = "muted-code" });
        feed.Recalculate();
        Check(!feed.ShowNotice && feed.UnreadCount == 0, "codes arriving while muted are silently acknowledged");
        var queuedBeforeEnable = queued.Count;
        // Simulate a scheduled code becoming available before the timer has run.
        document.Codes.Add(new RedeemCode { Code = "muted-boundary" });
        secondSwitch.IsChecked = true;
        Check(firstSwitch.IsChecked == true && feed.NotificationsEnabled, "turning on another panel's switch updates the shared setting");
        feed.Recalculate();
        Check(queued.Count == queuedBeforeEnable && !feed.ShowNotice, "re-enabling does not replay muted codes, including a pending time boundary");
        document.Codes.Add(new RedeemCode { Code = "after-enable" });
        feed.Recalculate();
        Check(queued.Count == queuedBeforeEnable + 1 && feed.UnreadCount == 1, "a later new code notifies normally after re-enabling");
        feed.MarkSeen();
        feed.Recalculate();
        Check(!feed.ShowNotice && manager.IsDismissed(queued.Last()), "viewing the list closes the shared notification without a repeat");
    }

    private static JToken RunArguments(JToken layer) =>
        layer["redeem"]!["action"]!["param"]!["custom_action_param"]!;

    private static async Task CheckPipelineOverrides(RedeemCodePanelViewModel panel)
    {
        foreach (var input in new[] { "[]", "[{}]" })
        {
            var original = JArray.Parse(input);
            var prepared = JArray.Parse(await panel.PrepareRunAsync(input));
            Check(prepared.Count == original.Count + 1
                && JToken.DeepEquals(new JArray(prepared.Take(original.Count)), original),
                $"{input} retains its layers and appends one run override");
            Check(RunArguments(prepared.Last!)["codes"]!.Values<string>().SequenceEqual(new[] { "A", "B" }),
                $"{input} submits all available codes");
            Check(!string.IsNullOrWhiteSpace(RunArguments(prepared.Last!)["run_id"]!.Value<string>()),
                $"{input} supplies a run id");
        }

        var layers = JArray.Parse("""
            [
              {"redeem":{"action":{"param":{"custom_action_param":{"selected_code":"A","codes":["old"],"run_id":"old-run"}}}},"other":{"enabled":true}},
              {"redeem":{"action":{"param":{"custom_action_param":{"selected_code":"B"}}}},"other":{"enabled":false}},
              {"redeem":{"action":{"param":{"custom_action_param":{"extra":"keep"}}}}}
            ]
            """);
        var result = JArray.Parse(await panel.PrepareRunAsync(layers.ToString()));
        Check(result.Count == layers.Count + 1 && JToken.DeepEquals(new JArray(result.Take(layers.Count)), layers),
            "multiple override layers retain all original values and ordering");
        Check(RunArguments(result.Last!)["codes"]!.Values<string>().SequenceEqual(new[] { "B" }),
            "later selection wins and a layer without a selection preserves it");
        Check(RunArguments(result.Last!)["run_id"]!.Value<string>() != "old-run",
            "current run id is appended after stale run parameters");

        RunArguments(layers.Last!)["selected_code"] = JValue.CreateNull();
        var cleared = JArray.Parse(await panel.PrepareRunAsync(layers.ToString()));
        Check(RunArguments(cleared.Last!)["codes"]!.Values<string>().SequenceEqual(new[] { "A", "B" }),
            "explicit null clears an earlier single-code selection");
        RunArguments(layers.Last!)["selected_code"] = "unavailable";
        var unavailable = JArray.Parse(await panel.PrepareRunAsync(layers.ToString()));
        Check(!RunArguments(unavailable.Last!)["codes"]!.Any(),
            "an unavailable selection cannot fall back to redeeming all codes");

        var obj = JObject.Parse(await panel.PrepareRunAsync("""
            {"redeem":{"action":{"param":{"custom_action_param":{"selected_code":"B","codes":["old"],"extra":"keep"}}}}}
            """));
        Check(RunArguments(obj)["codes"]!.Values<string>().SequenceEqual(new[] { "B" })
            && RunArguments(obj)["extra"]!.Value<string>() == "keep",
            "object input replaces stale codes and retains unrelated custom parameters");
        foreach (var input in new[] { "", "  " })
        {
            var empty = JObject.Parse(await panel.PrepareRunAsync(input));
            Check(RunArguments(empty)["codes"]!.Count() == 2, "blank input is treated as an empty object");
        }

        foreach (var input in new[] { "null", "1", "\"text\"", "[{},null]", "[[]]", "[{},1]" })
        {
            try
            {
                await panel.PrepareRunAsync(input);
            }
            catch (JsonSerializationException)
            {
                Check(true, $"invalid pipeline shape is rejected: {input}");
                continue;
            }
            throw new Exception($"Invalid pipeline shape was accepted: {input}");
        }
    }
}
