using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MFAAvalonia.Configuration;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MFAAvalonia.Helper;

public sealed class RedeemCodeSettings
{
    [JsonProperty("url")] public string Url { get; set; } = string.Empty;
    [JsonProperty("entry")] public string Entry { get; set; } = string.Empty;
}

public sealed class RedeemCode
{
    [JsonProperty("code")] public string Code { get; set; } = string.Empty;
    [JsonProperty("starts_at")] public DateTimeOffset? StartsAt { get; set; }
    [JsonProperty("expires_at")] public DateTimeOffset? ExpiresAt { get; set; }

    public bool IsAvailable(DateTimeOffset now) =>
        (!StartsAt.HasValue || StartsAt <= now) && (!ExpiresAt.HasValue || ExpiresAt > now);
}

public sealed class RedeemCodeDocument
{
    [JsonProperty("schema_version")] public int SchemaVersion { get; set; }
    [JsonProperty("codes")] public List<RedeemCode> Codes { get; set; } = [];

    public static RedeemCodeDocument Parse(string json)
    {
        var document = JsonConvert.DeserializeObject<RedeemCodeDocument>(json);
        if (document?.SchemaVersion != 1 || document.Codes == null
            || document.Codes.Any(item => item == null || string.IsNullOrWhiteSpace(item.Code)))
            throw new InvalidDataException("Invalid redemption code feed");
        document.Codes = document.Codes.DistinctBy(item => item.Code, StringComparer.Ordinal).ToList();
        return document;
    }

    public List<RedeemCode> Available(DateTimeOffset now) => Codes.Where(item => item.IsAvailable(now)).ToList();

    public DateTimeOffset? NextChange(DateTimeOffset now) => Codes
        .SelectMany(item => new[] { item.StartsAt, item.ExpiresAt })
        .Where(time => time > now).Min();
}

/// <summary>所有实例共用一次启动获取和已读状态；计时器只计算本地开启/过期时间。</summary>
public partial class RedeemCodeFeed : ObservableObject
{
    public const string NotificationsEnabledKey = "RedeemCodes.NotificationsEnabled";
    public static RedeemCodeFeed Instance { get; } = new();
    public RedeemCodeSettings? Settings { get; private set; }
    public IReadOnlyList<RedeemCode> Available { get; private set; } = [];
    public bool Enabled => Settings != null;
    public event Action? Changed;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _error = string.Empty;
    [ObservableProperty] private int _unreadCount;
    private bool _notificationsEnabled = true;
    public bool NotificationsEnabled
    {
        get => _notificationsEnabled;
        set
        {
            if (SetProperty(ref _notificationsEnabled, value))
                OnNotificationsEnabledChanged(value);
        }
    }
    public bool ShowNotice => NotificationsEnabled && UnreadCount > 0;

    private RedeemCodeDocument _document = new();
    private HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _timer = new();
    private Task? _initialization;
    private Task? _refresh;
    private string _seenKey = string.Empty;

    private RedeemCodeFeed()
    {
        _timer.Tick += (_, _) => Recalculate();
    }

    public Task InitializeAsync() => _initialization ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        var path = Path.Combine(AppPaths.DataRoot, "redeem_codes.json");
        if (!File.Exists(path)) return;
        try
        {
            Settings = JsonConvert.DeserializeObject<RedeemCodeSettings>(File.ReadAllText(path));
            if (string.IsNullOrWhiteSpace(Settings?.Entry) || string.IsNullOrWhiteSpace(Settings.Url))
                throw new InvalidDataException("Invalid redeem_codes.json");
            _seenKey = "RedeemCodes.Seen." + Settings.Url;
            _seen = JsonConvert.DeserializeObject<HashSet<string>>(GlobalConfiguration.GetValue(_seenKey, "[]")) ?? [];
            _notificationsEnabled = !bool.TryParse(GlobalConfiguration.GetValue(NotificationsEnabledKey), out var enabled) || enabled;
            OnPropertyChanged(nameof(NotificationsEnabled));
            OnPropertyChanged(nameof(Enabled));
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            LoggerHelper.Warning($"兑换码配置加载失败：{ex.Message}");
        }
    }

    public Task RefreshAsync()
    {
        if (_refresh is { IsCompleted: false }) return _refresh;
        return _refresh = RefreshCoreAsync();
    }

    private async Task RefreshCoreAsync()
    {
        if (Settings == null) return;
        IsLoading = true;
        Error = string.Empty;
        try
        {
            using var client = VersionChecker.CreateHttpClientWithProxy();
            client.Timeout = TimeSpan.FromSeconds(20);
            var json = await client.GetStringAsync(Settings.Url);
            _document = RedeemCodeDocument.Parse(json);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            LoggerHelper.Warning($"兑换码获取失败：{ex.Message}");
        }
        finally
        {
            IsLoading = false;
            Recalculate();
        }
    }

    public void Recalculate()
    {
        var now = DateTimeOffset.UtcNow;
        Available = _document.Available(now);
        // 静默期间的新码也记入已处理记录，重新开启通知时不补发。
        if (!NotificationsEnabled && Available.Any(item => !_seen.Contains(item.Code)))
        {
            _seen.UnionWith(Available.Select(item => item.Code));
            SaveSeen();
        }
        UnreadCount = Available.Count(item => !_seen.Contains(item.Code));
        OnPropertyChanged(nameof(ShowNotice));
        _timer.Stop();
        if (_document.NextChange(now) is { } next)
        {
            _timer.Interval = next - now;
            _timer.Start();
        }
        Changed?.Invoke();
    }

    public void MarkSeen()
    {
        _seen.UnionWith(_document.Available(DateTimeOffset.UtcNow).Select(item => item.Code));
        SaveSeen();
        UnreadCount = 0;
        OnPropertyChanged(nameof(ShowNotice));
        Changed?.Invoke();
    }

    private void SaveSeen()
    {
        if (!string.IsNullOrEmpty(_seenKey))
            GlobalConfiguration.SetValue(_seenKey, JsonConvert.SerializeObject(_seen));
    }

    private void OnNotificationsEnabledChanged(bool value)
    {
        GlobalConfiguration.SetValue(NotificationsEnabledKey, value.ToString());
        // 两个方向都清理当前可用的新码，包括计时器尚未处理的开启边界。
        MarkSeen();
    }

    public void Dismiss() => MarkSeen();
}
