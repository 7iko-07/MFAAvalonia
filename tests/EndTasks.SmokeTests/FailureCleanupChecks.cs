using MFAAvalonia.Helper;
using System.Collections.Concurrent;

static partial class Program
{
    private static async Task ContinueOnErrorCleanup()
    {
        var exited = Signal();
        var calls = new ConcurrentQueue<string>();
        var coordinator = new EndTaskCoordinator(() => exited.TrySetResult(true));
        var run = Begin(coordinator, "failed-config", "device",
            [Action(0, "game", calls), Action(1, "emulator", calls)], exit: true, continueOnError: true);
        coordinator.CompleteWork(run, false);
        Check(await Wait(run.DeviceCompletion), "automatic failure permits cleanup when continue-on-error is enabled");
        Check(calls.SequenceEqual(new[] { "game", "emulator" }), "failed-run cleanup retains closing order");
        Check(!exited.Task.IsCompleted, "failed-run cleanup waits for queue stop before exit");
        coordinator.Settle(run, true);
        await Wait(exited.Task);
        Check(true, "failed automatic run exits through the existing shutdown callback");
    }

    private static async Task MixedConfigurationPolicies()
    {
        foreach (var sameDevice in new[] { false, true })
        {
            var calls = new ConcurrentQueue<string>();
            var cancelled = Signal();
            var coordinator = new EndTaskCoordinator(() => calls.Enqueue("exit"));
            var enabled = Begin(coordinator, "enabled", "a", [Action(0, "game", calls)], true,
                () => cancelled.TrySetResult(true), continueOnError: true);
            var disabled = Begin(coordinator, "disabled", sameDevice ? "a" : "b");
            coordinator.CompleteWork(enabled, false);
            if (sameDevice) Check(!enabled.DeviceCompletion.IsCompleted, "allowed failure still waits for a same-device peer");
            coordinator.CompleteWork(disabled, false);
            Check(!await Wait(disabled.DeviceCompletion), "disabled configuration still blocks cleanup after failure");
            Check(await Wait(enabled.DeviceCompletion) == !sameDevice,
                sameDevice ? "disabled same-device failure blocks another configuration's close" :
                "different-device failure does not block permitted game cleanup");
            coordinator.Settle(disabled, false);
            coordinator.Settle(enabled, !sameDevice);
            await Wait(cancelled.Task);
            Check(!calls.Contains("exit"), "one enabled configuration cannot override another configuration's failure policy");
        }

        var combined = new ConcurrentQueue<string>();
        var exited = Signal();
        var both = new EndTaskCoordinator(() => exited.TrySetResult(true));
        var first = Begin(both, "first", "device", [Action(1, "emulator", combined)], true, continueOnError: true);
        var second = Begin(both, "second", "device", [Action(0, "game", combined)], true, continueOnError: true);
        both.CompleteWork(first, false);
        both.CompleteWork(second, false);
        Check(await Wait(first.DeviceCompletion) && await Wait(second.DeviceCompletion),
            "two enabled failed configurations can complete shared-device cleanup");
        Check(combined.SequenceEqual(new[] { "game", "emulator" }), "failed-run batches retain game-before-emulator ordering");
        both.Settle(first, true);
        both.Settle(second, true);
        await Wait(exited.Task);
    }

    private static async Task FailureModeProtections()
    {
        {
            var calls = new ConcurrentQueue<string>();
            var coordinator = new EndTaskCoordinator(() => calls.Enqueue("exit"));
            var run = Begin(coordinator, "manual-stop", "device",
                [Action(0, "game", calls), Action(1, "emulator", calls)], true, continueOnError: true);
            coordinator.Cancel(run);
            coordinator.CompleteWork(run, false);
            Check(!await Wait(run.DeviceCompletion), "failure-mode cleanup respects manual-stop");
            coordinator.Settle(run, false);
            Check(!calls.Contains("exit"), "continue-on-error cannot bypass manual-stop");
            Check(calls.IsEmpty, "no device close occurs after manual-stop");
        }

        foreach (var continueOnError in new[] { false, true })
        {
            var calls = new ConcurrentQueue<string>();
            var exited = Signal();
            var coordinator = new EndTaskCoordinator(() => { calls.Enqueue("exit"); exited.TrySetResult(true); });
            var run = Begin(coordinator, "close-failure", "device",
                [Action(0, "game", calls, false), Action(1, "emulator", calls, false)], true,
                continueOnError: continueOnError);
            coordinator.CompleteWork(run, true);
            Check(await Wait(run.DeviceCompletion) && run.CleanupFailed,
                $"both failed close actions finish without cancelling cleanup (continue={continueOnError})");
            Check(calls.SequenceEqual(new[] { "game", "emulator" }),
                "the emulator is attempted after game close fails");
            coordinator.Settle(run, true);
            await Wait(exited.Task);
            Check(calls.Last() == "exit", "application exit remains eligible after close failures under either policy");
        }
    }
}
