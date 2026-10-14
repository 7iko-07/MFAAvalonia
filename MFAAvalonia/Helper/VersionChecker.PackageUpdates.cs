using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAUpdater;
using Newtonsoft.Json.Linq;
using SukiUI.Toasts;

namespace MFAAvalonia.Helper;

public static partial class VersionChecker
{
    private static async Task UpdateResourceTransactionalAsync(bool isGithub, bool closeDialog, bool noDialog,
        Action? action, string currentVersion, string? localPackagePath)
    {
        shouldShowToast = false;
        Instances.RootViewModel.SetUpdating(true);
        ISukiToast? toast = null;
        ProgressBar? progress = null;
        TextBlock? status = null;
        try
        {
            toast = await DispatcherHelper.RunOnMainThreadAsync(() =>
            {
                progress = new ProgressBar { Height = 10, ShowProgressText = true };
                status = new TextBlock { Text = LangKeys.GettingLatestResources.ToLocalization() };
                var content = new StackPanel();
                content.Children.Add(status);
                content.Children.Add(progress);
                return Instances.ToastManager.CreateToast().WithTitle(LangKeys.UpdateResource.ToLocalization()).WithContent(content).Queue();
            });
            var localVersion = string.IsNullOrWhiteSpace(currentVersion) ? MaaProcessor.Interface?.Version ?? "" : currentVersion;
            if (string.IsNullOrWhiteSpace(localVersion)) throw new InvalidDataException("无法读取当前资源版本。");
            var isLocal = !string.IsNullOrWhiteSpace(localPackagePath);
            var fullUrl = localPackagePath ?? "";
            var latestVersion = isLocal ? Path.GetFileNameWithoutExtension(localPackagePath!) : "";
            var fullHash = "";
            string[] repository = [];
            if (!isLocal)
            {
                if (isGithub)
                {
                    repository = GetRepoFromUrl(MaaProcessor.Interface?.Github ?? MaaProcessor.Interface?.Url ?? "");
                    if (repository.Length < 2) throw new InvalidDataException("当前资源未配置 GitHub 仓库。");
                    var release = await GetLatestVersionAndDownloadUrlFromGithubAsync(repository[0], repository[1], false, "", localVersion);
                    (fullUrl, latestVersion, fullHash) = release;
                }
                else GetDownloadUrlFromMirror(localVersion, GetResourceID(), CDK(), out fullUrl, out latestVersion, out fullHash, out _, currentVersion: localVersion);
                if (string.IsNullOrWhiteSpace(latestVersion)) throw new InvalidDataException("无法获取目标资源版本。");
                if (!IsNewVersionAvailable(latestVersion, localVersion) && currentVersion != "v0.0.0")
                {
                    ToastHelper.Info(LangKeys.ResourcesAreLatestVersion.ToLocalization());
                    Instances.RootViewModel.SetUpdating(false);
                    action?.Invoke();
                    return;
                }
                if (string.IsNullOrWhiteSpace(fullUrl)) throw new InvalidDataException("无法获取更新包下载地址。");
            }
            var session = Path.Combine(AppPaths.TempResourceDirectory, "update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(session);
            var selectedUrl = fullUrl;
            var selectedHash = fullHash;
            var selectedDelta = false;
            if (!isLocal && isGithub && currentVersion != "v0.0.0")
            {
                var delta = await TryFindGithubDeltaAsync(repository[0], repository[1], latestVersion, localVersion, fullUrl);
                if (delta != null)
                {
                    selectedUrl = delta.Value.Url;
                    selectedHash = delta.Value.Hash;
                    selectedDelta = true;
                    LoggerHelper.Info($"使用 GitHub 增量包：{localVersion} -> {latestVersion}");
                }
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var startingInstallation = false;
                try
                {
                    var extension = GetFileExtensionFromUrl(selectedUrl);
                    if (string.IsNullOrEmpty(extension)) extension = ".zip";
                    var archive = isLocal ? localPackagePath! : Path.Combine(session, $"package-{attempt}{extension}");
                    if (!isLocal)
                    {
                        SetText(status, selectedDelta ? "正在下载改动文件" : LangKeys.Downloading.ToLocalization());
                        var download = await DownloadWithRetry(selectedUrl, archive, progress, 3);
                        if (!download.Item1) throw new IOException("更新包下载失败。");
                        archive = download.Item2;
                        if (!string.IsNullOrEmpty(selectedHash) && !await VerifyFileSha256Async(archive, selectedHash))
                            throw new InvalidDataException("更新包 SHA-256 校验失败。");
                    }
                    var extracted = Path.Combine(session, "payload-" + attempt);
                    ExtractUpdateArchive(archive, extracted);
                    var manifestPath = Path.Combine(extracted, UpdateManifest.FileName);
                    var manifest = File.Exists(manifestPath) ? UpdateManifest.Read(manifestPath) : null;
                    var incremental = manifest?.Kind == "delta" || (currentVersion != "v0.0.0" && File.Exists(Path.Combine(extracted, "changes.json")));
                    if (!ValidateExtractedResourcePackage(extracted, incremental, out var root, out _, out _, out var message))
                        throw new InvalidDataException(message);
                    manifest = File.Exists(Path.Combine(root, UpdateManifest.FileName)) ? UpdateManifest.Read(Path.Combine(root, UpdateManifest.FileName)) : manifest;
                    if (selectedDelta && (manifest?.Kind != "delta" || manifest.BaseVersion != localVersion || manifest.Version != latestVersion))
                        throw new InvalidDataException("增量包的目标或基准版本不符合当前更新。");
                    var plan = CreatePackageUpdatePlan(root, manifest, latestVersion, session, incremental);
                    TransactionalUpdate.VerifyPayload(plan);
                    if (plan.Manifest.Kind == "delta") TransactionalUpdate.VerifyBaseline(plan);
                    if (IsEnvEnabled(ResourceUpdateDebugEnv) && IsEnvEnabled(ResourceUpdateDryRunEnv))
                    {
                        File.WriteAllText(Path.Combine(session, "plan.json"), JsonSerializer.Serialize(plan, UpdateManifest.JsonOptions));
                        ToastHelper.Info("更新检查完成，未安装（DryRun）", session, 5000);
                        return;
                    }
                    SetText(status, "文件校验通过，正在退出并安装更新");
                    startingInstallation = true;
                    await LaunchPackageUpdaterAsync(plan, session);
                    action?.Invoke();
                    return;
                }
                catch (Exception ex) when (selectedDelta && !startingInstallation)
                {
                    LoggerHelper.Warning($"增量更新不可用，回退完整包：{ex.Message}");
                    SetText(status, "增量包不适用，正在下载完整包");
                    selectedUrl = fullUrl;
                    selectedHash = fullHash;
                    selectedDelta = false;
                }
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"资源更新失败，安装目录未修改：{ex.Message}", ex);
            ToastHelper.Warn(LangKeys.Warning.ToLocalization(), ex.Message, -1);
        }
        finally
        {
            Instances.RootViewModel.SetUpdating(false);
            DispatcherHelper.PostOnMainThread(() => Instances.InstanceTabBarViewModel.ActiveTab?.TaskQueueViewModel.ClearDownloadProgress());
            Dismiss(toast);
        }
    }

    private static async Task<(string Url, string Hash)?> TryFindGithubDeltaAsync(string owner, string repository,
        string version, string baseline, string fullUrl)
    {
        try
        {
            using var client = CreateHttpClientWithProxy();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("MFAComponentUpdater/1.0");
            var token = Instances.VersionUpdateSettingsUserControlModel.GitHubToken;
            if (!string.IsNullOrWhiteSpace(token)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var release = JObject.Parse(await client.GetStringAsync($"https://api.github.com/repos/{owner}/{repository}/releases/tags/{Uri.EscapeDataString(version)}"));
            var assets = release["assets"] as JArray;
            var indexAsset = assets?.FirstOrDefault(a => a["name"]?.ToString() == "update-index.json");
            if (indexAsset == null) return null;
            client.DefaultRequestHeaders.Authorization = null;
            var index = JObject.Parse(await client.GetStringAsync(indexAsset["browser_download_url"]!.ToString()));
            if ((int?)index["schema"] != 1 || index["version"]?.ToString() != version) return null;
            var fullName = Uri.UnescapeDataString(Path.GetFileName(new Uri(fullUrl).AbsolutePath));
            var delta = (index["deltas"] as JArray)?.FirstOrDefault(d => d["fullAsset"]?.ToString() == fullName
                && d["baseVersion"]?.ToString() == baseline && d["version"]?.ToString() == version);
            if (delta == null) return null;
            var asset = assets!.FirstOrDefault(a => a["name"]?.ToString() == delta["asset"]?.ToString());
            var hash = delta["sha256"]?.ToString() ?? "";
            if (asset == null || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) return null;
            var digest = ExtractSha256FromDigest(asset["digest"]?.ToString());
            if (!string.IsNullOrEmpty(digest) && !digest.Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("增量包校验信息不一致。");
            return (asset["browser_download_url"]!.ToString(), hash);
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"无法取得增量更新信息，将使用完整包：{ex.Message}");
            return null;
        }
    }

    private static void ExtractUpdateArchive(string archive, string destination)
    {
        if (!archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            if (!UniversalExtractor.Extract(archive, destination)) throw new InvalidDataException("更新包解压失败。");
            return;
        }
        Directory.CreateDirectory(destination);
        using var package = ZipFile.OpenRead(archive);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in package.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            var relative = entry.FullName;
            if (!UpdatePaths.IsManaged(relative) && relative != UpdateManifest.FileName) continue;
            if (!seen.Add(relative)) throw new InvalidDataException($"压缩包包含重复路径：{relative}");
            var target = UpdatePaths.Resolve(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target);
            if (!OperatingSystem.IsWindows())
            {
                var mode = (entry.ExternalAttributes >> 16) & 511;
                File.SetUnixFileMode(target, (UnixFileMode)(mode == 0 ? 420 : mode));
            }
        }
    }

    private static UpdatePlan CreatePackageUpdatePlan(string root, UpdateManifest? manifest, string version,
        string session, bool incremental = false, bool resourceUpdate = true)
    {
        var hasCompleteFileList = manifest != null || !incremental;
        if (manifest == null)
        {
            var packageInterface = Path.Combine(root, "interface.json");
            if (resourceUpdate && File.Exists(packageInterface))
                version = JObject.Parse(File.ReadAllText(packageInterface))["version"]?.ToString() ?? version;
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => UpdatePaths.IsManaged(Path.GetRelativePath(root, f).Replace('\\', '/'))
                            && Path.GetFileName(f) != "changes.json")
                .Select(f => UpdateFile.FromFile(root, f)).ToList();
            if (!resourceUpdate)
            {
                files.RemoveAll(f => UpdatePaths.IsData(f.Path));
                // Resource packages own the native/Python pair. A UI-only update
                // must not downgrade those libraries without the matching Python.
                if (File.Exists(Path.Combine(AppPaths.InstallRoot, "python-dependencies.json")))
                    files.RemoveAll(f => f.Path.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase)
                        && (Path.GetFileName(f.Path).Contains("Maa", StringComparison.OrdinalIgnoreCase)
                            || Path.GetFileName(f.Path).Contains("opencv", StringComparison.OrdinalIgnoreCase))
                        || f.Path.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase)
                        || f.Path.StartsWith("libs/MaaAgentBinary/", StringComparison.OrdinalIgnoreCase));
            }
            manifest = new UpdateManifest { Version = version, Files = files, Payload = files.Select(f => f.Path).ToList() };
        }
        manifest.Validate();
        manifest.ValidatePlatform();
        var currentExecutable = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName!;
        var executable = manifest.Files.FirstOrDefault(f => Path.GetFileName(f.Path).Equals(Path.GetFileName(currentExecutable), StringComparison.OrdinalIgnoreCase));
        var packageExecutable = FindMFAExecutableInDirectory(root);
        var restart = executable != null ? Path.Combine(AppPaths.InstallRoot, executable.Path)
            : !string.IsNullOrEmpty(packageExecutable) ? Path.Combine(AppPaths.InstallRoot, Path.GetRelativePath(root, packageExecutable)) : currentExecutable;
        var plan = new UpdatePlan
        {
            SourceRoot = Path.GetFullPath(root), InstallRoot = AppPaths.InstallRoot, DataRoot = AppPaths.DataRoot,
            BackupRoot = Path.Combine(session, "rollback"), RestartPath = restart, PreviousRestartPath = currentExecutable,
            ParentPid = Environment.ProcessId, ParentStartTimeUtcTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
            Manifest = manifest, IsResourceUpdate = resourceUpdate, HasCompleteFileList = hasCompleteFileList,
            Deleted = manifest.Deleted.ToList()
        };
        if (incremental && manifest.Kind != "delta")
        {
            var changes = JObject.Parse(File.ReadAllText(Path.Combine(root, "changes.json")));
            plan.Deleted.AddRange((changes["deleted"] as JArray)?.Values<string>().Where(p => p != null).Select(p => p!.Replace('\\', '/')) ?? []);
        }
        var target = manifest.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (resourceUpdate && !hasCompleteFileList
            && File.Exists(Path.Combine(AppPaths.InstallRoot, "python-dependencies.json"))
            && manifest.Files.Any(f => f.Path.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(f.Path).Contains("MaaFramework", StringComparison.OrdinalIgnoreCase))
            && !target.Contains("python-dependencies.json"))
            throw new InvalidDataException("增量包改变了 MaaFramework 但缺少配套依赖清单，请使用完整包更新。");
        if (!incremental && resourceUpdate)
        {
            var previous = Path.Combine(AppPaths.DataRoot, UpdateManifest.FileName);
            if (File.Exists(previous))
                try { plan.Deleted.AddRange(UpdateManifest.Read(previous).Files.Select(f => f.Path).Where(p => !target.Contains(p))); }
                catch (Exception ex) when (ex is InvalidDataException or JsonException)
                { LoggerHelper.Warning($"旧安装清单损坏，将使用完整包修复：{ex.Message}"); }
        }
        // A complete target manifest also describes unchanged dependencies in a delta.
        if (resourceUpdate && target.Contains("python-dependencies.json"))
        {
            var python = Path.Combine(AppPaths.InstallRoot, "python");
            if (Directory.Exists(python))
                foreach (var metadata in Directory.EnumerateDirectories(python, "*.dist-info", SearchOption.AllDirectories))
                {
                    var metadataPath = Path.GetRelativePath(AppPaths.InstallRoot, metadata).Replace('\\', '/');
                    var existing = Directory.EnumerateFiles(metadata, "*", SearchOption.AllDirectories).ToArray();
                    if (hasCompleteFileList && existing.Length == 0 && !target.Any(p => p.StartsWith(metadataPath + "/", StringComparison.OrdinalIgnoreCase)))
                        plan.EmptyDirectories.AddRange(Directory.EnumerateDirectories(metadata, "*", SearchOption.AllDirectories).Append(metadata)
                            .Select(p => Path.GetRelativePath(AppPaths.InstallRoot, p).Replace('\\', '/')).Where(UpdatePaths.IsManaged));
                    foreach (var file in existing)
                    {
                        var path = Path.GetRelativePath(AppPaths.InstallRoot, file).Replace('\\', '/');
                        if (UpdatePaths.IsManaged(path) && !target.Contains(path)
                            && (hasCompleteFileList || IsObsoletePythonMetadata(metadata, root))) plan.Deleted.Add(path);
                    }
                }
        }
        plan.Deleted = plan.Deleted.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return plan;
    }

    private static bool IsObsoletePythonMetadata(string directory, string sourceRoot)
    {
        var metadata = Path.Combine(directory, "METADATA");
        if (!File.Exists(metadata)) return false;
        var lines = File.ReadAllLines(metadata);
        var name = lines.FirstOrDefault(l => l.StartsWith("Name: "))?[6..];
        var installed = lines.FirstOrDefault(l => l.StartsWith("Version: "))?[9..];
        if (name == null || installed == null) return false;
        var packages = JObject.Parse(File.ReadAllText(Path.Combine(sourceRoot, "python-dependencies.json")))["packages"] as JObject;
        var desired = packages?.Properties().FirstOrDefault(p => p.Name.Replace('-', '_').Equals(name.Replace('-', '_'), StringComparison.OrdinalIgnoreCase));
        return desired != null && desired.Value.ToString() != installed;
    }

    private static async Task LaunchPackageUpdaterAsync(UpdatePlan plan, string session)
    {
        var name = OperatingSystem.IsWindows() ? "MFAUpdater.exe" : "MFAUpdater";
        var candidate = new[] { Path.Combine(plan.SourceRoot, name), Path.Combine(plan.InstallRoot, name), FindUpdaterExecutablePath(plan.InstallRoot) }
            .FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("缺少安全更新器，请安装包含新版 MFAUpdater 的完整包。");
        var stagedRoot = Path.Combine(session, "updater");
        Directory.CreateDirectory(stagedRoot);
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(candidate)!, Path.GetFileNameWithoutExtension(candidate) + ".*"))
            File.Copy(file, Path.Combine(stagedRoot, Path.GetFileName(file)), true);
        var staged = Path.Combine(stagedRoot, Path.GetFileName(candidate));
        if (!File.Exists(staged)) File.Copy(candidate, staged);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(staged, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var probeInfo = new ProcessStartInfo(staged) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        probeInfo.ArgumentList.Add("--protocol");
        using (var probe = Process.Start(probeInfo) ?? throw new IOException("安全更新器无法启动。"))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await probe.WaitForExitAsync(timeout.Token); }
            catch { if (!probe.HasExited) probe.Kill(true); throw; }
            if (probe.ExitCode != 0 || (await probe.StandardOutput.ReadToEndAsync()).Trim() != "mfa-update-v1")
                throw new InvalidDataException("更新器不支持安全安装，请使用包含新版 MFAUpdater 的完整包。");
        }
        plan.UpdaterPath = staged;
        var planPath = Path.Combine(session, "plan.json");
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan, UpdateManifest.JsonOptions));
        var start = new ProcessStartInfo(staged) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = stagedRoot };
        start.ArgumentList.Add("--apply-plan");
        start.ArgumentList.Add(planPath);
        using var updater = Process.Start(start) ?? throw new IOException("无法启动独立更新器。");
        // Cleanup is synchronous on the UI thread; the updater waits for the PID.
        try
        {
            await DispatcherHelper.RunOnMainThreadAsync(() => Instances.RootView.BeforeClosed(true, true));
            Queue.Clear();
            Instances.ShutdownApplication();
        }
        catch
        {
            if (!updater.HasExited) updater.Kill(true);
            throw;
        }
    }
}
