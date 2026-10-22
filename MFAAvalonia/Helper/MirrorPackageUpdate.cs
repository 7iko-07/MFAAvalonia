using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MFAUpdater;

namespace MFAAvalonia.Helper;

// Mirror's changes.json describes transport, while our full manifest describes
// the final installation. Convert the two into the existing transaction format.
internal static class MirrorPackageUpdate
{
    public static UpdateManifest CreateDelta(UpdateManifest? target, string sourceRoot,
        string installRoot, string dataRoot, string baselineVersion, string targetVersion)
    {
        if (target == null || target.Kind != "full" || target.Version != targetVersion)
            throw new InvalidDataException("Mirror 增量包缺少匹配的完整目标清单，请使用完整包。");
        target.Validate();
        target.ValidatePlatform();
        using var changes = JsonDocument.Parse(File.ReadAllText(Path.Combine(sourceRoot, "changes.json")));
        var payload = ReadPaths(changes.RootElement, "added");
        payload.UnionWith(ReadPaths(changes.RootElement, "modified"));
        var deleted = ReadPaths(changes.RootElement, "deleted");
        var files = target.Files.ToDictionary(f => f.Path, UpdatePaths.Comparer);
        if (payload.Any(p => !files.ContainsKey(p)) || deleted.Any(files.ContainsKey))
            throw new InvalidDataException("Mirror 差分与目标文件清单冲突。");

        var baseline = new List<UpdateFile>();
        var actualDeleted = new List<string>();
        string Installed(string path) => UpdatePaths.Resolve(UpdatePaths.IsData(path) ? dataRoot : installRoot, path, allowFinalLink: true);
        void Remember(string relative, string path)
        {
            var file = UpdateFile.FromFile(UpdatePaths.IsData(relative) ? dataRoot : installRoot, path);
            baseline.Add(file);
        }
        foreach (var file in target.Files)
        {
            var installed = Installed(file.Path);
            if (!payload.Contains(file.Path))
            {
                if (!file.Matches(installed))
                    throw new InvalidDataException($"本地未改动文件损坏或缺失：{file.Path}");
                baseline.Add(file);
            }
            else
            {
                if (!file.Matches(UpdatePaths.Resolve(sourceRoot, file.Path, allowFinalLink: true)))
                    throw new InvalidDataException($"Mirror 改动文件校验失败：{file.Path}");
                if (File.Exists(installed) || new FileInfo(installed).LinkTarget != null)
                    Remember(file.Path, installed);
            }
        }
        foreach (var relative in deleted)
        {
            var installed = Installed(relative);
            if (!File.Exists(installed) && new FileInfo(installed).LinkTarget == null) continue;
            Remember(relative, installed);
            actualDeleted.Add(relative);
        }
        // The updater checks this again after the client exits, detecting edits
        // between planning and installation as well as a stale resource version.
        using var current = JsonDocument.Parse(File.ReadAllText(Installed("interface.json")));
        if (current.RootElement.GetProperty("version").GetString() != baselineVersion)
            throw new InvalidDataException("本地资源版本与 Mirror 增量请求不一致。");
        var result = new UpdateManifest
        {
            Version = target.Version, Platform = target.Platform, Kind = "delta",
            BaseVersion = baselineVersion, Files = target.Files, BaseFiles = baseline,
            Payload = payload.ToList(), Deleted = actualDeleted
        };
        result.Validate();
        return result;
    }

    private static HashSet<string> ReadPaths(JsonElement changes, string field)
    {
        var paths = new HashSet<string>(UpdatePaths.Comparer);
        if (!changes.TryGetProperty(field, out var array)) return paths;
        foreach (var value in array.EnumerateArray())
        {
            var path = value.GetString() ?? throw new InvalidDataException("Mirror 差分路径为空。");
            // The manifest is consumed separately; user data and caches are never
            // installed/deleted by the transaction, even if present in a diff.
            if (path.Equals(UpdateManifest.FileName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!UpdatePaths.IsManaged(path))
            {
                if (path.Contains('\\') || path.Contains(':') || path.StartsWith('/')
                    || path.Split('/').Any(p => p is "" or "." or ".."))
                    throw new InvalidDataException($"Mirror 差分路径无效：{path}");
                continue;
            }
            paths.Add(path);
        }
        return paths;
    }
}
