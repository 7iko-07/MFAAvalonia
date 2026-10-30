using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.VisualTree;
using MFAAvalonia.Extensions;
using MFAAvalonia.Views.Mobile;
using SukiUI.Enums;
using SukiUI.Toasts;
using System;
using System.Linq;

namespace MFAAvalonia.Helper;

/// <summary>所有实例共用一条兑换码通知；独立宿主使其不会被普通通知挤掉。</summary>
public sealed class RedeemCodeNotification : IDisposable
{
    private static readonly Lazy<RedeemCodeNotification> Shared = new(() =>
        new(RedeemCodeFeed.Instance, new SukiToastManager(), OpenPanel));
    public static RedeemCodeNotification Instance => Shared.Value;
    public ISukiToastManager Manager { get; }

    private readonly RedeemCodeFeed _feed;
    private readonly Action _open;
    private ISukiToast? _toast;

    public RedeemCodeNotification(RedeemCodeFeed feed, ISukiToastManager manager, Action open)
    {
        _feed = feed;
        Manager = manager;
        _open = open;
        feed.Changed += ShowPending;
        LanguageHelper.LanguageChanged += OnLanguageChanged;
    }

    private static void OpenPanel()
    {
        Instances.TopLevel?.GetVisualDescendants().OfType<RootViewContent>().FirstOrDefault()?.NavigateToHome();
        Instances.InstanceTabBarViewModel.ActiveTab?.TaskQueueViewModel?.RedeemCodes.OpenCommand.Execute(null);
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => ShowPending();

    // 宿主绑定 Manager 后再调用，避免初始化前已获取的兑换码丢失首次通知。
    public void ShowPending()
    {
        if (!_feed.ShowNotice)
        {
            Clear();
            return;
        }

        var title = string.Format(LangKeys.RedeemNewNotice.ToLocalization(), _feed.UnreadCount);
        if (_toast != null)
        {
            _toast.Title = title;
            foreach (var button in _toast.ActionButtons.OfType<Button>())
                button.Content = LangKeys.RedeemView.ToLocalization();
            return;
        }

        var builder = Manager.CreateToast()
            .WithTitle(title)
            .OfType(NotificationType.Information)
            .Dismiss().ByClicking()
            .WithActionButton(LangKeys.RedeemView.ToLocalization(), _ =>
            {
                _open();
                _feed.MarkSeen();
            }, style: SukiButtonStyles.Basic)
            .OnDismissed((toast, source) =>
            {
                if (!ReferenceEquals(_toast, toast)) return;
                _toast = null;
                if (source is SukiToastDismissSource.Click or SukiToastDismissSource.ActionButton)
                    _feed.Dismiss();
            });
        _toast = builder.Toast;
        builder.Queue();
    }

    private void Clear()
    {
        if (_toast == null) return;
        var toast = _toast;
        _toast = null;
        Manager.Dismiss(toast);
    }

    public void Dispose()
    {
        _feed.Changed -= ShowPending;
        LanguageHelper.LanguageChanged -= OnLanguageChanged;
        Clear();
    }
}
