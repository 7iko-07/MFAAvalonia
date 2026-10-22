using MFAAvalonia.Helper;
using MFAUpdater;
using System.Text.Json;

internal static class MirrorDependencyTests
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        void Reject(Action action, string reason)
        {
            try { action(); } catch (InvalidDataException) { check(true, reason); return; }
            throw new Exception(reason);
        }
        void Write(string directory, string relative, string content)
        {
            var path = Path.Combine(directory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
        var directory = Path.Combine(root, "mirror-dependencies");
        var installed = Path.Combine(directory, "installed");
        var source = Path.Combine(directory, "source");
        var complete = Path.Combine(directory, "complete");
        const string oldMetadata = "python/Lib/site-packages/maafw-5.14.2.dist-info/METADATA";
        const string newMetadata = "python/Lib/site-packages/maafw-5.14.3.dist-info/METADATA";
        Write(installed, "interface.json", "{\"version\":\"v1\"}");
        Write(installed, "app.exe", "old");
        Write(installed, "resource/unchanged.txt", "unchanged");
        Write(installed, "resource/removed.txt", "obsolete");
        Write(installed, "config/user.json", "user configuration");
        Write(installed, oldMetadata, "Name: MaaFw\nVersion: v5.14.2\n");
        Write(installed, "python-dependencies.json", "{\"packages\":{\"MaaFw\":\"v5.14.2\"}}");
        Write(complete, "interface.json", "{\"version\":\"v2\"}");
        Write(complete, "app.exe", "new");
        Write(complete, "resource/unchanged.txt", "unchanged");
        Write(complete, newMetadata, "Name: MaaFw\nVersion: v5.14.3\n");
        Write(complete, "python-dependencies.json", "{\"packages\":{\"MaaFw\":\"v5.14.3\"}}");
        var files = Directory.GetFiles(complete, "*", SearchOption.AllDirectories).Select(p => UpdateFile.FromFile(complete, p)).ToList();
        var full = new UpdateManifest { Version = "v2", Files = files, Payload = files.Select(f => f.Path).ToList() };
        foreach (var file in files.Where(f => f.Path != "resource/unchanged.txt"))
            Write(source, file.Path, File.ReadAllText(Path.Combine(complete, file.Path)));
        var changes = JsonSerializer.Serialize(new
        {
            added = new[] { newMetadata },
            modified = new[] { "app.exe", "interface.json", "python-dependencies.json", UpdateManifest.FileName },
            deleted = new[] { oldMetadata, "resource/removed.txt", "config/user.json" },
            deleted_dir = new[] { "python/Lib/site-packages/maafw-5.14.2.dist-info" }
        });
        Write(source, "changes.json", changes);
        UpdateManifest Delta() => MirrorPackageUpdate.CreateDelta(full, source, installed, installed, "v1", "v2");
        var delta = Delta();
        check(!delta.Payload.Contains("resource/unchanged.txt"), "Mirror delta requires unchanged file in archive");
        check(!delta.Deleted.Contains("config/user.json"), "Mirror delta deleted user configuration");
        check(delta.Files.Count == full.Files.Count && full.Kind == "full", "Lost complete manifest or mutated input");
        Reject(() => MirrorPackageUpdate.CreateDelta(null, source, installed, installed, "v1", "v2"), "Legacy diff without full manifest accepted");
        Reject(() => MirrorPackageUpdate.CreateDelta(full, source, installed, installed, "v1", "v3"), "Wrong target version accepted");
        Reject(() => MirrorPackageUpdate.CreateDelta(full, source, installed, installed, "v0", "v2"), "Wrong local version accepted");
        Write(installed, "resource/unchanged.txt", "corrupt");
        Reject(() => Delta(), "Damaged unchanged file accepted");
        Write(installed, "resource/unchanged.txt", "unchanged");
        Write(source, "app.exe", "corrupt");
        Reject(() => Delta(), "Damaged patch file accepted");
        Write(source, "app.exe", "new");
        Write(source, "changes.json", "{\"deleted\":[\"../outside\"]}");
        Reject(() => Delta(), "Unsafe deletion accepted");
        Write(source, "changes.json", changes);
        var plan = new UpdatePlan
        {
            SourceRoot = source, InstallRoot = installed, DataRoot = installed,
            BackupRoot = Path.Combine(directory, "backup"), RestartPath = Path.Combine(installed, "app.exe"),
            PreviousRestartPath = Path.Combine(installed, "app.exe"), Manifest = Delta(), Deleted = delta.Deleted.ToList()
        };
        new TransactionalUpdate().Apply(plan);
        check(File.ReadAllText(plan.Target("interface.json")) == "{\"version\":\"v2\"}", "Mirror update did not commit version");
        check(!Directory.Exists(Path.GetDirectoryName(plan.Target(oldMetadata))), "Old metadata directory survived Mirror update");
        check(File.ReadAllText(plan.Target("resource/unchanged.txt")) == "unchanged", "Unchanged resource was altered");
        check(File.ReadAllText(Path.Combine(installed, "config/user.json")) == "user configuration", "User data changed");
        PythonDependencies.VerifyMetadata(installed);

        // Legacy full overlay: both versions coexist. The same cleanup used by
        // the client must remove old and empty metadata and remain rollbackable.
        Write(installed, oldMetadata, "Name: MaaFw\nVersion: v5.14.2\n");
        var empty = "python/Lib/site-packages/maafw-5.12.0.dist-info";
        Directory.CreateDirectory(Path.Combine(installed, empty));
        Reject(() => PythonDependencies.VerifyMetadata(installed), "Duplicate Python versions accepted");
        plan.SourceRoot = complete;
        plan.Manifest = full;
        plan.Deleted = [];
        plan.BackupRoot = Path.Combine(directory, "full-backup");
        PythonDependencies.AddObsoleteMetadata(plan);
        check(plan.Deleted.Contains(oldMetadata) && plan.EmptyDirectories.Contains(empty), "Full repair failed to plan stale metadata cleanup");
        new TransactionalUpdate().Apply(plan);
        PythonDependencies.VerifyMetadata(installed);
        check(!Directory.Exists(Path.Combine(installed, empty)), "Empty stale metadata directory survived repair");

        // Dependency manifest is not in this patch, but the installed copy must
        // still be checked; version advancement must roll back on failure.
        Write(installed, oldMetadata, "Name: MaaFw\nVersion: v5.14.2\n");
        Write(source, "interface.json", "{\"version\":\"v3\"}");
        var onlyInterface = UpdateFile.FromFile(source, Path.Combine(source, "interface.json"));
        plan.SourceRoot = source;
        plan.Manifest = new UpdateManifest { Version = "v3", Files = [onlyInterface], Payload = [onlyInterface.Path] };
        plan.HasCompleteFileList = false;
        plan.Deleted = [];
        plan.EmptyDirectories = [];
        plan.BackupRoot = Path.Combine(directory, "failed-patch");
        Reject(() => new TransactionalUpdate().Apply(plan), "Unchanged dependency manifest bypassed verification");
        check(File.ReadAllText(plan.Target("interface.json")) == "{\"version\":\"v2\"}", "Dependency failure did not roll back resource version");

        var marker = Path.Combine(directory, "repair.attempt");
        var winners = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => DependencyRepairState.TryBegin(marker))));
        check(winners.Count(x => x) == 1, "Concurrent agents scheduled multiple repairs");
        check(!DependencyRepairState.TryBegin(marker), "Repair repeated after a simulated restart");
        DependencyRepairState.Clear(marker);
        check(DependencyRepairState.TryBegin(marker), "Healthy installation did not reset repair state");

        await TestRealPythonAsync(directory, check);
    }

    private static async Task TestRealPythonAsync(string root, Action<bool, string> check)
    {
        // Optional Windows embedded-Python fixture. Copy only runtime files;
        // never alter the developer's actual installation or site-packages.
        var runtime = Environment.GetEnvironmentVariable("MFA_TEST_PYTHON_ROOT");
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(runtime)) return;
        var installed = Path.Combine(root, "python-probe");
        var python = Path.Combine(installed, "python");
        var site = Path.Combine(python, "Lib", "site-packages");
        Directory.CreateDirectory(site);
        foreach (var file in Directory.GetFiles(runtime))
            if (Path.GetExtension(file) is ".exe" or ".dll" or ".zip" or ".pyd")
                File.Copy(file, Path.Combine(python, Path.GetFileName(file)));
        var stdlib = Directory.GetFiles(python, "python*.zip").Single();
        File.WriteAllText(Path.ChangeExtension(stdlib, "._pth"), Path.GetFileName(stdlib) + "\n.\nLib/site-packages\nimport site\n");
        Directory.CreateDirectory(Path.Combine(site, "maa"));
        File.WriteAllText(Path.Combine(site, "maa", "__init__.py"), "");
        var metadata = Path.Combine(site, "maafw-5.14.3.dist-info");
        Directory.CreateDirectory(metadata);
        File.WriteAllText(Path.Combine(metadata, "METADATA"), "Name: MaaFw\nVersion: v5.14.3\n");
        File.WriteAllText(Path.Combine(installed, "python-dependencies.json"), "{\"packages\":{\"MaaFw\":\"v5.14.3\"}}");
        await PythonDependencies.VerifyInstalledAsync(installed);
        check(!Directory.Exists(Path.Combine(site, "maa", "__pycache__")), "Dependency probe wrote Python bytecode");
        Directory.Move(Path.Combine(site, "maa"), Path.Combine(site, "missing-maa"));
        var rejected = false;
        try { await PythonDependencies.VerifyInstalledAsync(installed); }
        catch (InvalidDataException ex) { rejected = ex.Message.Contains("模块 maa 缺失"); }
        check(rejected, "Valid metadata masked missing Python module");
    }
}
