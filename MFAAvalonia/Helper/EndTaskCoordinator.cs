using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MFAAvalonia.Helper;

/// <summary>
/// Coordinates run eligibility, device cleanup and application exit. Independent of
/// UI flags and MaaTasker.IsRunning: a queue waiting for cleanup is still a live run.
/// </summary>
public sealed class EndTaskCoordinator(Action shutdown)
{
    public sealed record Cleanup(int Order, string Key, Func<Task<bool>> Execute);

    public sealed class Run(string instanceId, string device, IReadOnlyList<Cleanup> cleanup,
        bool closeApplication, Action cancelled, bool allowFailureCleanup)
    {
        public string InstanceId { get; } = instanceId;
        public string Device { get; } = device;
        internal IReadOnlyList<Cleanup> Cleanup { get; } = cleanup;
        internal bool CloseApplication { get; } = closeApplication;
        internal Action Cancelled { get; } = cancelled;
        public bool AllowFailureCleanup { get; } = allowFailureCleanup;
        public bool CleanupFailed { get; internal set; }
        internal HashSet<Run> Peers { get; } = [];
        internal bool WorkDone;
        internal bool WorkAllowsCleanup;
        internal bool Settled;
        internal bool SettledAllowsCleanup;
        internal bool Cleaning;
        internal bool ExitHandled;
        internal TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> DeviceCompletion => Completion.Task;
    }

    private readonly object _sync = new();
    private readonly List<Run> _runs = [];
    private readonly HashSet<string> _closingDevices = [];
    private bool _exiting;

    public bool IsActive(Run? run)
    {
        lock (_sync) return run != null && !run.Settled;
    }

    // An unidentified device conservatively overlaps every device. Never guess
    // that two unknown connection targets are independent.
    private static bool SameDevice(string a, string b) => a.Length == 0 || b.Length == 0 || a == b;

    private static bool BlocksCleanup(Run run) => run.WorkDone && !run.WorkAllowsCleanup ||
        run.Settled && !run.SettledAllowsCleanup || run.Completion.Task.IsCompletedSuccessfully && !run.Completion.Task.Result;

    public Run? Begin(string instanceId, string device, IReadOnlyList<Cleanup> cleanup,
        bool closeApplication, Action cancelled, bool allowFailureCleanup = false)
    {
        lock (_sync)
        {
            if (_exiting || _runs.Any(run => run.InstanceId == instanceId && !run.Settled) ||
                _closingDevices.Any(closing => SameDevice(closing, device))) return null;
            var run = new Run(instanceId, device, cleanup, closeApplication, cancelled, allowFailureCleanup);
            foreach (var peer in _runs.Where(peer => !peer.Settled || peer.CloseApplication && !peer.ExitHandled))
            {
                run.Peers.Add(peer);
                peer.Peers.Add(run);
            }
            _runs.Add(run);
            return run;
        }
    }

    public void CompleteWork(Run run, bool succeeded)
    {
        lock (_sync)
        {
            // A late completion must not overwrite an earlier cancellation.
            if (!run.WorkDone)
            {
                run.WorkDone = true;
                run.WorkAllowsCleanup = succeeded || run.AllowFailureCleanup;
            }
            Pump();
        }
    }

    public void Cancel(Run? run)
    {
        if (run == null) return;
        lock (_sync)
        {
            if (run.Settled && (!run.CloseApplication || run.ExitHandled)) return;
            run.WorkDone = true;
            run.WorkAllowsCleanup = false;
            run.ExitHandled = true;
            run.Completion.TrySetResult(false);
            Pump();
        }
    }

    /// <summary>Called after the queue has finished stopping.</summary>
    public void Settle(Run? run, bool completed)
    {
        if (run == null) return;
        lock (_sync)
        {
            if (run.Settled) return;
            run.Settled = true;
            run.SettledAllowsCleanup = completed && run.WorkAllowsCleanup && run.Completion.Task.IsCompletedSuccessfully && run.Completion.Task.Result;
            if (!run.WorkDone)
            {
                run.WorkDone = true;
                run.WorkAllowsCleanup = false;
                run.Completion.TrySetResult(false);
            }
            Pump();
        }
    }

    // All mutations and reservations happen under _sync; external actions run
    // outside it. Device peers finish ordinary work before one combined, ordered
    // cleanup batch, so another tab cannot close the emulator before its game.
    private void Pump()
    {
        foreach (var run in _runs.Where(run => run.WorkDone && !run.Cleaning && !run.Completion.Task.IsCompleted).ToList())
        {
            if (!run.WorkAllowsCleanup)
            {
                run.Completion.TrySetResult(false);
                continue;
            }
            if (run.Cleanup.Count == 0)
            {
                run.Completion.TrySetResult(true);
                continue;
            }
            var peers = run.Peers.Where(peer => SameDevice(peer.Device, run.Device)).ToList();
            if (peers.Any(BlocksCleanup))
            {
                run.Completion.TrySetResult(false);
                continue;
            }
            if (peers.Any(peer => !peer.WorkDone) || _closingDevices.Any(device => SameDevice(device, run.Device))) continue;
            var pending = _runs.Where(peer => !peer.Cleaning && !peer.Completion.Task.IsCompleted).ToList();
            // An unknown target may share any known device. Keep the entire
            // cohort ordered and reserve all of it until cleanup finishes.
            var hasUnknownDevice = pending.Any(peer => peer.Device.Length == 0);
            var batch = pending.Where(peer => hasUnknownDevice || peer.Device == run.Device).ToList();
            if (batch.Any(peer => !peer.WorkDone)) continue;
            if (batch.Any(peer => BlocksCleanup(peer) || peer.Peers.Any(other => SameDevice(other.Device, peer.Device) && BlocksCleanup(other))))
            {
                foreach (var member in batch) member.Completion.TrySetResult(false);
                continue;
            }
            if (batch.Any(peer => peer.Peers.Any(other => SameDevice(other.Device, peer.Device) && !other.WorkDone))) continue;
            foreach (var member in batch) member.Cleaning = true;
            var reservedDevices = batch.Select(member => member.Device).Distinct().ToList();
            foreach (var device in reservedDevices) _closingDevices.Add(device);
            _ = Task.Run(() => CleanDeviceAsync(reservedDevices, batch));
        }

        foreach (var run in _runs.Where(run => run.CloseApplication && !run.ExitHandled && run.Settled).ToList())
        {
            var peers = run.Peers.Append(run).ToList();
            if (peers.Any(BlocksCleanup))
            {
                run.ExitHandled = true;
                _ = Task.Run(run.Cancelled);
                continue;
            }
            if (peers.Any(peer => !peer.Settled) || _runs.Any(peer => !peer.Settled) || _closingDevices.Count > 0) continue;
            // Reserve application exit before invoking the UI callback. New runs
            // cannot slip between the final eligibility check and shutdown.
            _exiting = true;
            foreach (var pending in _runs) pending.ExitHandled = true;
            _ = Task.Run(shutdown);
            break;
        }
        _runs.RemoveAll(run => run.Settled && (!run.CloseApplication || run.ExitHandled));
    }

    private async Task CleanDeviceAsync(List<string> reservedDevices, List<Run> batch)
    {
        var completed = true;
        var cleanupFailed = false;
        try
        {
            var actions = batch.SelectMany(run => run.Cleanup.Select(action => (run.Device, Action: action)))
                .OrderBy(item => item.Action.Order).DistinctBy(item => (item.Device, item.Action.Key));
            foreach (var item in actions)
            {
                lock (_sync)
                {
                    if (batch.Any(run => BlocksCleanup(run) || run.Peers.Any(peer =>
                            SameDevice(peer.Device, run.Device) && BlocksCleanup(peer))))
                        completed = false;
                }
                if (!completed) break;
                try
                {
                    if (!await item.Action.Execute().ConfigureAwait(false)) cleanupFailed = true;
                }
                catch
                {
                    cleanupFailed = true;
                }
            }
        }
        catch
        {
            completed = false;
        }
        lock (_sync)
        {
            foreach (var run in batch)
            {
                run.Cleaning = false;
                run.CleanupFailed = cleanupFailed;
                run.Completion.TrySetResult(completed && run.WorkAllowsCleanup);
            }
            foreach (var device in reservedDevices) _closingDevices.Remove(device);
            Pump();
        }
    }
}
