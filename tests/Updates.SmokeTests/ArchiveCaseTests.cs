using MFAAvalonia.Helper;
using MFAUpdater;
using System.IO.Compression;

internal static class ArchiveCaseTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        var zip = Path.Combine(root, "unix-full.zip");
        using (var package = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "python/share/terminfo/2/2621A", "python/share/terminfo/2/2621a" })
            {
                using var writer = new StreamWriter(package.CreateEntry(name).Open());
                writer.Write(name);
            }
        }
        var destination = Path.Combine(root, "case-payload");
        var caseSensitive = !OperatingSystem.IsWindows();
        if (caseSensitive)
            try { UpdatePaths.EnsureDistinctOnFileSystem(root, ["a", "A"]); }
            catch (InvalidDataException) { caseSensitive = false; }
        if (!caseSensitive)
        {
            try { UpdateArchive.ExtractZip(zip, destination); throw new Exception("Case collision on insensitive filesystem accepted"); }
            catch (InvalidDataException) { check(true, "Unrepresentable case collisions rejected"); }
        }
        else
        {
            // Exercise extraction, validation, writes, deletion and rollback on
            // a native Unix filesystem when running this suite on Linux/macOS.
            UpdateArchive.ExtractZip(zip, destination);
            var files = Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Select(p => UpdateFile.FromFile(destination, p)).ToList();
            var installed = Path.Combine(root, "case-install");
            Directory.CreateDirectory(installed);
            var plan = new UpdatePlan { SourceRoot = destination, InstallRoot = installed, DataRoot = installed,
                BackupRoot = Path.Combine(root, "case-backup"), RestartPath = Path.Combine(installed, "app"), PreviousRestartPath = Path.Combine(installed, "app"),
                Manifest = new UpdateManifest { Version = "v2", Files = files, Payload = files.Select(f => f.Path).ToList() } };
            new TransactionalUpdate().Apply(plan);
            check(files.All(f => f.Matches(plan.Target(f.Path))), "Case-distinct files lost during install");
            var upper = files.Single(f => f.Path.EndsWith('A'));
            var lower = files.Single(f => f.Path.EndsWith('a'));
            plan.BackupRoot = Path.Combine(root, "case-delete-backup");
            plan.Manifest = new UpdateManifest { Version = "v3", Files = [upper], Payload = [upper.Path] };
            plan.Deleted = [lower.Path];
            try { new TransactionalUpdate(beforeWrite: i => { if (i == 2) throw new IOException("rollback"); }).Apply(plan); }
            catch (IOException) { }
            check(upper.Matches(plan.Target(upper.Path)) && lower.Matches(plan.Target(lower.Path)), "Case-distinct rollback failed");
            plan.BackupRoot = Path.Combine(root, "case-delete-success");
            new TransactionalUpdate().Apply(plan);
            check(File.Exists(plan.Target(upper.Path)) && !File.Exists(plan.Target(lower.Path)), "Case-distinct deletion removed wrong file");
        }

        var links = Path.Combine(root, "old-unix-links.zip");
        using (var package = ZipFile.Open(links, ZipArchiveMode.Create))
        {
            var binary = package.CreateEntry("python/bin/python3.12");
            binary.ExternalAttributes = (0x8000 | 493) << 16;
            using (var binaryWriter = new StreamWriter(binary.Open())) binaryWriter.Write("python executable");
            var entry = package.CreateEntry("python/bin/python3");
            entry.ExternalAttributes = (0xA000 | 511) << 16;
            using var writer = new StreamWriter(entry.Open());
            writer.Write("python3.12");
        }
        var linksRoot = Path.Combine(root, "old-links-payload");
        UpdateArchive.ExtractZip(links, linksRoot);
        var linkFiles = Directory.GetFiles(linksRoot, "*", SearchOption.AllDirectories).Select(p => UpdateFile.FromFile(linksRoot, p)).ToList();
        UpdateArchive.RestoreLinkMetadata(links, "", linkFiles);
        var manifest = new UpdateManifest { Version = "v2", Files = linkFiles, Payload = linkFiles.Select(f => f.Path).ToList() };
        manifest.Validate();
        var linkFile = linkFiles.Single(f => f.Path == "python/bin/python3");
        check(linkFile.LinkTarget == "python3.12", "Legacy full ZIP lost symlink metadata");
        if (!OperatingSystem.IsWindows())
        {
            var installed = Path.Combine(root, "link-install");
            Directory.CreateDirectory(installed);
            var plan = new UpdatePlan { SourceRoot = linksRoot, InstallRoot = installed, DataRoot = installed,
                BackupRoot = Path.Combine(root, "link-backup"), RestartPath = Path.Combine(installed, "app"), PreviousRestartPath = Path.Combine(installed, "app"),
                Manifest = manifest };
            new TransactionalUpdate().Apply(plan);
            check(new FileInfo(plan.Target(linkFile.Path)).LinkTarget == "python3.12", "Legacy Python link installed as ordinary text");
        }
        linkFile.LinkTarget = "../../../escape";
        try { manifest.Validate(); throw new Exception("Unsafe link accepted"); }
        catch (InvalidDataException) { check(true, "Unsafe legacy links rejected"); }
    }
}
