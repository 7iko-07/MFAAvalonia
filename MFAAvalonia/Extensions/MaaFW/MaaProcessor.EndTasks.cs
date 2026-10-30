using Avalonia.Media;
using MaaFramework.Binding;
using MFAAvalonia.Configuration;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Extensions.MaaFW;

public partial class MaaProcessor
{
    private static readonly EndTaskCoordinator EndTasks = new(Instances.ShutdownApplication);
    private EndTaskCoordinator.Run? _endTaskRun;
    private bool _runHasEndTasks;

    private async Task FinishEndTaskRunAsync(EndTaskCoordinator.Run run,
        bool onlyStart, CancellationToken token)
    {
        if (!ReferenceEquals(_endTaskRun, run)) return;
        var workStatus = Status;
        if ((workStatus is MFATask.MFATaskStatus.SUCCEEDED or MFATask.MFATaskStatus.FAILED) && !token.IsCancellationRequested)
        {
            EndTasks.CompleteWork(run, workStatus == MFATask.MFATaskStatus.SUCCEEDED);
            if (_runHasEndTasks) AddLogByKey("EndTaskWaiting", (IBrush?)null);
            if (!await run.DeviceCompletion && !token.IsCancellationRequested)
            {
                Status = MFATask.MFATaskStatus.FAILED;
                if (_runHasEndTasks) ReportEndTaskFailure("EndTaskCancelled");
            }
            if (run.CleanupFailed && !run.AllowFailureCleanup)
                Status = MFATask.MFATaskStatus.FAILED;
        }
        else
        {
            EndTasks.Cancel(run);
        }
        if (token.IsCancellationRequested) Status = MFATask.MFATaskStatus.STOPPED;
        if (ReferenceEquals(_endTaskRun, run)) Stop(Status, true, onlyStart);
    }

    private void ResetRejectedRun()
    {
        DispatcherHelper.PostOnMainThread(() =>
        {
            if (ViewModel == null) return;
            ViewModel.IsRunning = false;
            ViewModel.ToggleEnable = true;
        });
    }

    private void ReportEndTaskFailure(string key)
    {
        using var scope = BeginInstanceLogScope("EndTask", "Worker");
        AddLogByKey(key, (IBrush?)null);
        ToastHelper.Warn(key.ToLocalization(), instanceId: InstanceId);
    }

    private string EndTaskDeviceKey() => ViewModel?.CurrentController switch
    {
        MaaControllerTypes.Adb => EndTaskPolicy.AdbDeviceKey(Config.AdbDevice.AdbSerial, Config.AdbDevice.Name),
        MaaControllerTypes.Win32 when Config.DesktopWindow.HWnd != 0 => $"win32:{Config.DesktopWindow.HWnd}",
        MaaControllerTypes.PlayCover when !string.IsNullOrWhiteSpace(Config.PlayCover.PlayCoverAddress) =>
            "playcover:" + Config.PlayCover.PlayCoverAddress,
        _ => string.Empty
    };

    private static IEnumerable<DragItemViewModel> ExpandEndTasks(IEnumerable<DragItemViewModel> tasks)
    {
        foreach (var source in tasks)
        {
            var task = source.InterfaceItem;
            if (task?.IsEndTask != true) continue;
            if (!task.IsShutdownTask)
            {
                yield return source;
                continue;
            }
            if (task.CloseGame)
            {
                var game = task.Clone();
                game.IsShutdownTask = false;
                game.Label = task.Name;
                game.DisplayNameOverride = null;
                yield return new DragItemViewModel(game) { OwnerViewModel = source.OwnerViewModel };
            }
            if (task.CloseEmulator)
                yield return new DragItemViewModel(new MaaInterface.MaaInterfaceTask
                {
                    Name = ClientTaskProvider.CloseEmulatorEntry, Entry = ClientTaskProvider.CloseEmulatorEntry,
                    EndAction = EndTaskPolicy.CloseEmulator,
                }) { OwnerViewModel = source.OwnerViewModel };
            if (task.CloseApplication)
                yield return new DragItemViewModel(new MaaInterface.MaaInterfaceTask
                {
                    Name = ClientTaskProvider.CloseMfaEntry, Entry = ClientTaskProvider.CloseMfaEntry,
                    EndAction = EndTaskPolicy.CloseMFA,
                }) { OwnerViewModel = source.OwnerViewModel };
        }
    }

    private bool BeginEndTaskRun(List<DragItemViewModel> tasks, bool onlyStart)
    {
        var endTasks = onlyStart ? [] : ExpandEndTasks(tasks).ToList();
        if (EndTaskPolicy.Conflicts(endTasks.Count > 0, InstanceConfiguration.GetValue(ConfigurationKeys.AfterTask, "None")))
        {
            ReportEndTaskFailure("EndTaskConflict");
            ResetRejectedRun();
            return false;
        }

        try
        {
            var continueOnError = InstanceConfiguration.GetValue(ConfigurationKeys.ContinueRunningWhenError, true);
            var cleanup = new List<EndTaskCoordinator.Cleanup>();
            foreach (var task in endTasks)
            {
                var order = EndTaskPolicy.Order(task.InterfaceItem!.EndAction);
                if (task.InterfaceItem.EndAction == EndTaskPolicy.CloseMFA) continue;
                if (task.InterfaceItem.EndAction == EndTaskPolicy.CloseEmulator)
                {
                    if (ViewModel?.CurrentController != MaaControllerTypes.Adb)
                        throw new InvalidOperationException("CloseEmulator requires an Adb controller.");
                    cleanup.Add(new(order, EndTaskPolicy.CloseEmulator, () => Task.Run(() =>
                    {
                        using var scope = BeginInstanceLogScope("EndTask", "Worker");
                        if (CancellationTokenSource?.IsCancellationRequested != false) return false;
                        AddLogByKey("EndTaskExecuting", (IBrush?)null, true, true, task.Name);
                        var succeeded = EmulatorHelper.KillEmulatorModeSwitcher(this, allowFallback: false);
                        if (!succeeded) ReportEndTaskFailure("EndTaskFailed");
                        return succeeded;
                    })));
                }
                else
                {
                    var node = CreateNodeAndParam(task, 0);
                    if (string.IsNullOrWhiteSpace(node.Entry)) throw new InvalidOperationException("End pipeline entry is empty.");
                    cleanup.Add(new(order, $"pipeline:{node.Entry}:{node.Param}", async () =>
                    {
                        using var scope = BeginInstanceLogScope("EndTask", "Worker");
                        try
                        {
                            if (MaaTasker == null || CancellationTokenSource?.IsCancellationRequested != false) return false;
                            AddLogByKey("EndTaskExecuting", (IBrush?)null, true, true, task.Name);
                            ViewModel?.SetCurrentTaskName(task.Name);
                            // Keep the action result; apply the run's error policy
                            // after all selected end actions have been attempted.
                            var succeeded = await Task.Run(() => MaaTasker.AppendTask(node.Entry, node.Param ?? "{}")
                                .Wait() == MaaJobStatus.Succeeded);
                            if (!succeeded) ReportEndTaskFailure("EndTaskFailed");
                            return succeeded;
                        }
                        catch (Exception ex)
                        {
                            LoggerHelper.Error($"结束后任务失败：{node.Name}，{ex.Message}", ex);
                            ReportEndTaskFailure("EndTaskFailed");
                            return false;
                        }
                    }));
                }
            }

            var run = EndTasks.Begin(InstanceId, EndTaskDeviceKey(), cleanup,
                endTasks.Any(task => task.InterfaceItem!.EndAction == EndTaskPolicy.CloseMFA),
                () => ReportEndTaskFailure("EndTaskExitCancelled"), allowFailureCleanup: continueOnError);
            if (run == null)
            {
                ReportEndTaskFailure("EndTaskBusy");
                ResetRejectedRun();
                return false;
            }
            _endTaskRun = run;
            _runHasEndTasks = endTasks.Count > 0;
            DispatcherHelper.PostOnMainThread(() =>
            {
                if (ViewModel != null) ViewModel.IsRunning = true;
                Instances.RootViewModel.IsRunning = true;
            });
            return true;
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"结束后任务配置无效：{ex.Message}", ex);
            ReportEndTaskFailure("EndTaskInvalid");
            ResetRejectedRun();
            return false;
        }
    }
}
