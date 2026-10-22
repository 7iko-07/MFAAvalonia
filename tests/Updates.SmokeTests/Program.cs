using MFAUpdater;
using System.Text.Json;

var passed = 0;
void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
    passed++;
}
void Reject(Action action, string message)
{
    try { action(); } catch { passed++; return; }
    throw new Exception(message);
}
var root = Path.Combine(Path.GetTempPath(), "mfa-update-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    UpdatePlan Fixture(string name)
    {
        var directory = Path.Combine(root, name);
        var installed = Path.Combine(directory, "installed");
        var payload = Path.Combine(directory, "payload");
        Directory.CreateDirectory(installed);
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(installed, "MFAAvalonia.exe"), "old executable");
        File.WriteAllText(Path.Combine(installed, "old.txt"), "obsolete");
        Directory.CreateDirectory(Path.Combine(installed, "config"));
        File.WriteAllText(Path.Combine(installed, "config", "user.json"), "user data");
        File.WriteAllText(Path.Combine(payload, "MFAAvalonia.exe"), "new executable");
        File.WriteAllText(Path.Combine(payload, "new.txt"), "new file");
        var files = Directory.GetFiles(payload).Select(p => UpdateFile.FromFile(payload, p)).ToList();
        return new UpdatePlan
        {
            SourceRoot = payload, InstallRoot = installed, DataRoot = installed,
            BackupRoot = Path.Combine(directory, "rollback"),
            RestartPath = Path.Combine(installed, "MFAAvalonia.exe"),
            PreviousRestartPath = Path.Combine(installed, "MFAAvalonia.exe"),
            Manifest = new UpdateManifest { Version = "v2", Files = files, Payload = files.Select(f => f.Path).ToList() },
            Deleted = ["old.txt"]
        };
    }
    var plan = Fixture("success");
    new TransactionalUpdate().Apply(plan);
    Check(File.ReadAllText(plan.RestartPath) == "new executable", "Executable not replaced");
    Check(!File.Exists(plan.Target("old.txt")), "Deleted file retained");
    Check(File.ReadAllText(Path.Combine(plan.InstallRoot, "config", "user.json")) == "user data", "User config changed");
    Check(UpdateManifest.Read(plan.Target(UpdateManifest.FileName)).Version == "v2", "Version ledger not committed");
    Check(!File.Exists(Path.Combine(plan.InstallRoot, TransactionalUpdate.JournalName)), "Successful transaction left a journal");

    plan = Fixture("rollback");
    Reject(() => new TransactionalUpdate(beforeWrite: i => { if (i == 3) throw new IOException("simulated interruption"); }).Apply(plan), "Failure was swallowed");
    Check(File.ReadAllText(plan.RestartPath) == "old executable", "Executable not rolled back");
    Check(File.ReadAllText(plan.Target("old.txt")) == "obsolete", "Deletion not rolled back");
    Check(!File.Exists(plan.Target("new.txt")), "New file not rolled back");
    Check(!File.Exists(plan.Target(UpdateManifest.FileName)), "Version advanced after rollback");

    plan = Fixture("corrupt");
    File.WriteAllText(Path.Combine(plan.SourceRoot, "new.txt"), "corrupt");
    Reject(() => new TransactionalUpdate().Apply(plan), "Corrupt payload accepted");
    Check(File.ReadAllText(plan.RestartPath) == "old executable", "Payload validation mutated installation");

    plan = Fixture("baseline");
    plan.Manifest.Kind = "delta";
    plan.Manifest.BaseVersion = "v1";
    plan.Manifest.BaseFiles = [UpdateFile.FromFile(plan.InstallRoot, plan.RestartPath), UpdateFile.FromFile(plan.InstallRoot, plan.Target("old.txt"))];
    plan.Manifest.Deleted = ["old.txt"];
    plan.Manifest.Validate();
    File.WriteAllText(plan.RestartPath, "locally damaged");
    Reject(() => new TransactionalUpdate().Apply(plan), "Damaged baseline accepted");
    Check(!File.Exists(plan.Target("new.txt")), "Baseline failure wrote payload");

    foreach (var path in new[] { "../outside", "/absolute", "C:/outside", "config/user.json", "agent/data.sqlite3", "agent/data.sqlite3-wal", "appsettings.json", "a\\b", "a/../b", "temp/file", "a./file" })
        Check(!UpdatePaths.IsManaged(path), "Unsafe or user-owned path accepted: " + path);
    plan = Fixture("delete-config");
    plan.Deleted.Add("config/user.json");
    Reject(() => new TransactionalUpdate().Apply(plan), "Protected deletion accepted");

    plan = Fixture("python-check");
    var dependency = Path.Combine(plan.SourceRoot, "python-dependencies.json");
    File.WriteAllText(dependency, "{\"packages\":{\"maafw\":\"5.14.2\"}}");
    plan.Manifest.Files.Add(UpdateFile.FromFile(plan.SourceRoot, dependency));
    plan.Manifest.Payload.Add("python-dependencies.json");
    Reject(() => new TransactionalUpdate().Apply(plan), "Missing Python dependency accepted");
    Check(File.ReadAllText(plan.RestartPath) == "old executable", "Dependency failure did not rollback");

    plan = Fixture("recovery");
    Directory.CreateDirectory(plan.BackupRoot);
    var backup = Path.Combine(plan.BackupRoot, "00000000");
    File.Copy(plan.RestartPath, backup);
    var journal = new UpdateJournal { Plan = plan, Records = [new UpdateRecord { Path = "MFAAvalonia.exe", Existed = true, Backup = backup, Mode = 420 }] };
    var journalPath = Path.Combine(plan.InstallRoot, TransactionalUpdate.JournalName);
    File.WriteAllText(journalPath, JsonSerializer.Serialize(journal, UpdateManifest.JsonOptions));
    File.WriteAllText(plan.RestartPath, "partial update");
    new TransactionalUpdate().Recover(journalPath);
    Check(File.ReadAllText(plan.RestartPath) == "old executable", "Interrupted update recovery failed");
    Check(!File.Exists(journalPath), "Recovered journal retained");

    plan = Fixture("committed-recovery");
    plan.PreviousRestartPath = Path.Combine(plan.InstallRoot, "previous.exe");
    journal = new UpdateJournal { Plan = plan, Committed = true };
    journalPath = Path.Combine(plan.InstallRoot, TransactionalUpdate.JournalName);
    File.WriteAllText(journalPath, JsonSerializer.Serialize(journal, UpdateManifest.JsonOptions));
    var recovered = new TransactionalUpdate().Recover(journalPath);
    Check(recovered.RestartPath == plan.RestartPath, "Committed update restarted previous application");
    Check(!File.Exists(journalPath), "Committed recovery journal retained");

    plan = Fixture("readonly-rollback");
    File.SetAttributes(plan.RestartPath, FileAttributes.ReadOnly);
    Reject(() => new TransactionalUpdate(beforeWrite: i => { if (i == 1) throw new IOException("failure after replacing readonly file"); }).Apply(plan), "Readonly rollback failure swallowed");
    Check(File.ReadAllText(plan.RestartPath) == "old executable", "Readonly file not restored");
    if (OperatingSystem.IsWindows()) Check((File.GetAttributes(plan.RestartPath) & FileAttributes.ReadOnly) != 0, "Readonly attribute not restored");
    File.SetAttributes(plan.RestartPath, FileAttributes.Normal);

    plan = Fixture("separate-data");
    plan.DataRoot = Path.Combine(root, "independent-data");
    var resource = Path.Combine(plan.SourceRoot, "resource", "test.json");
    Directory.CreateDirectory(Path.GetDirectoryName(resource)!);
    File.WriteAllText(resource, "resource");
    plan.Manifest.Files.Add(UpdateFile.FromFile(plan.SourceRoot, resource));
    plan.Manifest.Payload.Add("resource/test.json");
    new TransactionalUpdate().Apply(plan);
    Check(File.Exists(Path.Combine(plan.DataRoot, "resource", "test.json")), "Resource not routed to independent data directory");
    Check(!File.Exists(Path.Combine(plan.InstallRoot, "resource", "test.json")), "Resource written to install directory");

    plan = Fixture("duplicate-python");
    dependency = Path.Combine(plan.SourceRoot, "python-dependencies.json");
    File.WriteAllText(dependency, "{\"packages\":{\"maafw\":\"5.14.2\"}}");
    plan.Manifest.Files.Add(UpdateFile.FromFile(plan.SourceRoot, dependency));
    plan.Manifest.Payload.Add("python-dependencies.json");
    foreach (var version in new[] { "5.13.1", "5.14.2" })
    {
        var directory = Path.Combine(plan.InstallRoot, "python", "Lib", "site-packages", $"maafw-{version}.dist-info");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "METADATA"), $"Name: maafw\nVersion: {version}\n");
    }
    Reject(() => new TransactionalUpdate().Apply(plan), "Duplicate Python dependency metadata accepted");
    Check(File.ReadAllText(plan.RestartPath) == "old executable", "Duplicate dependency failure did not roll back");

    plan = Fixture("empty-directory-rollback");
    var emptyMetadata = "python/Lib/site-packages/maafw-5.12.0.dist-info";
    Directory.CreateDirectory(plan.Target(emptyMetadata));
    plan.EmptyDirectories.Add(emptyMetadata);
    dependency = Path.Combine(plan.SourceRoot, "python-dependencies.json");
    File.WriteAllText(dependency, "{\"packages\":{\"maafw\":\"5.14.2\"}}");
    plan.Manifest.Files.Add(UpdateFile.FromFile(plan.SourceRoot, dependency));
    plan.Manifest.Payload.Add("python-dependencies.json");
    Reject(() => new TransactionalUpdate().Apply(plan), "Missing dependency after empty directory cleanup accepted");
    Check(Directory.Exists(plan.Target(emptyMetadata)), "Empty metadata directory not restored on rollback");
    Check(File.ReadAllText(plan.RestartPath) == "old executable", "Empty directory cleanup failure did not roll back executable");

    plan = Fixture("platform");
    plan.Manifest.Platform = OperatingSystem.IsWindows() ? "linux-x86_64" : "win-x86_64";
    Reject(() => new TransactionalUpdate().Apply(plan), "Incorrect package platform accepted");
    Check(File.ReadAllText(plan.RestartPath) == "old executable", "Platform mismatch modified installation");

    if (args.Length == 1)
    {
        plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(args[0]), UpdateManifest.JsonOptions)!;
        new TransactionalUpdate().Apply(plan);
        foreach (var file in plan.Manifest.Files) Check(file.Matches(plan.Target(file.Path)), "Producer/updater contract mismatch: " + file.Path);
        foreach (var deleted in plan.Deleted) Check(!File.Exists(plan.Target(deleted)), "Producer deletion not applied: " + deleted);
        Check(UpdateManifest.Read(plan.Target(UpdateManifest.FileName)).Version == plan.Manifest.Version, "Producer target version not committed");
    }
    await ActionsUpdateTests.RunAsync(root, Check);
    await MirrorDependencyTests.RunAsync(root, Check);
    await ReleaseFallbackTests.RunAsync(Check);
    ArchiveCaseTests.Run(root, Check);
    DependencyLinkTests.Run(root, Check);
    Console.WriteLine($"Passed {passed} update smoke checks.");
}
finally
{
    if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        throw new Exception("Test cleanup path escaped temp");
    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        File.SetAttributes(file, FileAttributes.Normal);
    Directory.Delete(root, true);
}
