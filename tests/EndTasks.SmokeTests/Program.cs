using MFAAvalonia.Helper;
using System.Collections.Concurrent;

static partial class Program
{
    private static int _checks;
    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception(name);
        _checks++;
        Console.WriteLine("PASS " + name);
    }
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<T> Wait<T>(Task<T> task) => await task.WaitAsync(TimeSpan.FromSeconds(5));
    private static EndTaskCoordinator.Run Begin(EndTaskCoordinator coordinator, string id, string device,
        IReadOnlyList<EndTaskCoordinator.Cleanup>? cleanup = null, bool exit = false, Action? cancelled = null,
        bool continueOnError = false) =>
        coordinator.Begin(id, device, cleanup ?? [], exit, cancelled ?? (() => { }), continueOnError) ?? throw new Exception("begin " + id);
    private static EndTaskCoordinator.Cleanup Action(int order, string key, ConcurrentQueue<string> calls, bool success = true) =>
        new(order, key, () => { calls.Enqueue(key); return Task.FromResult(success); });

    public static async Task Main()
    {
        Check(EndTaskPolicy.Conflicts(true, "CloseMFA") && EndTaskPolicy.Conflicts(true, "ShutDown"), "end tasks conflict with every enabled Settings operation");
        Check(!EndTaskPolicy.Conflicts(false, "CloseMFA") && !EndTaskPolicy.Conflicts(true, "None"), "legacy settings and end tasks may be used separately");
        Check(EndTaskPolicy.AdbDeviceKey("emulator-5554") == EndTaskPolicy.AdbDeviceKey("localhost:5555"), "ADB aliases share a device barrier");
        Check(EndTaskPolicy.AdbDeviceKey(null) == "", "unknown devices retain a conservative barrier");
        Check(EndTaskPolicy.AdbDeviceKey("127.0.0.1:16384", "MuMuPlayer12") ==
            EndTaskPolicy.AdbDeviceKey("localhost:16385", "MuMu"), "MuMu ports for one emulator share a barrier");
        Check(EndTaskPolicy.AdbDeviceKey("127.0.0.1:62001", "Nox") ==
            EndTaskPolicy.AdbDeviceKey("localhost:62024", "Nox"), "Nox default-instance aliases share a barrier");
        Check(EndTaskPolicy.IsLocalEmulatorAddress("127.0.0.1:62024", "Nox") &&
            EndTaskPolicy.IsLocalEmulatorAddress("127.0.0.1:21513", "XYAZ"), "valid Nox and MEmu index ports are accepted");
        Check(!EndTaskPolicy.IsLocalEmulatorAddress("192.168.0.2:16384", "MuMuEmulator12") &&
            !EndTaskPolicy.IsLocalEmulatorAddress("usb-phone", "LDPlayer"), "remote and USB devices cannot become local emulator targets");
        Check(!EndTaskPolicy.IsLocalEmulatorAddress("127.0.0.1:5555", "Nox") &&
            !EndTaskPolicy.IsLocalEmulatorAddress("127.0.0.1:21505", "XYAZ") &&
            !EndTaskPolicy.IsLocalEmulatorAddress("emulator-5555", "LDPlayer"), "invalid emulator indexes and ambiguous fractional indexes are rejected");
        foreach (var value in new[] { "CloseEverything", "1", "pipeline", "" })
        {
            try { EndTaskPolicy.Order(value); throw new Exception("accepted invalid end action"); }
            catch (ArgumentException) { Check(true, "unknown end action rejected: " + value); }
        }

        await OrderedSingleRun();
        await FailuresAndStops();
        await SharedDevice();
        await IndependentDevices();
        await LateRunAndFailedPeer();
        await ClosingReservations();
        await ConcurrentExitRequests();
        await StableCompletedResults();
        await UnknownDeviceCohort();
        await ContinueOnErrorCleanup();
        await MixedConfigurationPolicies();
        await FailureModeProtections();
        Console.WriteLine($"{_checks} checks passed; no real device or application close was invoked.");
    }

    private static async Task OrderedSingleRun()
    {
        var calls = new ConcurrentQueue<string>();
        var exited = Signal();
        var coordinator = new EndTaskCoordinator(() => { calls.Enqueue("exit"); exited.TrySetResult(true); });
        var run = Begin(coordinator, "one", "device", [Action(1, "emulator", calls), Action(0, "game", calls)], true);
        Check(calls.IsEmpty, "top-of-list end tasks do not execute during ordinary work");
        coordinator.CompleteWork(run, true);
        Check(await Wait(run.DeviceCompletion), "successful ordinary work permits device cleanup");
        Check(calls.SequenceEqual(new[] { "game", "emulator" }), "game closes before emulator regardless of input order");
        Check(!exited.Task.IsCompleted, "application exit waits for queue stop");
        coordinator.Settle(run, true);
        await Wait(exited.Task);
        Check(calls.Last() == "exit", "application exit runs last");
        Check(coordinator.Begin("new", "other", [], false, () => { }) == null, "new runs blocked after exit reservation");

        var standalone = new EndTaskCoordinator(() => { });
        var closeOnly = Begin(standalone, "close-only", "device", [Action(0, "close-only", calls)]);
        standalone.CompleteWork(closeOnly, true);
        Check(await Wait(closeOnly.DeviceCompletion), "cleanup-only queue is executable");
        standalone.Settle(closeOnly, true);
    }

    private static async Task FailuresAndStops()
    {
        foreach (var stop in new[] { false, true })
        {
            var calls = new ConcurrentQueue<string>();
            var coordinator = new EndTaskCoordinator(() => calls.Enqueue("exit"));
            var run = Begin(coordinator, "failed", "same", [Action(0, "game", calls)], true);
            if (stop) coordinator.Cancel(run); else coordinator.CompleteWork(run, false);
            coordinator.CompleteWork(run, true);
            Check(!await Wait(run.DeviceCompletion), stop ? "manual stop cannot be overwritten by late success" : "ordinary failure cannot be overwritten by late success");
            coordinator.Settle(run, false);
            Check(calls.IsEmpty, "failed or stopped queue does not close anything");
        }
        foreach (var throws in new[] { false, true })
        {
            var calls = new ConcurrentQueue<string>();
            var exited = Signal();
            var coordinator = new EndTaskCoordinator(() => { calls.Enqueue("exit"); exited.TrySetResult(true); });
            var first = throws ? new EndTaskCoordinator.Cleanup(0, "game", () => throw new IOException("failure")) : Action(0, "game", calls, false);
            var run = Begin(coordinator, "failure", "device", [first, Action(1, "emulator", calls)], true);
            coordinator.CompleteWork(run, true);
            Check(await Wait(run.DeviceCompletion) && run.CleanupFailed,
                "a failed or throwing close action preserves its failure while finishing later cleanup");
            coordinator.Settle(run, true);
            await Wait(exited.Task);
            Check(calls.Contains("emulator") && calls.Last() == "exit",
                "a failed or throwing game close still attempts emulator close and application exit");
        }
    }

    private static async Task SharedDevice()
    {
        var calls = new ConcurrentQueue<string>();
        var coordinator = new EndTaskCoordinator(() => calls.Enqueue("exit"));
        var first = Begin(coordinator, "first", "device", [Action(1, "emulator", calls)]);
        var second = Begin(coordinator, "second", "device", [Action(0, "game", calls), Action(1, "emulator", calls)]);
        coordinator.CompleteWork(first, true);
        Check(!first.DeviceCompletion.IsCompleted && calls.IsEmpty, "shared device waits for other tab's ordinary work");
        coordinator.CompleteWork(second, true);
        Check(await Wait(first.DeviceCompletion) && await Wait(second.DeviceCompletion), "shared device wait has no mutual-wait deadlock");
        Check(calls.SequenceEqual(new[] { "game", "emulator" }), "shared device aggregates game first and deduplicates emulator close");
        coordinator.Settle(first, true);
        coordinator.Settle(second, true);

        var failed = Begin(coordinator, "failed-peer", "device");
        var waiting = Begin(coordinator, "waiting", "device", [Action(0, "must-not-run", calls)]);
        coordinator.CompleteWork(waiting, true);
        coordinator.CompleteWork(failed, false);
        Check(!await Wait(waiting.DeviceCompletion) && !calls.Contains("must-not-run"), "shared device failure cancels waiting close");
        coordinator.Settle(waiting, false);
        coordinator.Settle(failed, false);
    }

    private static async Task IndependentDevices()
    {
        var calls = new ConcurrentQueue<string>();
        var exited = Signal();
        var coordinator = new EndTaskCoordinator(() => exited.TrySetResult(true));
        var fast = Begin(coordinator, "fast", "device-a", [Action(0, "game-a", calls)], true);
        var slow = Begin(coordinator, "slow", "device-b");
        coordinator.CompleteWork(fast, true);
        Check(await Wait(fast.DeviceCompletion) && calls.Contains("game-a"), "different device does not delay game cleanup");
        coordinator.Settle(fast, true);
        Check(!exited.Task.IsCompleted, "assistant waits for another device's run");
        coordinator.CompleteWork(slow, true);
        await Wait(slow.DeviceCompletion);
        Check(!exited.Task.IsCompleted, "assistant also waits for another queue to stop");
        coordinator.Settle(slow, true);
        await Wait(exited.Task);
    }

    private static async Task LateRunAndFailedPeer()
    {
        var exited = Signal();
        var cancelled = Signal();
        var coordinator = new EndTaskCoordinator(() => exited.TrySetResult(true));
        var request = Begin(coordinator, "request", "a", exit: true, cancelled: () => cancelled.TrySetResult(true));
        var original = Begin(coordinator, "original", "b");
        coordinator.CompleteWork(request, true);
        await Wait(request.DeviceCompletion);
        coordinator.Settle(request, true);
        var late = Begin(coordinator, "late", "c");
        coordinator.CompleteWork(original, true);
        await Wait(original.DeviceCompletion);
        coordinator.Settle(original, true);
        Check(!exited.Task.IsCompleted, "newly started tabs join pending assistant exit");
        coordinator.Cancel(late);
        coordinator.Settle(late, false);
        await Wait(cancelled.Task);
        Check(!exited.Task.IsCompleted, "late manual stop cancels pending assistant exit");

        var pastFailure = Begin(coordinator, "past-failure", "b");
        var secondCancel = Signal();
        var next = Begin(coordinator, "next", "a", exit: true, cancelled: () => secondCancel.TrySetResult(true));
        coordinator.CompleteWork(pastFailure, false);
        coordinator.Settle(pastFailure, false);
        coordinator.CompleteWork(next, true);
        await Wait(next.DeviceCompletion);
        coordinator.Settle(next, true);
        await Wait(secondCancel.Task);
        Check(!exited.Task.IsCompleted, "overlapping failure remains visible after failed peer is retired");
    }

    private static async Task ClosingReservations()
    {
        var entered = Signal();
        var release = Signal();
        var calls = new ConcurrentQueue<string>();
        var coordinator = new EndTaskCoordinator(() => { });
        var run = Begin(coordinator, "closing", "device", [new(0, "game", async () =>
        {
            entered.TrySetResult(true);
            return await release.Task;
        }), Action(1, "emulator", calls)]);
        coordinator.CompleteWork(run, true);
        await Wait(entered.Task);
        Check(coordinator.Begin("same", "device", [], false, () => { }) == null, "same device cannot start during close");
        Check(coordinator.Begin("unknown", "", [], false, () => { }) == null, "unknown device cannot bypass a close reservation");
        var unrelated = Begin(coordinator, "other", "other-device");
        coordinator.Cancel(run);
        release.TrySetResult(true);
        Check(!await Wait(run.DeviceCompletion), "stop during close cancels the run");
        coordinator.Settle(run, false);
        coordinator.CompleteWork(unrelated, true);
        await Wait(unrelated.DeviceCompletion);
        coordinator.Settle(unrelated, true);
        // DeviceCompletion can be cancelled before the in-flight action unwinds.
        // Its later action is guarded again, and remains prohibited.
        Check(!calls.Contains("emulator"), "stop does not advance to the next close action");
    }

    private static async Task ConcurrentExitRequests()
    {
        var exited = Signal();
        var count = 0;
        var coordinator = new EndTaskCoordinator(() => { Interlocked.Increment(ref count); exited.TrySetResult(true); });
        var a = Begin(coordinator, "a", "a", exit: true);
        var b = Begin(coordinator, "b", "b", exit: true);
        coordinator.CompleteWork(a, true);
        coordinator.CompleteWork(b, true);
        await Wait(a.DeviceCompletion);
        await Wait(b.DeviceCompletion);
        coordinator.Settle(a, true);
        coordinator.Settle(b, true);
        await Wait(exited.Task);
        Check(count == 1, "multiple exit requests produce one exit without deadlock");
    }

    private static async Task StableCompletedResults()
    {
        var exited = Signal();
        var coordinator = new EndTaskCoordinator(() => exited.TrySetResult(true));
        var finished = Begin(coordinator, "finished", "a");
        var requester = Begin(coordinator, "requester", "b", exit: true);
        coordinator.CompleteWork(finished, true);
        await Wait(finished.DeviceCompletion);
        coordinator.Settle(finished, true);
        coordinator.Cancel(finished);
        coordinator.Settle(finished, false);
        coordinator.CompleteWork(requester, true);
        await Wait(requester.DeviceCompletion);
        coordinator.Settle(requester, true);
        await Wait(exited.Task);
        Check(true, "stopping an already completed ordinary tab does not revoke its successful result");

        var cancelled = Signal();
        coordinator = new EndTaskCoordinator(() => throw new Exception("must not exit"));
        var failed = Begin(coordinator, "failed-stop", "a");
        requester = Begin(coordinator, "requester", "b", exit: true, cancelled: () => cancelled.TrySetResult(true));
        coordinator.CompleteWork(failed, true);
        await Wait(failed.DeviceCompletion);
        coordinator.Settle(failed, false);
        coordinator.Settle(failed, true);
        coordinator.CompleteWork(requester, true);
        await Wait(requester.DeviceCompletion);
        coordinator.Settle(requester, true);
        await Wait(cancelled.Task);
        Check(true, "late duplicate stop callbacks cannot overwrite a failed stop");
    }

    private static async Task UnknownDeviceCohort()
    {
        var calls = new ConcurrentQueue<string>();
        var entered = Signal();
        var release = Signal();
        var coordinator = new EndTaskCoordinator(() => { });
        var a = Begin(coordinator, "a", "a", [new(0, "game", async () =>
        {
            calls.Enqueue("game-a");
            entered.TrySetResult(true);
            return await release.Task;
        }), new(1, "emulator", () => { calls.Enqueue("emulator-a"); return Task.FromResult(true); })]);
        var unknown = Begin(coordinator, "unknown", "", [new(0, "game", () =>
        {
            calls.Enqueue("game-unknown"); return Task.FromResult(true);
        })]);
        var b = Begin(coordinator, "b", "b", [new(0, "game", () =>
        {
            calls.Enqueue("game-b"); return Task.FromResult(true);
        }), new(1, "emulator", () => { calls.Enqueue("emulator-b"); return Task.FromResult(true); })]);
        coordinator.CompleteWork(a, true);
        coordinator.CompleteWork(unknown, true);
        Check(calls.IsEmpty, "unknown target keeps all related ordinary work ahead of cleanup");
        coordinator.CompleteWork(b, true);
        await Wait(entered.Task);
        Check(coordinator.Begin("another-b", "b", [], false, () => { }) == null,
            "mixed known and unknown cleanup reserves every potentially shared target");
        release.TrySetResult(true);
        Check(await Wait(a.DeviceCompletion) && await Wait(b.DeviceCompletion) && await Wait(unknown.DeviceCompletion),
            "known and unknown device cohort completes without deadlock");
        Check(calls.Count == 5 && calls.Take(3).All(call => call.StartsWith("game")) &&
            calls.Contains("emulator-a") && calls.Contains("emulator-b"),
            "unknown-device batching does not deduplicate actions belonging to different devices");
        coordinator.Settle(a, true);
        coordinator.Settle(b, true);
        coordinator.Settle(unknown, true);
    }
}
