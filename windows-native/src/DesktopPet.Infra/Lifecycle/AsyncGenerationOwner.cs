namespace DesktopPet.Infra.Lifecycle;

/// <summary>
/// Publishes immutable runtime generations while allowing in-flight callers to finish on the
/// generation they acquired. Retired generations are disposed exactly once after their final lease.
/// </summary>
public sealed class AsyncGenerationOwner<T> : IAsyncDisposable where T : class, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly HashSet<Entry> _entries = [];
    private Entry? _current;
    private bool _disposed;

    public GenerationLease? Acquire()
    {
        lock (_sync)
        {
            if (_disposed || _current is null) return null;
            _current.LeaseCount++;
            return new GenerationLease(this, _current);
        }
    }

    /// <summary>
    /// Publishes <paramref name="next"/> immediately and returns a task that completes after the
    /// previous generation has drained and been disposed.
    /// </summary>
    public Task ReplaceAsync(T? next)
    {
        Entry? retired;
        Entry? disposeNow = null;
        lock (_sync)
        {
            if (_disposed) return DisposeRejectedAsync(next);

            retired = _current;
            _current = next is null ? null : new Entry(next);
            if (_current is not null) _entries.Add(_current);

            if (retired is not null)
            {
                retired.Retired = true;
                if (retired.LeaseCount == 0 && !retired.DisposeStarted)
                {
                    retired.DisposeStarted = true;
                    disposeNow = retired;
                }
            }
        }

        if (disposeNow is not null) _ = DisposeEntryAsync(disposeNow);
        return retired?.Disposed.Task ?? Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        Entry? disposeNow = null;
        lock (_sync)
        {
            _disposed = true;
            if (_current is not null)
            {
                _current.Retired = true;
                if (_current.LeaseCount == 0 && !_current.DisposeStarted)
                {
                    _current.DisposeStarted = true;
                    disposeNow = _current;
                }
                _current = null;
            }
            pending = _entries.Select(entry => entry.Disposed.Task).ToArray();
        }

        if (disposeNow is not null) _ = DisposeEntryAsync(disposeNow);
        if (pending.Length > 0) await Task.WhenAll(pending).ConfigureAwait(false);
    }

    private void Release(Entry entry)
    {
        Entry? disposeNow = null;
        lock (_sync)
        {
            if (entry.LeaseCount <= 0) return;
            entry.LeaseCount--;
            if (entry.Retired && entry.LeaseCount == 0 && !entry.DisposeStarted)
            {
                entry.DisposeStarted = true;
                disposeNow = entry;
            }
        }

        if (disposeNow is not null) _ = DisposeEntryAsync(disposeNow);
    }

    private async Task DisposeEntryAsync(Entry entry)
    {
        try
        {
            await entry.Value.DisposeAsync().ConfigureAwait(false);
            entry.Disposed.TrySetResult(true);
        }
        catch (Exception ex)
        {
            entry.Disposed.TrySetException(ex);
        }
        finally
        {
            lock (_sync) _entries.Remove(entry);
        }
    }

    private static async Task DisposeRejectedAsync(T? value)
    {
        if (value is not null) await value.DisposeAsync().ConfigureAwait(false);
        throw new ObjectDisposedException(nameof(AsyncGenerationOwner<T>));
    }

    internal sealed class Entry(T value)
    {
        public T Value { get; } = value;
        public int LeaseCount { get; set; }
        public bool Retired { get; set; }
        public bool DisposeStarted { get; set; }
        public TaskCompletionSource<bool> Disposed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class GenerationLease : IDisposable
    {
        private AsyncGenerationOwner<T>? _owner;
        private readonly Entry _entry;

        internal GenerationLease(AsyncGenerationOwner<T> owner, Entry entry)
        {
            _owner = owner;
            _entry = entry;
        }

        public T Value => _entry.Value;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(_entry);
        }
    }
}
