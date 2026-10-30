using Avalonia.Controls.Notifications;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using SukiUI.Toasts;

// Run the production helper with a controllable UI dispatcher and toast sink.
var notifications = new (NotificationType Type, Action<string?> Send)[]
{
    (NotificationType.Success, id => ToastHelper.Success("title", "body", 7, id)),
    (NotificationType.Information, id => ToastHelper.Info("title", "body", 7, id)),
    (NotificationType.Warning, id => ToastHelper.Warn("title", "body", 7, id)),
    (NotificationType.Error, id => ToastHelper.Error("title", "body", 7, id))
};
var checks = 0;
foreach (var (type, send) in notifications)
{
    Reset();
    send("selected");
    DispatcherHelper.Drain();
    Check(Instances.ToastManager.Shown.Count == 1, $"{type}: selected instance was hidden");
    var toast = Instances.ToastManager.Shown.Single();
    Check(toast.Type == type && toast.Title == "title" && Equals(toast.Content, "body") &&
        toast.Timeout == TimeSpan.FromSeconds(7), $"{type}: presentation changed");

    Reset();
    send("background");
    Check(DispatcherHelper.Pending.Count == 0, $"{type}: background message was deferred");
    MaaProcessorManager.Instance.Current = new("background");
    DispatcherHelper.Drain();
    Check(Instances.ToastManager.Shown.Count == 0, $"{type}: discarded message resurfaced on selection");

    Reset();
    send("selected");
    MaaProcessorManager.Instance.Current = new("other");
    DispatcherHelper.Drain();
    Check(Instances.ToastManager.Shown.Count == 0, $"{type}: selection change before UI dispatch was ignored");

    Reset();
    send(null);
    MaaProcessorManager.Instance.Current = null;
    DispatcherHelper.Drain();
    Check(Instances.ToastManager.Shown.Count == 1, $"{type}: global notification was hidden");

    Reset();
    MaaProcessorManager.Instance.Current = null;
    send("selected");
    DispatcherHelper.Drain();
    Check(Instances.ToastManager.Shown.Count == 0, $"{type}: instance notification appeared with no selection");
}
Reset();
ToastHelper.Warn("persistent", duration: -1, instanceId: "selected");
DispatcherHelper.Drain();
Check(Instances.ToastManager.Shown.Single().Timeout == null, "persistent notification became timed");
Console.WriteLine($"PASS: {checks} notification checks");

void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

void Reset()
{
    MaaProcessorManager.Instance.Current = new("selected");
    DispatcherHelper.Pending.Clear();
    Instances.ToastManager.Shown.Clear();
}

namespace MFAAvalonia.Extensions.MaaFW
{
    public sealed record MaaProcessor(string InstanceId);
    public sealed class MaaProcessorManager
    {
        public static MaaProcessorManager Instance { get; } = new();
        public MaaProcessor? Current { get; set; }
    }
}

namespace MFAAvalonia.Helper
{
    public static class DispatcherHelper
    {
        public static Queue<Action> Pending { get; } = new();
        public static void RunOnMainThread(Action action) => Pending.Enqueue(action);
        public static void Drain()
        {
            while (Pending.TryDequeue(out var action)) action();
        }
    }

    public static class Instances
    {
        public static SukiToastManager ToastManager { get; } = new();
    }
}

namespace Avalonia.Controls.Notifications
{
    public enum NotificationType { Success, Information, Warning, Error }
}

namespace SukiUI.Toasts
{
    public sealed class SukiToastManager
    {
        public List<SukiToastBuilder> Shown { get; } = new();
        public SukiToastBuilder CreateToast() => new(this);
    }

    public sealed class SukiToastBuilder(SukiToastManager manager)
    {
        public string Title { get; private set; } = "";
        public object? Content { get; private set; }
        public NotificationType Type { get; private set; }
        public TimeSpan? Timeout { get; private set; }
        public SukiToastBuilder WithTitle(string value) { Title = value; return this; }
        public SukiToastBuilder WithContent(object? value) { Content = value; return this; }
        public SukiToastBuilder OfType(NotificationType value) { Type = value; return this; }
        public SukiToastBuilder Dismiss() => this;
        public SukiToastBuilder ByClicking() => this;
        public SukiToastBuilder After(TimeSpan value) { Timeout = value; return this; }
        public void Queue() => manager.Shown.Add(this);
    }
}
