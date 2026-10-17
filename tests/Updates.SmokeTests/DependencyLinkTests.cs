using System.Diagnostics;
using MFAUpdater;

internal static class DependencyLinkTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "dependency-links");
        var installed = Path.Combine(directory, "installed");
        var source = Path.Combine(directory, "source");
        var external = Path.Combine(directory, "external");
        var metadata = "python/lib/site-packages/demo-1.0.dist-info/METADATA";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(installed, metadata))!);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(source, metadata))!);
        Directory.CreateDirectory(Path.Combine(external, "other-9.0.dist-info"));
        var sentinel = Path.Combine(external, "other-9.0.dist-info", "METADATA");
        File.WriteAllText(sentinel, "external data");
        File.WriteAllText(Path.Combine(installed, metadata), "Name: demo\nVersion: 1.0\n");
        File.WriteAllText(Path.Combine(source, metadata), "Name: demo\nVersion: 1.0\n");
        File.WriteAllText(Path.Combine(installed, "app.exe"), "old app");
        File.WriteAllText(Path.Combine(source, "app.exe"), "new app");
        File.WriteAllText(Path.Combine(source, "python-dependencies.json"), "{\"packages\":{\"demo\":\"1.0\"}}");
        var links = new List<string>();
        void Link(string path, string target)
        {
            if (OperatingSystem.IsWindows())
            {
                // Junction creation does not require Windows symlink privileges.
                var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                start.Environment["MFA_TEST_LINK"] = path;
                start.Environment["MFA_TEST_TARGET"] = target;
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-NonInteractive");
                start.ArgumentList.Add("-Command");
                start.ArgumentList.Add("New-Item -ItemType Junction -Path $env:MFA_TEST_LINK -Target $env:MFA_TEST_TARGET -ErrorAction Stop | Out-Null");
                using var process = Process.Start(start)!;
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) throw new IOException(error);
            }
            else Directory.CreateSymbolicLink(path, target);
            links.Add(path);
        }
        try
        {
            var python = Path.Combine(installed, "python");
            Link(Path.Combine(python, "lib64"), Path.Combine(python, "lib"));
            Link(Path.Combine(python, "loop"), python);
            Link(Path.Combine(python, "external"), external);
            var metadataRoot = Path.GetDirectoryName(Path.Combine(installed, metadata))!;
            Link(Path.Combine(metadataRoot, "external"), external);
            if (OperatingSystem.IsWindows()) File.SetAttributes(Path.Combine(installed, metadata), FileAttributes.Hidden);
            var discovered = Directory.EnumerateDirectories(python, "*.dist-info", UpdatePaths.DependencyScanOptions).ToArray();
            check(discovered.SequenceEqual(new[] { metadataRoot }), "Dependency scan followed a directory link");
            var expectedMetadata = Path.GetFullPath(Path.Combine(installed, metadata));
            check(Directory.EnumerateFiles(python, "METADATA", UpdatePaths.DependencyScanOptions).Single() == expectedMetadata, "Metadata scan missed hidden metadata or followed a link");
            check(Directory.EnumerateFiles(metadataRoot, "*", UpdatePaths.DependencyScanOptions).Single() == expectedMetadata, "Cleanup traversed an external directory");
            check(!Directory.EnumerateDirectories(metadataRoot, "*", UpdatePaths.DependencyScanOptions).Any(), "Cleanup included a linked directory");
            var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Select(p => UpdateFile.FromFile(source, p)).ToList();
            var plan = new UpdatePlan { SourceRoot = source, InstallRoot = installed, DataRoot = installed,
                BackupRoot = Path.Combine(directory, "backup"), RestartPath = Path.Combine(installed, "app.exe"), PreviousRestartPath = Path.Combine(installed, "app.exe"),
                Manifest = new UpdateManifest { Version = "v2", Files = files, Payload = files.Select(f => f.Path).ToList() } };
            new TransactionalUpdate().Apply(plan);
            check(File.ReadAllText(plan.RestartPath) == "new app", "Directory alias prevented update");
            check(UpdateManifest.Read(plan.Target(UpdateManifest.FileName)).Version == "v2", "Update did not commit");
            check(links.All(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0), "Update changed a directory link");
            check(File.ReadAllText(sentinel) == "external data", "Update touched external linked data");

            // Real duplicate distributions must still fail and roll back.
            Directory.CreateDirectory(Path.Combine(python, "lib", "site-packages", "demo-0.9.dist-info"));
            File.WriteAllText(Path.Combine(source, "app.exe"), "bad update");
            plan.Manifest.Files = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Select(p => UpdateFile.FromFile(source, p)).ToList();
            plan.BackupRoot = Path.Combine(directory, "rollback");
            var rejected = false;
            try { new TransactionalUpdate().Apply(plan); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected && File.ReadAllText(plan.RestartPath) == "new app", "Real stale distribution was ignored or rollback failed");
        }
        finally
        {
            // Remove links themselves, non-recursively, before outer test cleanup.
            foreach (var path in links.AsEnumerable().Reverse())
            {
                if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, UpdatePaths.Comparison))
                    throw new InvalidDataException("Test link escaped fixture");
                Directory.Delete(path);
            }
        }
    }
}
