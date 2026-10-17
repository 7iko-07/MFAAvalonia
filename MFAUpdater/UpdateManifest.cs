using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MFAUpdater;

// Shared by the client and the independent updater. Paths are package-relative,
// never absolute; manifests describe only files owned by the distribution.
public sealed class UpdateManifest
{
    public const string FileName = "update-manifest.json";
    public int Schema { get; set; } = 1;
    public string Version { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Kind { get; set; } = "full";
    public string? BaseVersion { get; set; }
    public List<UpdateFile> Files { get; set; } = [];
    public List<UpdateFile> BaseFiles { get; set; } = [];
    public List<string> Payload { get; set; } = [];
    public List<string> Deleted { get; set; } = [];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static UpdateManifest Read(string path)
    {
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(path), JsonOptions)
                       ?? throw new InvalidDataException("更新清单为空。");
        manifest.Validate();
        return manifest;
    }

    public void Validate()
    {
        if (Schema != 1 || string.IsNullOrWhiteSpace(Version) || Kind is not ("full" or "delta"))
            throw new InvalidDataException("不支持的更新清单。");
        if (Files == null || Files.Count == 0 || BaseFiles == null || Payload == null || Deleted == null)
            throw new InvalidDataException("更新清单缺少文件列表。");
        ValidateFiles(Files);
        ValidateFiles(BaseFiles);
        var target = Files.Select(f => f.Path).ToHashSet(UpdatePaths.Comparer);
        var payload = new HashSet<string>(UpdatePaths.Comparer);
        foreach (var path in Payload)
            if (!UpdatePaths.IsManaged(path) || !target.Contains(path) || !payload.Add(path))
                throw new InvalidDataException($"无效的更新文件：{path}");
        var deleted = new HashSet<string>(UpdatePaths.Comparer);
        foreach (var path in Deleted)
            if (!UpdatePaths.IsManaged(path) || target.Contains(path) || !deleted.Add(path))
                throw new InvalidDataException($"无效的删除文件：{path}");
        if (Kind == "full" && (payload.Count != Files.Count || BaseFiles.Count != 0 || Deleted.Count != 0))
            throw new InvalidDataException("完整包清单缺少文件。");
        if (Kind == "delta")
        {
            if (string.IsNullOrWhiteSpace(BaseVersion) || BaseFiles.Count == 0)
                throw new InvalidDataException("增量包缺少基准版本。");
            var baseline = BaseFiles.ToDictionary(f => f.Path, UpdatePaths.Comparer);
            foreach (var file in Files)
                if (!payload.Contains(file.Path) && (!baseline.TryGetValue(file.Path, out var old)
                    || old.Sha256 != file.Sha256 || old.Mode != file.Mode || old.LinkTarget != file.LinkTarget))
                    throw new InvalidDataException($"增量包遗漏改动：{file.Path}");
            if (!deleted.SetEquals(baseline.Keys.Where(p => !target.Contains(p))))
                throw new InvalidDataException("增量包删除清单不完整。");
        }
    }

    private static void ValidateFiles(List<UpdateFile> files)
    {
        var seen = new HashSet<string>(UpdatePaths.Comparer);
        foreach (var file in files)
            if (file == null || !UpdatePaths.IsManaged(file.Path) || !seen.Add(file.Path) || file.Size < 0
                || file.Sha256?.Length != 64 || !file.Sha256.All(Uri.IsHexDigit)
                || file.Mode is < 0 or > 511)
                throw new InvalidDataException($"无效的文件清单：{file?.Path}");
        foreach (var file in files.Where(f => f.LinkTarget != null))
        {
            var link = file.LinkTarget!;
            var bytes = Encoding.UTF8.GetBytes(link);
            if (bytes.Length != file.Size || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"符号链接校验信息不一致：{file.Path}");
            if (Path.IsPathRooted(link) || link.Contains('\\') || link.Contains(':'))
                throw new InvalidDataException($"符号链接越界：{file.Path}");
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mfa-link-validation"));
            var destination = Path.GetFullPath(Path.Combine(root, Path.GetDirectoryName(file.Path) ?? "", link));
            if (!destination.StartsWith(root + Path.DirectorySeparatorChar, UpdatePaths.Comparison)
                || !UpdatePaths.IsManaged(Path.GetRelativePath(root, destination).Replace('\\', '/')))
                throw new InvalidDataException($"符号链接越界：{file.Path}");
        }
        // A file cannot also be a parent directory of another file.
        foreach (var path in seen)
            for (var parent = path.LastIndexOf('/'); parent > 0; parent = path.LastIndexOf('/', parent - 1))
                if (seen.Contains(path[..parent]))
                    throw new InvalidDataException($"文件路径冲突：{path}");
    }

    public void ValidatePlatform()
    {
        if (string.IsNullOrEmpty(Platform)) return; // Older packages have no platform manifest.
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var normalized = Platform.Replace("macos-", "osx-").Replace("-x86_64", "-x64").Replace("-aarch64", "-arm64");
        if (normalized != $"{os}-{arch}") throw new InvalidDataException($"更新包平台不匹配：{Platform}，当前 {os}-{arch}。");
    }

    public UpdateManifest AsInstalled() => new()
    {
        Version = Version, Platform = Platform, Files = Files,
        Payload = Files.Select(f => f.Path).ToList()
    };
}

public sealed class UpdateFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public int Mode { get; set; } = 420;
    public string? LinkTarget { get; set; }

    public bool Matches(string path)
    {
        if (LinkTarget != null)
        {
            var actual = new FileInfo(path).LinkTarget;
            if (actual != null) return actual == LinkTarget;
            // ZIP extractors represent symlinks as their UTF-8 link text.
        }
        if (!File.Exists(path) || new FileInfo(path).Length != Size) return false;
        if (!OperatingSystem.IsWindows() && ((int)File.GetUnixFileMode(path) & 511) != Mode) return false;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(Sha256, StringComparison.OrdinalIgnoreCase);
    }

    public static UpdateFile FromFile(string root, string path)
    {
        var link = new FileInfo(path).LinkTarget;
        if (link != null)
        {
            var bytes = Encoding.UTF8.GetBytes(link);
            return new UpdateFile
            {
                Path = System.IO.Path.GetRelativePath(root, path).Replace('\\', '/'),
                Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                Mode = 511, LinkTarget = link
            };
        }
        using var stream = File.OpenRead(path);
        return new UpdateFile
        {
            Path = System.IO.Path.GetRelativePath(root, path).Replace('\\', '/'),
            Size = stream.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
            Mode = OperatingSystem.IsWindows() ? 420 : (int)File.GetUnixFileMode(path) & 511
        };
    }
}

public static class UpdatePaths
{
    public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // Dependency discovery must not follow aliases (lib64 -> lib), junctions,
    // external directories or cycles. Keep hidden metadata visible and report
    // access errors rather than silently treating an incomplete scan as valid.
    public static EnumerationOptions DependencyScanOptions => new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = false,
        MatchType = MatchType.Simple,
        MatchCasing = MatchCasing.PlatformDefault
    };

    public static void EnsureDistinctOnFileSystem(string root, IEnumerable<string> paths)
    {
        var names = paths.Distinct(StringComparer.Ordinal).ToArray();
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Length) return;
        if (OperatingSystem.IsWindows()) throw new InvalidDataException("目标文件路径存在大小写冲突。");
        // macOS volumes may be case-sensitive or case-insensitive. Check the
        // destination before any package files are changed, including deletions.
        var probe = Path.Combine(root, ".mfa-update-case-" + Guid.NewGuid().ToString("N") + "-a");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write)) { }
            if (File.Exists(probe[..^1] + "A"))
                throw new InvalidDataException("更新包包含大小写不同的独立文件，安装目录需要区分大小写的文件系统。");
        }
        finally { File.Delete(probe); }
    }

    public static bool IsData(string path) => path.StartsWith("resource/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("agent/", StringComparison.OrdinalIgnoreCase)
        || path is "interface.json" or "interface.jsonc" or UpdateManifest.FileName;

    public static bool IsManaged(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':') || path.StartsWith('/')
            || path.Split('/').Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' '))) return false;
        var parts = path.Split('/');
        if (parts.Any(p => new[] { "config", "logs", "debug", "temp", "backup", "__pycache__" }
                .Contains(p, StringComparer.OrdinalIgnoreCase))) return false;
        if (path.EndsWith(".backupMFA", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".pending_update", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".sqlite3-wal", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".sqlite3-shm", StringComparison.OrdinalIgnoreCase)
            || path.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase)
            || path.Equals(UpdateManifest.FileName, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(".mfa-update", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    public static string Resolve(string root, string relative, bool allowFinalLink = false)
    {
        if (!IsManaged(relative) && relative != UpdateManifest.FileName)
            throw new InvalidDataException($"更新路径无效：{relative}");
        var basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(basePath, relative));
        if (!fullPath.StartsWith(basePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException($"更新路径越界：{relative}");
        // Do not follow symlinks/junctions into user directories outside the package.
        for (var cursor = allowFinalLink ? Path.GetDirectoryName(fullPath) : fullPath; cursor != null && cursor.Length >= basePath.Length - 1; cursor = Path.GetDirectoryName(cursor))
            if ((File.Exists(cursor) || Directory.Exists(cursor)) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"更新路径包含符号链接：{relative}");
        return fullPath;
    }
}

public sealed class UpdatePlan
{
    public string SourceRoot { get; set; } = "";
    public string InstallRoot { get; set; } = "";
    public string DataRoot { get; set; } = "";
    public string BackupRoot { get; set; } = "";
    public string RestartPath { get; set; } = "";
    public string PreviousRestartPath { get; set; } = "";
    public string UpdaterPath { get; set; } = "";
    public int ParentPid { get; set; }
    public long ParentStartTimeUtcTicks { get; set; }
    public bool IsResourceUpdate { get; set; } = true;
    public bool HasCompleteFileList { get; set; } = true;
    public UpdateManifest Manifest { get; set; } = new();
    public List<string> Deleted { get; set; } = [];
    public List<string> EmptyDirectories { get; set; } = [];

    public string Target(string relative) => UpdatePaths.Resolve(UpdatePaths.IsData(relative) ? DataRoot : InstallRoot, relative, allowFinalLink: true);
}
