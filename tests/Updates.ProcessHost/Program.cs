using System.Diagnostics;
using System.Text.Json;

if (args.Length == 4 && args[0] == "--wait")
{
    // Hold a real file open until the test releases this simulated application.
    using var heldFile = new FileStream(args[3], FileMode.Open, FileAccess.Read, FileShare.Read);
    using var process = Process.GetCurrentProcess();
    File.WriteAllText(args[1] + ".tmp", JsonSerializer.Serialize(new
    {
        pid = process.Id,
        startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks
    }));
    File.Move(args[1] + ".tmp", args[1], true);
    var deadline = DateTime.UtcNow.AddSeconds(60);
    while (!File.Exists(args[2]))
    {
        if (DateTime.UtcNow >= deadline) return 2;
        await Task.Delay(25);
    }
    return 0;
}

// No arguments means the updater restarted the test application.
using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "interface.json")));
var marker = Path.Combine(AppContext.BaseDirectory, "config", "restarted.json");
Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
File.WriteAllText(marker + ".tmp", JsonSerializer.Serialize(new
{
    version = manifest.RootElement.GetProperty("version").GetString(),
    pid = Environment.ProcessId
}));
File.Move(marker + ".tmp", marker, true);
return 0;
