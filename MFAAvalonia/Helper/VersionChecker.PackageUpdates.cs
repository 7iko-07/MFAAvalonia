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
    private static int _packageUpdateActive;
    internal static string DependencyRepairMarker => Path.Combine(AppPaths.TempDirectory, "agent-dependency-repair.attempt");

    internal static void RequestDependencyRepair()
    {
        if (Volatile.Read(ref _packageUpdateActive) != 0) return;
        try
        {
            if (!DependencyRepairState.TryBegin(DependencyRepairMarker))
            {
                ToastHelper.Warn(LangKeys.AgentStartFailed.ToLocalization(), LangKeys.AgentDependencyRepairStopped.ToLocalization(), -1);
                return;
            }
            ToastHelper.Info(LangKeys.AgentDependencyRepairStarted.ToLocalization());
            UpdateResourceAsync("v0.0.0");
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"无法发起依赖修复：{ex.Message}", ex);
            ToastHelper.Error(LangKeys.AgentStartFailed.ToLocalization(), ex.Message);
        }
    }

    private static async Task UpdateResourceTransactionalAsync(bool isGithub, bool closeDialog, bool noDialog,
        Action? action, string currentVersion, string? localPackagePath)
    {
        if (Interlocked.CompareExchange(ref _packageUpdateActive, 1, 0) != 0) return;
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
            var mirrorIncremental = false;
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
                else
                {
                    GetDownloadUrlFromMirror(localVersion, GetResourceID(), CDK(), out fullUrl, out latestVersion, out fullHash, out var isFull, currentVersion: localVersion);
                    mirrorIncremental = !isFull;
                    if (currentVersion == "v0.0.0" && mirrorIncremental)
                        throw new InvalidDataException("Mirror 未返回请求的完整修复包。");
                }
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
            var selectedDelta = mirrorIncremental;
            var requireFull = currentVersion == "v0.0.0";
            GithubActionsDelta? actionsDelta = null;
            if (!isLocal && isGithub && currentVersion != "v0.0.0")
            {
                var delta = await TryFindGithubDeltaAsync(repository[0], repository[1], latestVersion, localVersion, fullUrl, fullHash);
                if (delta != null)
                {
                    actionsDelta = delta;
                    selectedHash = delta.Sha256;
                    selectedDelta = true;
                    LoggerHelper.Info($"使用 GitHub 增量包：{localVersion} -> {latestVersion}");
                }
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var startingInstallation = false;
                try
                {
                    var extension = actionsDelta != null ? ".zip" : GetFileExtensionFromUrl(selectedUrl);
                    if (string.IsNullOrEmpty(extension)) extension = ".zip";
                    var archive = isLocal ? localPackagePath! : Path.Combine(session, $"package-{attempt}{extension}");
                    if (!isLocal)
                    {
                        SetText(status, selectedDelta ? "正在下载改动文件" : LangKeys.Downloading.ToLocalization());
                        if (actionsDelta != null && selectedDelta)
                        {
                            using var client = CreateHttpClientWithProxy(allowAutoRedirect: false);
                            var source = new GithubActionsUpdateSource(client, Instances.VersionUpdateSettingsUserControlModel.GitHubToken);
                            await source.DownloadDeltaAsync(repository[0], repository[1], actionsDelta!, archive,
                                value => SetProgress(progress, value));
                        }
                        else
                        {
                            var download = await DownloadWithRetry(selectedUrl, archive, progress, 3);
                            if (!download.Item1) throw new IOException("更新包下载失败。");
                            archive = download.Item2;
                        }
                        if (!string.IsNullOrEmpty(selectedHash) && !await VerifyFileSha256Async(archive, selectedHash))
                            throw new InvalidDataException("更新包 SHA-256 校验失败。");
                    }
                    var extracted = Path.Combine(session, "payload-" + attempt);
                    ExtractUpdateArchive(archive, extracted);
                    var manifestPath = Path.Combine(extracted, UpdateManifest.FileName);
                    var manifest = File.Exists(manifestPath) ? UpdateManifest.Read(manifestPath) : null;
                    var incremental = selectedDelta || manifest?.Kind == "delta" || File.Exists(Path.Combine(extracted, "changes.json"));
                    if (!ValidateExtractedResourcePackage(extracted, incremental, out var root, out _, out _, out var message))
                        throw new InvalidDataException(message);
                    manifest = File.Exists(Path.Combine(root, UpdateManifest.FileName)) ? UpdateManifest.Read(Path.Combine(root, UpdateManifest.FileName)) : manifest;
                    incremental |= manifest?.Kind == "delta" || File.Exists(Path.Combine(root, "changes.json"));
                    if (requireFull && incremental)
                        throw new InvalidDataException("完整包请求返回了增量包，已停止安装。");
                    if (!isLocal && !isGithub && incremental)
                    {
                        selectedDelta = true;
                        manifest = MirrorPackageUpdate.CreateDelta(manifest, root, AppPaths.InstallRoot,
                            AppPaths.DataRoot, localVersion, latestVersion);
                    }
                    if (incremental && manifest == null)
                        throw new InvalidDataException("增量包缺少完整目标清单，请使用完整包。");
                    if (!isLocal && manifest != null && manifest.Version != latestVersion)
                        throw new InvalidDataException("更新清单的版本与下载目标不一致。");
                    if (selectedDelta && (manifest?.Kind != "delta" || manifest.BaseVersion != localVersion || manifest.Version != latestVersion))
                        throw new InvalidDataException("增量包的目标或基准版本不符合当前更新。");
                    var prefix = Path.GetRelativePath(extracted, root).Replace('\\', '/');
                    var plan = CreatePackageUpdatePlan(root, manifest, latestVersion, session, incremental,
                        archivePath: archive, archivePrefix: prefix == "." ? "" : prefix + "/");
                    if (!isLocal && plan.Manifest.Version != latestVersion)
                        throw new InvalidDataException("资源包版本与下载目标不一致。");
                    TransactionalUpdate.VerifyPayload(plan);
                    if (plan.Manifest.Kind == "delta") TransactionalUpdate.VerifyBaseline(plan);
                    if (incremental)
                        await PythonDependencies.VerifyInstalledAsync(AppPaths.InstallRoot, requireInterpreter: false);
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
                catch (Exception ex) when (selectedDelta && !isLocal && !startingInstallation && attempt == 0)
                {
                    LoggerHelper.Warning($"增量更新不可用，回退完整包：{ex.Message}");
                    SetText(status, "增量包不适用，正在下载完整包");
                    if (!isGithub)
                    {
                        GetDownloadUrlFromMirror("v0.0.0", GetResourceID(), CDK(), out fullUrl,
                            out var fullVersion, out fullHash, out var isFull, currentVersion: localVersion);
                        if (!isFull || string.IsNullOrWhiteSpace(fullUrl) || fullVersion != latestVersion)
                            throw new InvalidDataException("Mirror 完整包类型或版本不匹配，请重新检查更新。");
                    }
                    selectedUrl = fullUrl;
                    selectedHash = fullHash;
                    selectedDelta = false;
                    actionsDelta = null;
                    requireFull = true;
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
            Interlocked.Exchange(ref _packageUpdateActive, 0);
            Instances.RootViewModel.SetUpdating(false);
            DispatcherHelper.PostOnMainThread(() => Instances.InstanceTabBarViewModel.ActiveTab?.TaskQueueViewModel.ClearDownloadProgress());
            Dismiss(toast);
        }
    }

    private static async Task<GithubActionsDelta?> TryFindGithubDeltaAsync(string owner, string repository,
        string version, string baseline, string fullUrl, string fullHash)
    {
        try
        {
            var token = Instances.VersionUpdateSettingsUserControlModel.GitHubToken;
            if (string.IsNullOrWhiteSpace(token)) return null;
            using var client = CreateHttpClientWithProxy(allowAutoRedirect: false);
            var fullName = Uri.UnescapeDataString(Path.GetFileName(new Uri(fullUrl).AbsolutePath));
            return await new GithubActionsUpdateSource(client, token).FindDeltaAsync(owner, repository, version, baseline, fullName, fullHash);
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
        UpdateArchive.ExtractZip(archive, destination);
    }

    private static UpdatePlan CreatePackageUpdatePlan(string root, UpdateManifest? manifest, string version,
        string session, bool incremental = false, bool resourceUpdate = true, string? archivePath = null, string archivePrefix = "")
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
            if (archivePath?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
                UpdateArchive.RestoreLinkMetadata(archivePath, archivePrefix, files);
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
        var target = manifest.Files.Select(f => f.Path).ToHashSet(UpdatePaths.Comparer);
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
        PythonDependencies.AddObsoleteMetadata(plan);
        plan.Deleted = plan.Deleted.Distinct(UpdatePaths.Comparer).ToList();
        return plan;
    }

    private static async Task LaunchPackageUpdaterAsync(UpdatePlan plan, string session)
    {
        var name = OperatingSystem.IsWindows() ? "MFAUpdater.exe" : "MFAUpdater";
        var candidates = new[] { Path.Combine(plan.SourceRoot, name), Path.Combine(plan.InstallRoot, name), FindUpdaterExecutablePath(plan.InstallRoot) }
            .Where(File.Exists).Distinct(UpdatePaths.Comparer).ToArray();
        var stagedRoot = Path.Combine(session, "updater");
        string? staged = null;
        for (var i = 0; i < candidates.Length; i++)
        {
            // Probe staged copies, never launch a legacy updater in the install
            // directory. An older resource package may bundle an older updater.
            var candidate = candidates[i];
            var directory = Path.Combine(stagedRoot, i.ToString());
            Directory.CreateDirectory(directory);
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(candidate)!, Path.GetFileNameWithoutExtension(candidate) + ".*"))
                File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), true);
            var path = Path.Combine(directory, Path.GetFileName(candidate));
            if (!File.Exists(path)) File.Copy(candidate, path);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (await ProbeUpdaterAsync(path, "--protocol", "mfa-update-v1")
                && await ProbeUpdaterAsync(path, "--dependency-check-protocol", "mfa-python-check-v1"))
            {
                staged = path;
                break;
            }
        }
        if (staged == null)
            throw new InvalidDataException("缺少支持 Python 依赖校验的更新器，请安装包含新版 MFAUpdater 的完整包。");
        plan.UpdaterPath = staged;
        var planPath = Path.Combine(session, "plan.json");
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan, UpdateManifest.JsonOptions));
        var start = new ProcessStartInfo(staged) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(staged)! };
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

    private static async Task<bool> ProbeUpdaterAsync(string executable, string argument, string expected)
    {
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)!
            };
            start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("安全更新器无法启动。");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { if (!process.HasExited) process.Kill(true); throw; }
            await error;
            return process.ExitCode == 0 && (await output).Trim() == expected;
        }
        catch (Exception ex)
        {
            LoggerHelper.Warning($"更新器能力检查失败：{ex.Message}");
            return false;
        }
    }
}
