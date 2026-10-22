using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MFAUpdater;

public static class PythonDependencies
{
    public static void AddObsoleteMetadata(UpdatePlan plan)
    {
        var target = plan.Manifest.Files.Select(f => f.Path).ToHashSet(UpdatePaths.Comparer);
        if (!plan.IsResourceUpdate || !plan.HasCompleteFileList || !target.Contains("python-dependencies.json")) return;
        var python = Path.Combine(plan.InstallRoot, "python");
        if (!Directory.Exists(python)) return;
        foreach (var metadata in Directory.EnumerateDirectories(python, "*.dist-info", UpdatePaths.DependencyScanOptions))
        {
            var relative = Path.GetRelativePath(plan.InstallRoot, metadata).Replace('\\', '/');
            var existing = Directory.EnumerateFiles(metadata, "*", UpdatePaths.DependencyScanOptions).ToArray();
            if (existing.Length == 0 && !target.Any(p => p.StartsWith(relative + "/", UpdatePaths.Comparison)))
                plan.EmptyDirectories.AddRange(Directory.EnumerateDirectories(metadata, "*", UpdatePaths.DependencyScanOptions).Append(metadata)
                    .Select(p => Path.GetRelativePath(plan.InstallRoot, p).Replace('\\', '/')).Where(UpdatePaths.IsManaged));
            foreach (var file in existing)
            {
                var path = Path.GetRelativePath(plan.InstallRoot, file).Replace('\\', '/');
                if (UpdatePaths.IsManaged(path) && !target.Contains(path)) plan.Deleted.Add(path);
            }
        }
        plan.Deleted = plan.Deleted.Distinct(UpdatePaths.Comparer).ToList();
    }

    public static string Interpreter(string installRoot) => Path.Combine(installRoot, "python",
        OperatingSystem.IsWindows() ? "python.exe" : "bin/python3");

    public static void VerifyMetadata(string installRoot)
    {
        var manifest = Path.Combine(installRoot, "python-dependencies.json");
        if (!File.Exists(manifest)) return;
        using var json = JsonDocument.Parse(File.ReadAllText(manifest));
        var python = Path.Combine(installRoot, "python");
        if (!Directory.Exists(python)) throw new InvalidDataException("Python 环境缺失。");
        var directories = Directory.EnumerateDirectories(python, "*.dist-info", UpdatePaths.DependencyScanOptions).ToArray();
        foreach (var package in json.RootElement.GetProperty("packages").EnumerateObject())
        {
            var expected = package.Value.GetString();
            var candidates = directories.Where(directory =>
            {
                var name = Path.GetFileName(directory);
                var delimiter = name.IndexOf('-');
                return delimiter > 0 && Normalize(name[..delimiter]) == Normalize(package.Name);
            }).ToArray();
            var versions = candidates.Select(directory =>
            {
                var metadata = Path.Combine(directory, "METADATA");
                if (!File.Exists(metadata)) return "未安装/元数据缺失";
                var lines = File.ReadAllLines(metadata);
                var name = lines.FirstOrDefault(l => l.StartsWith("Name: "))?[6..];
                return name != null && Normalize(name) == Normalize(package.Name)
                    ? lines.FirstOrDefault(l => l.StartsWith("Version: "))?[9..] ?? "版本缺失"
                    : "元数据名称不匹配";
            }).ToArray();
            if (versions.Length != 1 || versions[0] != expected)
                throw new InvalidDataException($"Python 依赖版本不匹配：{package.Name}，要求 {expected}，实际 {(versions.Length == 0 ? "未安装" : string.Join(", ", versions))}。请重新下载完整资源包修复。");
        }
    }

    // Query the bundled interpreter as well: scanning metadata alone does not
    // establish which distribution Python actually selects on its sys.path.
    public static async Task VerifyInstalledAsync(string installRoot, bool requireInterpreter = true,
        CancellationToken cancellationToken = default)
    {
        var manifest = Path.Combine(installRoot, "python-dependencies.json");
        if (!File.Exists(manifest)) return; // Source projects / non-Python agents.
        VerifyMetadata(installRoot);
        var executable = Interpreter(installRoot);
        if (!File.Exists(executable))
        {
            if (requireInterpreter) throw new InvalidDataException("随包 Python 解释器缺失，请重新下载完整资源包修复。");
            return;
        }
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = installRoot,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-I", "-B", "-X", "utf8", "-c", ProbeScript, manifest })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidDataException("无法启动随包 Python 进行依赖检查。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidDataException("Python 依赖检查超时，请重新下载完整资源包修复。");
        }
        var result = (await output.ConfigureAwait(false)).Trim();
        var stderr = (await error.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0 || result != "MFA_PYTHON_OK")
            throw new InvalidDataException($"Python 依赖检查失败：{(result.Length > 0 ? result : stderr)}");
    }

    private static string Normalize(string value) => value.Replace('-', '_').Replace('.', '_').ToLowerInvariant();

    private const string ProbeScript = """
        import importlib.metadata as metadata
        import importlib.util
        import json
        import sys
        with open(sys.argv[1], encoding='utf-8') as stream:
            packages = json.load(stream)['packages']
        errors = []
        for name, expected in packages.items():
            try:
                actual = metadata.version(name)
            except metadata.PackageNotFoundError:
                actual = '未安装'
            if actual != expected:
                errors.append(f'{name}，要求 {expected}，实际 {actual}')
            module = {'maafw': 'maa', 'loguru': 'loguru'}.get(name.lower())
            if module and importlib.util.find_spec(module) is None:
                errors.append(f'{name}，模块 {module} 缺失')
        print('；'.join(errors) if errors else 'MFA_PYTHON_OK')
        sys.exit(1 if errors else 0)
        """;
}
