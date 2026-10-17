using System;
using System.Reflection;
using Semver;

namespace MFAAvalonia.Helper;

internal static class AppVersion
{
    // Read Core's version even when the desktop executable has been renamed.
    public static string Current { get; } = FromInformationalVersion(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    internal static string FromInformationalVersion(string? informationalVersion)
    {
        var version = informationalVersion?.Trim() ?? string.Empty;
        if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase)) version = version[1..];
        if (!SemVersion.TryParse(version, SemVersionStyles.Strict, out _)) return "v0.0.0-dev";

        var metadataStart = version.IndexOf('+');
        if (metadataStart >= 0) version = version[..metadataStart];
        return "v" + version;
    }
}
