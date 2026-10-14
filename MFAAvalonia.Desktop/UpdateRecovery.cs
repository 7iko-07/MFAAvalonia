using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace MFAAvalonia.Desktop;

// Keep this bootstrap independent of Core and native dependencies. A crash can
// leave the installation between versions of those assemblies.
internal static class UpdateRecovery
{
    public static bool TryRecover(string installRoot)
    {
        var lockPath = Path.Combine(installRoot, ".mfa-update.lock");
        if (File.Exists(lockPath))
        {
            try { using var updateLock = File.Open(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { Console.Error.WriteLine("更新仍在进行，请等待更新器完成。"); return true; }
        }
        var journalPath = Path.Combine(installRoot, ".mfa-update-journal.json");
        if (!File.Exists(journalPath)) return false;
        using var document = JsonDocument.Parse(File.ReadAllText(journalPath));
        var updater = Path.GetFullPath(document.RootElement.GetProperty("plan").GetProperty("updaterPath").GetString()
                                       ?? throw new InvalidDataException("更新恢复程序路径为空。"));
        var backupRoot = Path.GetFullPath(document.RootElement.GetProperty("plan").GetProperty("backupRoot").GetString()
                                          ?? throw new InvalidDataException("更新备份路径为空。"));
        var stagedRoot = Path.Combine(Path.GetDirectoryName(backupRoot)!, "updater") + Path.DirectorySeparatorChar;
        if (!updater.StartsWith(stagedRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(updater)
            || Path.GetFileNameWithoutExtension(updater) is not ("MFAUpdater" or "M9AUpdater"))
            throw new InvalidDataException("更新中断且恢复程序缺失，请重新安装完整包；配置目录可保留。");
        var start = new ProcessStartInfo(updater) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--recover");
        start.ArgumentList.Add(journalPath);
        using var process = Process.Start(start) ?? throw new IOException("无法启动更新恢复程序。");
        return true;
    }
}
