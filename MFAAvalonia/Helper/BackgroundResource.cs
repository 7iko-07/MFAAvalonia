using System;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Helper;

/// <summary>
/// Shares one background initialization per connection generation. A reset prevents
/// an older, still-running native initializer from publishing into the new connection.
/// </summary>
internal sealed class BackgroundResource<T>(Action<T> release, Action<Exception> reportError) where T : class
{
    private readonly object _sync = new();
    private T? _value;
    private Task<T?>? _pending;
    private long _generation;

    public T? Value
    {
        get { lock (_sync) return _value; }
    }

    public Task<T?> GetOrCreateAsync(Func<CancellationToken, Task<T?>> initialize, CancellationToken token)
    {
        lock (_sync)
        {
            if (token.IsCancellationRequested)
                return Task.FromCanceled<T?>(token);
            if (_value != null)
                return Task.FromResult<T?>(_value);

            var generation = _generation;
            // Do not run any synchronous portion of the initializer on the caller.
            return _pending ??= Task.Run(() => InitializeAsync(initialize, generation, token));
        }
    }

    public T? Reset()
    {
        lock (_sync)
        {
            ++_generation;
            _pending = null;
            var value = _value;
            _value = null;
            // The caller owns retirement of a published value (it may still be in use).
            return value;
        }
    }

    private async Task<T?> InitializeAsync(Func<CancellationToken, Task<T?>> initialize, long generation, CancellationToken token)
    {
        T? value = null;
        try
        {
            lock (_sync)
            {
                if (generation != _generation)
                    return null;
            }
            token.ThrowIfCancellationRequested();
            value = await initialize(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { reportError(ex); }

        lock (_sync)
        {
            if (generation == _generation)
            {
                _pending = null;
                if (!token.IsCancellationRequested)
                {
                    _value = value;
                    return value;
                }
            }
        }

        // An unpublished result has no readers and can be released immediately.
        if (value != null)
        {
            try { release(value); }
            catch (Exception ex) { reportError(ex); }
        }
        return null;
    }
}
