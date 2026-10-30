using Avalonia.Controls.Notifications;
using MFAAvalonia.Extensions.MaaFW;
using SukiUI.Toasts;
using System;

namespace MFAAvalonia.Helper;

public static class ToastHelper
{
    public static SukiToastBuilder CreateToastByType(NotificationType toastType, string title = "", object? content = null, int duration = 3)
    {
        if (duration <= 0)
        {
            return Instances.ToastManager.CreateToast()
           .WithTitle(title)
           .WithContent(
               content)
           .OfType(toastType).Dismiss().ByClicking();
        }
        return Instances.ToastManager.CreateToast()
            .WithTitle(title)
            .WithContent(
                content)
            .OfType(toastType).Dismiss().After(TimeSpan.FromSeconds(duration))
            .Dismiss().ByClicking();
    }

    // 没有实例归属的消息属于全局通知，始终保留。
    internal static bool ShouldShowForInstance(string? instanceId) =>
        instanceId == null || string.Equals(instanceId, MaaProcessorManager.Instance.Current?.InstanceId, StringComparison.Ordinal);

    private static void Show(NotificationType type, string title, object? content, int duration, string? instanceId)
    {
        // 后台消息直接丢弃，不能等切回该实例后再显示。
        if (!ShouldShowForInstance(instanceId)) return;
        DispatcherHelper.RunOnMainThread(() =>
        {
            // 等待 UI 线程期间可能切换了实例，入队前再检查一次。
            if (ShouldShowForInstance(instanceId))
                CreateToastByType(type, title, content, duration).Queue();
        });
    }

    public static void Success(string title = "", object? content = null, int duration = 3, string? instanceId = null)
    {
        Show(NotificationType.Success, title, content, duration, instanceId);
    }

    public static void Info(string title = "", object? content = null, int duration = 3, string? instanceId = null)
    {
        Show(NotificationType.Information, title, content, duration, instanceId);
    }

    public static void Warn(string title = "", object? content = null, int duration = 3, string? instanceId = null)
    {
        Show(NotificationType.Warning, title, content, duration, instanceId);
    }

    public static void Error(string title = "", object? content = null, int duration = 3, string? instanceId = null)
    {
        Show(NotificationType.Error, title, content, duration, instanceId);
    }
}
