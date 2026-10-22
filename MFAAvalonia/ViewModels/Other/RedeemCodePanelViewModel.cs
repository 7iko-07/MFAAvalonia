using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MFAAvalonia.Extensions;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Pages;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace MFAAvalonia.ViewModels.Other;

public partial class RedeemCodeRow : ObservableObject
{
    public RedeemCode CodeInfo { get; set; }
    public string Code => CodeInfo.Code;
    public RedeemCodePanelViewModel Panel { get; }
    public string Expiry => CodeInfo.ExpiresAt is { } end
        ? string.Format(LangKeys.RedeemExpires.ToLocalization(), end.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
        : LangKeys.RedeemExpiryUnknown.ToLocalization();
    [ObservableProperty] private string _result = string.Empty;
    [ObservableProperty] private bool _succeeded;

    public RedeemCodeRow(RedeemCode code, RedeemCodePanelViewModel panel)
    {
        CodeInfo = code;
        Panel = panel;
    }

    public void Update(RedeemCode code)
    {
        CodeInfo = code;
        OnPropertyChanged(nameof(Expiry));
    }
}

public partial class RedeemCodePanelViewModel : ObservableObject, IDisposable
{
    public RedeemCodeFeed Feed => RedeemCodeFeed.Instance;
    public ObservableCollection<RedeemCodeRow> Codes { get; } = [];
    public bool CanRedeem => _owner.Idle && !Feed.IsLoading && Codes.Count > 0;
    public bool IsTaskRunning => !_owner.Idle;
    public string Summary => string.Format(LangKeys.RedeemAvailable.ToLocalization(), Codes.Count);
    public string Notice => string.Format(LangKeys.RedeemNewNotice.ToLocalization(), Feed.UnreadCount);
    public string Badge => string.Format(LangKeys.RedeemNewBadge.ToLocalization(), Feed.UnreadCount);
    public bool HasUnread => Feed.UnreadCount > 0;
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _progress = string.Empty;

    private readonly TaskQueueViewModel _owner;
    private readonly Dictionary<string, RedeemCodeRow> _rows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _completed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _runCodes = new(StringComparer.Ordinal);
    private string? _runId;

    public RedeemCodePanelViewModel(TaskQueueViewModel owner)
    {
        _owner = owner;
        Feed.Changed += UpdateCodes;
        Feed.PropertyChanged += OnFeedChanged;
        owner.PropertyChanged += OnOwnerChanged;
        LanguageHelper.LanguageChanged += OnLanguageChanged;
    }

    public async Task InitializeAsync()
    {
        await Feed.InitializeAsync();
        UpdateCodes();
    }

    public bool IsRedeemTask(string? entry) => Feed.Settings != null && entry == Feed.Settings.Entry;

    private void UpdateCodes()
    {
        Codes.Clear();
        foreach (var code in Feed.Available)
        {
            if (!_rows.TryGetValue(code.Code, out var row))
                _rows[code.Code] = row = new RedeemCodeRow(code, this);
            else
                row.Update(code);
            Codes.Add(row);
        }
        OnPropertyChanged(nameof(Summary));
        NotifyActions();
    }

    private void OnFeedChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Notice));
        OnPropertyChanged(nameof(Badge));
        OnPropertyChanged(nameof(HasUnread));
        UpdateTaskBadges();
        NotifyActions();
    }

    public void UpdateTaskBadges()
    {
        foreach (var task in _owner.TaskItemViewModels)
            task.RedeemCodeBadge = HasUnread && IsRedeemTask(task.InterfaceItem?.Entry) ? Badge : null;
    }

    private void OnOwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TaskQueueViewModel.Idle)) return;
        OnPropertyChanged(nameof(IsTaskRunning));
        if (_owner.Idle)
        {
            FinishRun();
        }
        NotifyActions();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        UpdateCodes();
        OnFeedChanged(null, new PropertyChangedEventArgs(null));
    }

    private void NotifyActions()
    {
        OnPropertyChanged(nameof(CanRedeem));
        RedeemAllCommand.NotifyCanExecuteChanged();
        RedeemOneCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task Refresh() => await Feed.RefreshAsync();

    [RelayCommand]
    private void Open()
    {
        var task = _owner.TaskItemViewModels.FirstOrDefault(item => IsRedeemTask(item.InterfaceItem?.Entry));
        if (task == null) return;
        foreach (var item in _owner.TaskItemViewModels.Where(item => item.EnableSetting && item != task))
            item.EnableSetting = false;
        task.EnableSetting = true;
        if (IsOpen) Feed.MarkSeen();
    }

    public void Select(bool selected)
    {
        IsOpen = selected;
        if (selected) Feed.MarkSeen();
    }

    [RelayCommand]
    private void Dismiss() => Feed.Dismiss();

    [RelayCommand(CanExecute = nameof(CanRedeem))]
    private void RedeemAll() => Start(null);

    [RelayCommand(CanExecute = nameof(CanRedeem))]
    private void RedeemOne(RedeemCodeRow row) => Start(row.Code);

    private void Start(string? code)
    {
        var task = _owner.TaskItemViewModels.FirstOrDefault(item => IsRedeemTask(item.InterfaceItem?.Entry));
        if (task == null) return;
        // 仅为这次运行克隆参数，不改变任务勾选、顺序或保存的选项。
        var runTask = new DragItemViewModel(task.InterfaceItem!.Clone());
        if (code != null)
        {
            runTask.InterfaceItem!.PipelineOverride ??= new();
            runTask.InterfaceItem.PipelineOverride[Feed.Settings!.Entry] = JObject.FromObject(new
            {
                action = new { param = new { custom_action_param = new { selected_code = code } } }
            });
        }
        _owner.Processor.Start([runTask], ignoreCheckedState: true);
    }

    public async Task<string> PrepareRunAsync(string? pipeline)
    {
        await Feed.InitializeAsync();
        Feed.Recalculate();
        var root = JToken.Parse(string.IsNullOrWhiteSpace(pipeline) ? "{}" : pipeline);
        var layers = root switch
        {
            JObject obj => new[] { obj },
            JArray array when array.All(item => item is JObject) => array.Cast<JObject>().ToArray(),
            _ => throw new JsonSerializationException("Redeem pipeline must be an object or an array of objects.")
        };
        var entry = Feed.Settings!.Entry;
        string? selected = null;
        foreach (var layer in layers)
        {
            var selection = layer[entry]?["action"]?["param"]?["custom_action_param"]?["selected_code"];
            // 后层未设置时保留前层选择，显式 null 则清除选择。
            if (selection != null) selected = selection.Value<string>();
        }
        var codes = Feed.Available.Where(item => selected == null || item.Code == selected).Select(item => item.Code).ToList();
        BeginRun(Guid.NewGuid().ToString("N"), codes);
        var runOverride = JObject.FromObject(new Dictionary<string, object>
        {
            [entry] = new { action = new { param = new { custom_action_param = new { run_id = _runId, codes } } } }
        });
        if (root is JArray overrides)
            // 保持 MaaToken 的覆盖层及顺序，本次运行参数最后生效。
            overrides.Add(runOverride);
        else
            ((JObject)root).Merge(runOverride, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
        return root.ToString(Formatting.None);
    }

    private void BeginRun(string runId, IEnumerable<string> codes)
    {
        _runId = runId;
        _runCodes.Clear();
        _runCodes.UnionWith(codes);
        _completed.Clear();
        foreach (var row in _rows.Values)
        {
            row.Result = string.Empty;
            row.Succeeded = false;
        }
        UpdateProgress();
    }

    public void HandleMessage(JObject message)
    {
        if (message["run_id"]?.Value<string>() != _runId) return;
        var kind = message["kind"]?.Value<string>();
        var code = message["code"]?.Value<string>();
        if (code != null && _rows.TryGetValue(code, out var row))
        {
            if (kind == "running") row.Result = LangKeys.RedeemRunning.ToLocalization();
            if (kind == "result")
            {
                row.Succeeded = message["success"]?.Value<bool>() == true;
                row.Result = message["message"]?.Value<string>() ?? string.Empty;
                _completed.Add(code);
                UpdateProgress();
            }
        }
        if (kind == "error") Progress = message["message"]?.Value<string>() ?? string.Empty;
        if (kind == "end") FinishRun();
    }

    private void UpdateProgress() => Progress = string.Format(LangKeys.RedeemProgress.ToLocalization(), _completed.Count, _runCodes.Count);

    public void FinishRun()
    {
        if (_runId == null) return;
        foreach (var code in _runCodes.Except(_completed))
            if (_rows.TryGetValue(code, out var row)) row.Result = LangKeys.RedeemNotCompleted.ToLocalization();
    }

    public void Dispose()
    {
        Feed.Changed -= UpdateCodes;
        Feed.PropertyChanged -= OnFeedChanged;
        _owner.PropertyChanged -= OnOwnerChanged;
        LanguageHelper.LanguageChanged -= OnLanguageChanged;
    }
}
