using MFAAvalonia.ViewModels.Other;

namespace MFAAvalonia.ViewModels.Pages;

public partial class TaskQueueViewModel
{
    private RedeemCodePanelViewModel? _redeemCodes;
    public RedeemCodePanelViewModel RedeemCodes => _redeemCodes ??= new(this);

    public void DisposeRedeemCodes() => _redeemCodes?.Dispose();
}
