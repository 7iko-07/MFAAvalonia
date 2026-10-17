using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.Json;

namespace MFAUpdater;

public sealed class TransactionalUpdate(Action<string>? log = null, Action<int>? beforeWrite = null)
{
    public const string JournalName = ".mfa-update-journal.json";
    private readonly Action<string> _log = log ?? (_ => { });

    public void Apply(UpdatePlan plan)
    {
        ValidatePlan(plan);
        var journalPath = Path.Combine(plan.InstallRoot, JournalName);
        using var updateLock = new FileStream(Path.Combine(plan.InstallRoot, ".mfa-update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(journalPath)) throw new IOException("上次更新尚未恢复，禁止继续覆盖。");
        VerifyPayload(plan);
        if (plan.Manifest.Kind == "delta") VerifyBaseline(plan);
        Directory.CreateDirectory(plan.BackupRoot);
        var journal = new UpdateJournal { Plan = plan };
        var committed = false;
        try
        {
            var operations = plan.Manifest.Payload.Concat(plan.Deleted).Distinct(UpdatePaths.Comparer).ToList();
            // Write version metadata last; it participates in rollback until verification passes.
            operations.RemoveAll(p => p is "interface.json" or "interface.jsonc");
            operations.AddRange(plan.Manifest.Payload.Concat(plan.Deleted).Where(p => p is "interface.json" or "interface.jsonc"));
            if (plan.IsResourceUpdate && plan.HasCompleteFileList) operations.Add(UpdateManifest.FileName);
            Save(journalPath, journal);
            for (var i = 0; i < operations.Count; i++)
            {
                var relative = operations[i];
                var target = plan.Target(relative);
                var backup = Path.Combine(plan.BackupRoot, i.ToString("D8"));
                var oldLink = new FileInfo(target).LinkTarget;
                var record = new UpdateRecord { Path = relative, Backup = backup, Existed = File.Exists(target) || oldLink != null, LinkTarget = oldLink };
                if (Directory.Exists(target) && oldLink == null) throw new IOException($"目标文件是目录：{relative}");
                if (record.Existed)
                {
                    record.Attributes = (int)File.GetAttributes(target);
                    if (oldLink != null) File.WriteAllText(backup, oldLink);
                    else Retry(() => File.Copy(target, backup, true));
                    record.Mode = OperatingSystem.IsWindows() ? 420 : (int)File.GetUnixFileMode(target) & 511;
                }
                journal.Records.Add(record);
                // Persist undo information before modifying the installation.
                Save(journalPath, journal);
                beforeWrite?.Invoke(i);
                if (relative == UpdateManifest.FileName)
                {
                    var content = JsonSerializer.Serialize(plan.Manifest.AsInstalled(), UpdateManifest.JsonOptions);
                    AtomicWrite(target, path => File.WriteAllText(path, content));
                }
                else if (plan.Deleted.Contains(relative, UpdatePaths.Comparer))
                    Retry(() =>
                    {
                        DeleteFile(target);
                        PruneEmptyParents(target, UpdatePaths.IsData(relative) ? plan.DataRoot : plan.InstallRoot);
                    });
                else
                {
                    var source = UpdatePaths.Resolve(plan.SourceRoot, relative);
                    var entry = plan.Manifest.Files.Single(f => f.Path == relative);
                    if (entry.LinkTarget != null) AtomicWrite(target, path => File.CreateSymbolicLink(path, entry.LinkTarget));
                    else AtomicWrite(target, path => File.Copy(source, path, true));
                    if (!OperatingSystem.IsWindows() && entry.LinkTarget == null) File.SetUnixFileMode(target, (UnixFileMode)entry.Mode);
                }
                _log($"已更新：{relative}");
            }
            foreach (var relative in plan.EmptyDirectories.OrderByDescending(p => p.Length))
            {
                var directory = plan.Target(relative);
                if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any()) continue;
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"不能清理符号链接目录：{relative}");
                journal.Records.Add(new UpdateRecord
                {
                    Path = relative, Backup = Path.Combine(plan.BackupRoot, journal.Records.Count.ToString("D8")),
                    Existed = true, IsDirectory = true, Attributes = (int)File.GetAttributes(directory),
                    Mode = OperatingSystem.IsWindows() ? 420 : (int)File.GetUnixFileMode(directory) & 511
                });
                Save(journalPath, journal);
                Directory.Delete(directory);
            }
            foreach (var entry in plan.Manifest.Files)
                if (!entry.Matches(plan.Target(entry.Path))) throw new InvalidDataException($"安装后文件校验失败：{entry.Path}");
            VerifyPythonDependencies(plan);
            journal.Committed = true;
            Save(journalPath, journal);
            committed = true;
            try { File.Delete(journalPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { _log("更新已提交，恢复记录将在下次启动时清理。"); }
            _log("更新已完成，文件与依赖校验通过。");
        }
        catch (Exception error) when (!committed)
        {
            _log($"更新失败，正在恢复：{error.Message}");
            Rollback(journal, journalPath);
            throw;
        }
    }

    public static void VerifyPayload(UpdatePlan plan)
    {
        plan.Manifest.Validate();
        foreach (var relative in plan.Manifest.Payload)
        {
            var file = plan.Manifest.Files.Single(f => f.Path == relative);
            if (!file.Matches(UpdatePaths.Resolve(plan.SourceRoot, relative, allowFinalLink: file.LinkTarget != null)))
                throw new InvalidDataException($"更新文件校验失败：{relative}");
        }
    }

    public static void VerifyBaseline(UpdatePlan plan)
    {
        foreach (var file in plan.Manifest.BaseFiles)
        {
            // interface.json is customized by older clients; validate its version instead.
            if (file.Path == "interface.json")
            {
                using var json = JsonDocument.Parse(File.ReadAllText(plan.Target(file.Path)));
                if (json.RootElement.GetProperty("version").GetString() != plan.Manifest.BaseVersion)
                    throw new InvalidDataException("本地资源版本与增量包不一致。");
            }
            else if (!file.Matches(plan.Target(file.Path)))
                throw new InvalidDataException($"本地文件不符合增量包基准：{file.Path}");
        }
    }

    public UpdatePlan Recover(string journalPath)
    {
        var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(journalPath), UpdateManifest.JsonOptions)
                      ?? throw new InvalidDataException("更新恢复记录为空。");
        ValidatePlan(journal.Plan);
        if (Path.GetFullPath(journalPath) != Path.Combine(journal.Plan.InstallRoot, JournalName))
            throw new InvalidDataException("恢复记录路径无效。");
        using var updateLock = new FileStream(Path.Combine(journal.Plan.InstallRoot, ".mfa-update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (journal.Committed) File.Delete(journalPath);
        else
        {
            Rollback(journal, journalPath);
            journal.Plan.RestartPath = journal.Plan.PreviousRestartPath;
        }
        return journal.Plan;
    }

    private void Rollback(UpdateJournal journal, string journalPath)
    {
        // Keep the journal and backups if any recovery step fails; a later run can retry.
        foreach (var record in journal.Records.AsEnumerable().Reverse())
        {
            if (Path.GetDirectoryName(Path.GetFullPath(record.Backup)) != Path.GetFullPath(journal.Plan.BackupRoot))
                throw new InvalidDataException("恢复文件越界。");
            var target = journal.Plan.Target(record.Path);
            if (record.IsDirectory)
            {
                Directory.CreateDirectory(target);
                if (OperatingSystem.IsWindows()) File.SetAttributes(target, (FileAttributes)record.Attributes);
                else File.SetUnixFileMode(target, (UnixFileMode)record.Mode);
                continue;
            }
            if (record.Existed)
            {
                if (record.LinkTarget != null) AtomicWrite(target, path => File.CreateSymbolicLink(path, record.LinkTarget));
                else AtomicWrite(target, path => File.Copy(record.Backup, path, true));
                if (!OperatingSystem.IsWindows() && record.LinkTarget == null) File.SetUnixFileMode(target, (UnixFileMode)record.Mode);
                if (OperatingSystem.IsWindows() && record.LinkTarget == null) File.SetAttributes(target, (FileAttributes)record.Attributes);
            }
            else Retry(() =>
            {
                DeleteFile(target);
                PruneEmptyParents(target, UpdatePaths.IsData(record.Path) ? journal.Plan.DataRoot : journal.Plan.InstallRoot);
            });
        }
        File.Delete(journalPath);
        _log("已恢复更新前的文件与版本信息。");
    }

    private static void ValidatePlan(UpdatePlan plan)
    {
        plan.Manifest.Validate();
        plan.Manifest.ValidatePlatform();
        foreach (var root in new[] { plan.SourceRoot, plan.InstallRoot, plan.DataRoot, plan.BackupRoot })
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || Path.GetFullPath(root) != root)
                throw new InvalidDataException("更新目录必须是规范的绝对路径。");
        if (plan.SourceRoot == plan.InstallRoot || plan.SourceRoot == plan.DataRoot
            || plan.BackupRoot == plan.InstallRoot || plan.BackupRoot == plan.DataRoot)
            throw new InvalidDataException("更新源、目标和备份不能重合。");
        if (!Path.GetFullPath(plan.RestartPath).StartsWith(plan.InstallRoot + Path.DirectorySeparatorChar, UpdatePaths.Comparison)
            || !Path.GetFullPath(plan.PreviousRestartPath).StartsWith(plan.InstallRoot + Path.DirectorySeparatorChar, UpdatePaths.Comparison))
            throw new InvalidDataException("重启程序不在安装目录中。");
        foreach (var relative in plan.Deleted)
            if (!UpdatePaths.IsManaged(relative) || plan.Manifest.Files.Any(f => f.Path.Equals(relative, UpdatePaths.Comparison)))
                throw new InvalidDataException($"删除文件不符合更新清单：{relative}");
        foreach (var directory in plan.EmptyDirectories)
            if (!UpdatePaths.IsManaged(directory) || !directory.Split('/').Any(p => p.EndsWith(".dist-info", StringComparison.OrdinalIgnoreCase))
                || plan.Manifest.Files.Any(f => f.Path.StartsWith(directory + "/", UpdatePaths.Comparison)))
                throw new InvalidDataException($"空目录不符合更新清单：{directory}");
        foreach (var file in plan.Manifest.Files) _ = plan.Target(file.Path);
        var paths = plan.Manifest.Files.Select(f => f.Path).Concat(plan.Deleted).ToArray();
        UpdatePaths.EnsureDistinctOnFileSystem(plan.InstallRoot, paths.Where(p => !UpdatePaths.IsData(p)));
        UpdatePaths.EnsureDistinctOnFileSystem(plan.DataRoot, paths.Where(UpdatePaths.IsData));
    }

    private static void VerifyPythonDependencies(UpdatePlan plan)
    {
        if (!plan.Manifest.Files.Any(f => f.Path == "python-dependencies.json")) return;
        using var json = JsonDocument.Parse(File.ReadAllText(plan.Target("python-dependencies.json")));
        var python = Path.Combine(plan.InstallRoot, "python");
        if (!Directory.Exists(python)) throw new InvalidDataException("Python 环境缺失。");
        var metadata = Directory.EnumerateFiles(python, "METADATA", UpdatePaths.DependencyScanOptions)
            .Select(p => Path.GetRelativePath(plan.InstallRoot, p).Replace('\\', '/'))
            .Where(p => p.EndsWith(".dist-info/METADATA", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var package in json.RootElement.GetProperty("packages").EnumerateObject())
        {
            var normalize = static (string name) => name.Replace('-', '_').Replace('.', '_').ToLowerInvariant();
            var expected = package.Value.GetString();
            // importlib.metadata discovers *.dist-info directories even when
            // METADATA is missing. An empty old directory can mask the new version.
            var candidates = Directory.EnumerateDirectories(python, "*.dist-info", UpdatePaths.DependencyScanOptions)
                .Count(directory =>
                {
                    var name = Path.GetFileName(directory);
                    var delimiter = name.IndexOf('-');
                    return delimiter > 0 && normalize(name[..delimiter]) == normalize(package.Name);
                });
            var versions = metadata.Select(p =>
            {
                var lines = File.ReadAllLines(plan.Target(p));
                var name = lines.FirstOrDefault(l => l.StartsWith("Name: "))?[6..];
                var version = lines.FirstOrDefault(l => l.StartsWith("Version: "))?[9..];
                return name != null && normalize(name) == normalize(package.Name) ? version : null;
            }).Where(v => v != null).ToArray();
            if (candidates != 1 || versions.Length != 1 || versions[0] != expected)
                throw new InvalidDataException($"Python 依赖未配套或残留旧版本：{package.Name} {expected}");
        }
    }

    private static void AtomicWrite(string target, Action<string> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".pending_update";
        try
        {
            Retry(() =>
            {
                if (File.Exists(temporary) || new FileInfo(temporary).LinkTarget != null) File.Delete(temporary);
                write(temporary);
                if (new FileInfo(temporary).LinkTarget == null) File.SetAttributes(temporary, FileAttributes.Normal);
                if (File.Exists(target) && new FileInfo(target).LinkTarget == null) File.SetAttributes(target, FileAttributes.Normal);
                File.Move(temporary, target, true);
            });
        }
        finally { DeleteFile(temporary); }
    }

    private static void DeleteFile(string path)
    {
        var link = new FileInfo(path).LinkTarget;
        if (link == null && !File.Exists(path)) return;
        if (link == null) File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    private static void PruneEmptyParents(string path, string root)
    {
        for (var directory = Path.GetDirectoryName(path); directory != null && directory != root; directory = Path.GetDirectoryName(directory))
        {
            if (!directory.StartsWith(root + Path.DirectorySeparatorChar, UpdatePaths.Comparison))
                throw new InvalidDataException("清理目录越界。");
            if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any()) break;
            Directory.Delete(directory);
        }
    }

    private static void Retry(Action action)
    {
        for (var attempt = 0; ; attempt++)
            try { action(); return; }
            catch (Exception ex) when (attempt < 4 && ex is IOException or UnauthorizedAccessException)
            { Thread.Sleep(250 * (attempt + 1)); }
    }

    private static void Save(string path, UpdateJournal journal) => AtomicWrite(path,
        target => File.WriteAllText(target, JsonSerializer.Serialize(journal, UpdateManifest.JsonOptions)));
}

public sealed class UpdateJournal
{
    public UpdatePlan Plan { get; set; } = new();
    public List<UpdateRecord> Records { get; set; } = [];
    public bool Committed { get; set; }
}

public sealed class UpdateRecord
{
    public string Path { get; set; } = "";
    public string Backup { get; set; } = "";
    public bool Existed { get; set; }
    public int Mode { get; set; }
    public string? LinkTarget { get; set; }
    public int Attributes { get; set; }
    public bool IsDirectory { get; set; }
}
