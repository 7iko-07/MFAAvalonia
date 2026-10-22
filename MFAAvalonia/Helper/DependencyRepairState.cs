using System;
using System.IO;

namespace MFAAvalonia.Helper;

// Persist across restarts: a failed repair must not create a download/restart loop.
internal static class DependencyRepairState
{
    public static bool TryBegin(string marker)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        try
        {
            using var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(file);
            writer.Write(DateTimeOffset.UtcNow.ToString("O"));
            return true;
        }
        catch (IOException) when (File.Exists(marker)) { return false; }
    }

    public static void Clear(string marker) => File.Delete(marker);
}
