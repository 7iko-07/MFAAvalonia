using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MFAAvalonia.Helper;

public static class RegressionCases
{
    private static int checks;
    private static void Check(bool value, string name)
    {
        if (!value) throw new Exception(name);
        checks++;
        Console.WriteLine("PASS " + name);
    }
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Wait(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(5));
    private static AgentContext Agent(Action cleanup) => new() { Cleanup = cleanup };

    public static async Task Run()
    {
        await DuplicateStopAndIndependentInstance();
        await AsyncFailure();
        await EarlyReturnAndCleanupFailure();
        await NormalCompletion();
        await ConcurrentAgentCleanup();
        Console.WriteLine($"{checks} checks passed; UI/native boundaries simulated, production stop and command methods executed.");
    }

    private static async Task DuplicateStopAndIndependentInstance()
    {
        using var processor = new MaaProcessor();
        using var release = new ManualResetEventSlim();
        var cleaning = Signal();
        var calls = 0;
        var agent = Agent(() =>
        {
            Interlocked.Increment(ref calls);
            cleaning.TrySetResult(true);
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("cleanup gate");
        });
        processor.Agents.Add(agent);
        // Reproduce the cancellation callback changing Status and scheduling a second stop.
        using var registration = processor.CancellationTokenSource.Token.Register(() =>
        {
            processor.Status = MFATask.MFATaskStatus.STOPPED;
            processor.Stop(MFATask.MFATaskStatus.STOPPED, finished: true);
        });
        processor.Stop(MFATask.MFATaskStatus.STOPPED);
        try
        {
            await Wait(cleaning.Task);
            Check(!processor.ViewModel.ToggleEnable, "button stays disabled while Agent cleanup is blocked");
            Check(agent.ForceKilled, "manual stop terminates Agent before waiting for cleanup");
            using var independent = new MaaProcessor();
            independent.Stop(MFATask.MFATaskStatus.STOPPED);
            await Wait(independent.Drain());
            Check(independent.ViewModel.ToggleEnable, "another instance can stop while the first is cleaning");
            Check(processor.ViewModel.ResetCount == 1 && calls == 1, "queued cancellation stop cannot overlap the first cleanup");
        }
        finally { release.Set(); }
        await Wait(processor.Drain());
        Check(calls == 1 && processor.Errors.Count == 0, "duplicate stop releases each Agent once without an enumeration failure");
        Check(processor.ViewModel.ToggleEnable && !processor.ViewModel.IsRunning, "button and running state recover after queued stops");
        Check(processor.Settlements.All(success => !success), "manual stop never enables end actions");
    }

    private static async Task AsyncFailure()
    {
        using var processor = new MaaProcessor();
        processor.MaaTasker = new MaaTasker
        {
            IsRunning = true,
            OnStop = call => call == 1 ? MaaJobStatus.Failed : throw new InvalidOperationException("after await")
        };
        var stopped = processor.RunStop(MFATask.MFATaskStatus.STOPPED);
        Check(!stopped.IsCompleted && !processor.ViewModel.ToggleEnable, "stop returns the pending asynchronous operation");
        await Wait(stopped);
        Check(processor.Errors.Single().Message == "after await", "failure after retry await reaches the normal stop error handler");
        Check(processor.ViewModel.ToggleEnable && !processor.ViewModel.IsRunning, "asynchronous stop failure still restores the button");
        Check(processor.Settlements.SequenceEqual(new[] { false }), "failed stop settles unsuccessfully");
    }

    private static async Task EarlyReturnAndCleanupFailure()
    {
        using var cancelled = new MaaProcessor();
        cancelled.CancellationTokenSource.Cancel();
        var cleaned = 0;
        cancelled.Agents.Add(Agent(() => cleaned++));
        await cancelled.RunStop(MFATask.MFATaskStatus.STOPPED);
        Check(cleaned == 1 && cancelled.ViewModel.ToggleEnable && !cancelled.ViewModel.IsRunning,
            "already-cancelled early return cleans Agents and restores the button");
        Check(cancelled.Settlements.SequenceEqual(new[] { false }), "early return settles unsuccessfully");

        using var failed = new MaaProcessor();
        failed.Agents.Add(Agent(() => throw new InvalidOperationException("cleanup failed")));
        await failed.RunStop(MFATask.MFATaskStatus.STOPPED);
        Check(failed.Errors.Single().Message == "cleanup failed", "Agent cleanup failure is caught");
        Check(failed.ViewModel.ToggleEnable && !failed.ViewModel.IsRunning, "cleanup failure restores the button");

        using var resultFailure = new MaaProcessor();
        resultFailure.ResultError = new InvalidOperationException("result failed");
        await resultFailure.RunStop(MFATask.MFATaskStatus.SUCCEEDED, finished: true);
        Check(resultFailure.Errors.Count == 1 && resultFailure.ViewModel.ToggleEnable,
            "completion callback failure also restores the button");
    }

    private static async Task NormalCompletion()
    {
        foreach (var status in new[] { MFATask.MFATaskStatus.SUCCEEDED, MFATask.MFATaskStatus.FAILED })
        {
            using var processor = new MaaProcessor();
            processor.Agents.Add(Agent(() => throw new Exception("normal completion must retain reusable Agents")));
            await processor.RunStop(status, finished: true);
            Check(processor.Agents.Count == 1 && processor.Settlements.SequenceEqual(new[] { true }),
                $"normal {status} completion retains Agents and existing end-action eligibility");
            Check(processor.ViewModel.ToggleEnable && !processor.ViewModel.IsRunning, $"normal {status} completion restores the button");
        }
    }

    private static async Task ConcurrentAgentCleanup()
    {
        var entered = Signal();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var contexts = new List<AgentContext>
        {
            Agent(() =>
            {
                Interlocked.Increment(ref calls);
                entered.TrySetResult(true);
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("agent gate");
            }),
            Agent(() => Interlocked.Increment(ref calls))
        };
        var first = Task.Run(() => AgentHelper.KillAllAgents(contexts));
        try
        {
            await Wait(entered.Task);
            await Wait(Task.Run(() => AgentHelper.KillAllAgents(contexts)));
            Check(contexts.Count == 0 && calls == 1, "concurrent cleanup takes no Agents already owned by the first caller");
        }
        finally { release.Set(); }
        await Wait(first);
        Check(calls == 2, "both Agents are released exactly once outside the collection lock");
    }
}

public partial class MaaProcessor : IDisposable
{
    private readonly Lock _stopLock = new();
    private readonly BlockingCollection<Func<Task>> _commandQueue = new();
    private readonly object _commandThreadLock = new();
    private readonly CancellationTokenSource _commandThreadCts = new();
    private Thread _commandThread;
    private int _taskQueueTotal;
    private readonly FakeRun _endTaskRun = new();
    private List<AgentContext> _agentContexts = new();
    private bool _agentStarted = true;
    private CancellationTokenSource _emulatorCancellationTokenSource;
    public CancellationTokenSource CancellationTokenSource { get; } = new();
    public MFATask.MFATaskStatus Status;
    public List<MFATask> TaskQueue { get; } = new();
    public FakeViewModel ViewModel { get; } = new();
    public string InstanceId => "test";
    public MaaTasker MaaTasker { get; set; }
    public static List<MaaProcessor> Processors { get; } = new();
    public List<AgentContext> Agents => _agentContexts;
    public List<Exception> Errors { get; } = new();
    public List<bool> Settlements => _endTaskRun.Settlements;
    public Exception ResultError;
    public Task RunStop(MFATask.MFATaskStatus status, bool finished = false) => StopInternal(status, finished, false, null);
    public Task Drain()
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EnqueueCommand(() => { done.SetResult(true); return Task.CompletedTask; });
        return done.Task;
    }
    public void Dispose() { StopCommandThread(); _commandThread?.Join(1000); }
    private IDisposable BeginInstanceLogScope(string operation, string source = "Runtime") => new Scope();
    private void ResetActionFailedCount() { }
    private void ClearTaskbarProgress() { }
    private void SetWaitingForModal(bool value) { }
    private void AddLogByKey(string key, IBrush brush) { }
    private void HandleStopResult(MFATask.MFATaskStatus status, MaaJobStatus result, bool onlyStart, Action action, bool update)
    { if (ResultError != null) throw ResultError; action?.Invoke(); }
    private void HandleStopException(Exception ex) => Errors.Add(ex);
    // PROCESSOR_METHODS
}

public static class AgentHelper
{
    public static void KillSingleAgent(AgentContext context, MaaTasker tasker = null) => context.Cleanup();
    private static void DisposeMaaTasker(MaaTasker tasker) { }
    // AGENT_METHODS
}
public class AgentContext { public bool ForceKilled; public FakeProcess Process; public Action Cleanup; }
public class FakeProcess { public bool HasExited; public void Kill(bool tree) { HasExited = true; } }
public enum MaaJobStatus { Succeeded, Failed, Invalid }
public class MaaTasker
{
    public bool IsRunning, IsStopping;
    private int calls;
    public Func<int, MaaJobStatus> OnStop = _ => MaaJobStatus.Succeeded;
    public FakeJob Stop() => new(() => OnStop(Interlocked.Increment(ref calls)));
}
public class FakeJob(Func<MaaJobStatus> result) { public MaaJobStatus Wait() => result(); }
public class MFATask
{
    public bool IsUpdateRelated;
    public enum MFATaskStatus { NOT_STARTED, STOPPING, STOPPED, SUCCEEDED, FAILED }
}
public class FakeViewModel
{
    public bool IsRunning = true, ToggleEnable = true;
    public int ResetCount;
    public bool Idle => !IsRunning;
    public void SetAdbRecoverySelectionLock(bool value) { }
    public void SetCurrentTaskName(string name) { ResetCount++; }
}
public class FakeRun { public List<bool> Settlements { get; } = new(); }
public static class EndTasks
{
    public static void Cancel(FakeRun run) { }
    public static void Settle(FakeRun run, bool success) => run.Settlements.Add(success);
}
public interface IBrush { }
public sealed class Scope : IDisposable { public void Dispose() { } }
public static class Instances
{
    public static FakeRoot RootViewModel { get; } = new();
    public static FakeTabs InstanceTabBarViewModel { get; } = new();
}
public class FakeRoot { public bool IsRunning, Idle; }
public class FakeTabs { public FakeTab ActiveTab; }
public class FakeTab { public FakeViewModel TaskQueueViewModel; }
public static class LangKeys { public const string NoTaskToStop = "NoTaskToStop", Stopping = "Stopping"; }
public static class Extensions
{
    public static string ToLocalization(this string text) => text;
    public static void SafeCancel(this CancellationTokenSource source) => source?.Cancel();
}
namespace MFAAvalonia.Helper
{
    public static class DispatcherHelper { public static void PostOnMainThread(Action action) => action(); }
    public static class ToastHelper { public static void Warn(string message, string instanceId) { } }
    public static class LoggerHelper
    {
        public static void Info(object message) { }
        public static void Error(string message, Exception error) { }
        public static void Warning(string message) { }
    }
}
