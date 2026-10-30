using System;

namespace MFAAvalonia.Helper;

public static class EndTaskPolicy
{
    public const string GroupName = "__mfa_end_tasks";
    public const string Pipeline = "Pipeline";
    public const string CloseEmulator = "CloseEmulator";
    public const string CloseMFA = "CloseMFA";

    public static int Order(string? action) => action switch
    {
        Pipeline => 0,
        CloseEmulator => 1,
        CloseMFA => 2,
        _ => throw new ArgumentException($"Unknown __mfa_end_action: {action}")
    };

    public static bool Conflicts(bool hasEndTasks, string? afterTask) =>
        hasEndTasks && !string.IsNullOrWhiteSpace(afterTask) && afterTask != "None";

    // Mirrors the index conventions used by the existing emulator consoles.
    // Explicit end tasks must never translate a remote/USB target or a malformed
    // port into a local emulator index.
    public static string EmulatorMode(string? name)
    {
        name ??= string.Empty;
        if (name.Contains("MuMu", StringComparison.OrdinalIgnoreCase)) return "MuMuEmulator12";
        if (name.Contains("Nox", StringComparison.OrdinalIgnoreCase)) return "Nox";
        if (name.Contains("LDPlayer", StringComparison.OrdinalIgnoreCase) || name.Contains("雷电")) return "LDPlayer";
        if (name.Contains("XYAZ", StringComparison.OrdinalIgnoreCase)) return "XYAZ";
        if (name.Contains("BlueStacks", StringComparison.OrdinalIgnoreCase)) return "BlueStacks";
        return "None";
    }

    public static bool IsLocalEmulatorAddress(string? serial, string mode)
    {
        if (string.IsNullOrWhiteSpace(serial)) return false;
        if (serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase))
            return mode is "MuMuEmulator12" or "LDPlayer" &&
                int.TryParse(serial[9..], out var console) && console >= 5554 && console <= 65534 && console % 2 == 0;
        var normalized = serial.Replace("localhost:", "127.0.0.1:", StringComparison.OrdinalIgnoreCase);
        if (!normalized.StartsWith("127.0.0.1:", StringComparison.Ordinal) ||
            !int.TryParse(normalized[10..], out var port) || port > 65535) return false;
        return mode switch
        {
            "MuMuEmulator12" => port >= 16384 || port == 7555 || port >= 5555 && port % 2 == 1,
            "LDPlayer" => port >= 5555 && port % 2 == 1,
            "Nox" => port == 62001 || port >= 62024,
            "XYAZ" => port >= 21503 && (port - 21503) % 10 == 0,
            _ => false
        };
    }

    public static string AdbDeviceKey(string? serial, string? deviceName = null)
    {
        if (string.IsNullOrWhiteSpace(serial)) return string.Empty;
        serial = serial.Trim();
        if (serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(serial[9..], out var consolePort))
            serial = $"127.0.0.1:{consolePort + 1}";
        serial = serial.Replace("localhost:", "127.0.0.1:", StringComparison.OrdinalIgnoreCase);
        var mode = EmulatorMode(deviceName);
        if (IsLocalEmulatorAddress(serial, mode) && int.TryParse(serial[10..], out var port))
        {
            var index = mode switch
            {
                "MuMuEmulator12" => port >= 16384 ? (port - 16384) / 32 : port == 7555 ? 0 : (port - 5555) / 2,
                "Nox" => port == 62001 ? 0 : port - 62024,
                "XYAZ" => (port - 21503) / 10,
                _ => (port - 5555) / 2
            };
            return $"adb:{mode}:{index}";
        }
        return "adb:" + serial;
    }
}
