using MFAAvalonia.Helper;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
static async Task<T> Complete<T>(Task<T> task) => await task.WaitAsync(TimeSpan.FromSeconds(5));

var tests = new (string Name, Func<Task> Run)[]
{
    ("Prewarm returns before synchronous initialization, first readers share it", async () =>
    {
        var errors = new List<Exception>();
        var cache = new BackgroundResource<object>(_ => throw new Exception("Unexpected release"), errors.Add);
        var started = Gate<bool>();
        using var unblock = new ManualResetEventSlim();
        var expected = new object();
        var calls = 0;
        Task<object?> Factory(CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            started.SetResult(true);
            if (!unblock.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Caller was blocked by prewarm");
            return Task.FromResult<object?>(expected);
        }
        var pending = cache.GetOrCreateAsync(Factory, default);
        try
        {
            await Complete(started.Task);
            Check(!pending.IsCompleted, "Initialization unexpectedly complete");
            for (var i = 0; i < 10; ++i)
                Check(ReferenceEquals(pending, cache.GetOrCreateAsync(Factory, default)), "Duplicate initialization");
        }
        finally { unblock.Set(); }
        Check(ReferenceEquals(await Complete(pending), expected), "Wrong resource");
        Check(ReferenceEquals(await Complete(cache.GetOrCreateAsync(Factory, default)), expected), "Published value not reused");
        Check(calls == 1 && errors.Count == 0, "Factory reran or failed");
    }),
    ("Old connection completing cannot overwrite or clear new initialization", async () =>
    {
        var released = new List<object>();
        var cache = new BackgroundResource<object>(released.Add, ex => throw ex);
        var oldStarted = Gate<bool>();
        var oldGate = Gate<object?>();
        var oldTask = cache.GetOrCreateAsync(_ => { oldStarted.SetResult(true); return oldGate.Task; }, default);
        await Complete(oldStarted.Task);
        Check(cache.Reset() == null, "Pending value published early");
        var newGate = Gate<object?>();
        var newTask = cache.GetOrCreateAsync(_ => newGate.Task, default);
        var oldValue = new object();
        oldGate.SetResult(oldValue);
        Check(await Complete(oldTask) == null, "Stale value returned to reader");
        Check(released.Count == 1 && ReferenceEquals(released[0], oldValue), "Stale value not released once");
        Check(ReferenceEquals(newTask, cache.GetOrCreateAsync(_ => throw new Exception("Duplicate"), default)), "Old completion cleared new flight");
        var newValue = new object();
        newGate.SetResult(newValue);
        Check(ReferenceEquals(await Complete(newTask), newValue) && ReferenceEquals(cache.Value, newValue), "New value lost");
    }),
    ("Late old completion leaves already-published new connection intact", async () =>
    {
        var released = new List<object>();
        var cache = new BackgroundResource<object>(released.Add, ex => throw ex);
        var started = Gate<bool>();
        var gate = Gate<object?>();
        var old = cache.GetOrCreateAsync(_ => { started.SetResult(true); return gate.Task; }, default);
        await Complete(started.Task);
        cache.Reset();
        var current = new object();
        await Complete(cache.GetOrCreateAsync(_ => Task.FromResult<object?>(current), default));
        gate.SetResult(new object());
        await Complete(old);
        Check(ReferenceEquals(cache.Value, current) && released.Count == 1, "Late completion replaced live resource");
        Check(ReferenceEquals(cache.Reset(), current) && cache.Reset() == null, "Reset did not transfer ownership once");
        Check(released.Count == 1, "Published resource disposed before caller retired it");
    }),
    ("Cancellation discards non-cancellable native result and permits retry", async () =>
    {
        var released = new List<object>();
        var cache = new BackgroundResource<object>(released.Add, ex => throw ex);
        using var cancellation = new CancellationTokenSource();
        var started = Gate<bool>();
        var gate = Gate<object?>();
        var pending = cache.GetOrCreateAsync(_ => { started.SetResult(true); return gate.Task; }, cancellation.Token);
        await Complete(started.Task);
        cancellation.Cancel();
        gate.SetResult(new object());
        Check(await Complete(pending) == null && cache.Value == null && released.Count == 1, "Cancelled resource leaked/published");
        Check(await Complete(cache.GetOrCreateAsync(_ => Task.FromResult<object?>(new object()), default)) != null, "Retry failed");
    }),
    ("Failures are observed and null or faulted initialization can retry", async () =>
    {
        var errors = new List<Exception>();
        var cache = new BackgroundResource<object>(_ => { }, errors.Add);
        Check(await Complete(cache.GetOrCreateAsync(_ => throw new InvalidOperationException("Expected failure"), default)) == null, "Failure not handled");
        Check(errors.Count == 1, "Background failure not reported");
        Check(await Complete(cache.GetOrCreateAsync(_ => Task.FromResult<object?>(null), default)) == null, "Null result changed");
        Check(await Complete(cache.GetOrCreateAsync(_ => Task.FromResult<object?>(new object()), default)) != null, "Failure prevented retry");
    }),
    ("Already-cancelled request never starts initialization", async () =>
    {
        var cache = new BackgroundResource<object>(_ => { }, ex => throw ex);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = 0;
        try
        {
            await cache.GetOrCreateAsync(_ => { calls++; return Task.FromResult<object?>(new object()); }, cancellation.Token);
            throw new Exception("Cancellation not propagated");
        }
        catch (OperationCanceledException) { }
        Check(calls == 0, "Cancelled factory ran");
    }),
};

foreach (var (name, run) in tests)
{
    await run();
    Console.WriteLine($"PASS {name}");
}
Console.WriteLine($"{tests.Length} startup lifecycle checks passed.");
