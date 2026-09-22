using System;
using Avalonia;
using Avalonia.Controls;

namespace MFAAvalonia.Controls;

/// <summary>
/// 只在页面首次进入可视树时创建内容，切换回来时保留原有页面状态。
/// </summary>
public sealed class DeferredView : Decorator
{
    private Func<Control>? _factory;

    public DeferredView(Func<Control> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_factory == null) return;

        Child = _factory();
        _factory = null;
    }
}
