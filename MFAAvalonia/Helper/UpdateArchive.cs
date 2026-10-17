using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using MFAUpdater;

namespace MFAAvalonia.Helper;

internal static class UpdateArchive
{
    public static void ExtractZip(string archive, string destination)
    {
        Directory.CreateDirectory(destination);
        using var package = ZipFile.OpenRead(archive);
        var seen = new HashSet<string>(UpdatePaths.Comparer);
        foreach (var entry in package.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            var relative = entry.FullName;
            if (!UpdatePaths.IsManaged(relative) && relative != UpdateManifest.FileName) continue;
            if (!seen.Add(relative)) throw new InvalidDataException($"压缩包包含重复路径：{relative}");
            var target = UpdatePaths.Resolve(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // Never overwrite a case alias on case-insensitive macOS volumes.
            // Such a package cannot be represented on that filesystem.
            if (File.Exists(target)) throw new InvalidDataException($"更新暂存目录存在路径冲突，需使用区分大小写的文件系统：{relative}");
            entry.ExtractToFile(target);
            if (!OperatingSystem.IsWindows())
            {
                var mode = (entry.ExternalAttributes >> 16) & 511;
                File.SetUnixFileMode(target, (UnixFileMode)(mode == 0 ? 420 : mode));
            }
        }
    }

    // Older full packages have no manifest. ZIP symlinks were extracted as text;
    // recover their metadata so the installer recreates links, not text files.
    public static void RestoreLinkMetadata(string archive, string prefix, List<UpdateFile> files)
    {
        using var package = ZipFile.OpenRead(archive);
        foreach (var file in files)
        {
            var entry = package.GetEntry(prefix + file.Path);
            if (entry == null || ((entry.ExternalAttributes >> 16) & 0xF000) != 0xA000) continue;
            if (entry.Length > 32768) throw new InvalidDataException($"符号链接过长：{file.Path}");
            using var reader = new StreamReader(entry.Open());
            file.LinkTarget = reader.ReadToEnd();
        }
    }
}
