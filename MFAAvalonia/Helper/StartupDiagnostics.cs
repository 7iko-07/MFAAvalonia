using System;
using System.Diagnostics;

namespace MFAAvalonia.Helper;

/// <summary>
/// 启动阶段计时；包含进程创建到第一条日志之间的耗时，不以日志初始化作为起点。
/// Loaded 仅代表视图已加载，并非屏幕首帧实际呈现时间。
/// </summary>
public static class StartupDiagnostics
{
    private static readonly TimeSpan InitialProcessAge = GetProcessAge();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object Sync = new();
    private static TimeSpan _previous;

    public static void Mark(string stage)
    {
        lock (Sync)
        {
            var elapsed = InitialProcessAge + Clock.Elapsed;
            var interval = elapsed - _previous;
            _previous = elapsed;
            LoggerHelper.Info($"[启动耗时] {stage}：进程累计={elapsed.TotalMilliseconds:F0}ms，距上阶段={interval.TotalMilliseconds:F0}ms");
        }
    }

    private static TimeSpan GetProcessAge()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return DateTime.UtcNow - process.StartTime.ToUniversalTime();
        }
        catch
        {
            // 不支持读取进程时间的平台从首次使用计时器开始计算。
            return TimeSpan.Zero;
        }
    }
}
