using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MFAAvalonia.Helper;

namespace MFAAvalonia.Views.Mobile;

public partial class RootViewMobile : UserControl
{
    public RootViewMobile()
    {
        InitializeComponent();
        Loaded += (_, _) => RedeemCodeNotification.Instance.ShowPending();
    }
}
