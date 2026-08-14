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

    /// <summary>当前代际的值（不增加租约；仅比较用，不保证存活——调用方不得持有）。</summary>
    public T? Current
    {
        get
        {
            lock (_sync) return _current?.Value;
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

    public async ValueTask DisposeAsync() => await DisposeAsync(DefaultDisposeTimeout);

    /// <summary>
    /// 带超时的释放：租约泄漏（调用方未释放）或底层释放卡死（如 provider 网络调用不响应
    /// 取消）时强制回收，防退出/恢复出厂挂死。
    /// 修复一：原实现无限等待全部租约释放，任何泄漏路径都会让 DisposeAsync 永久挂起。
    /// 修复二：原实现超时后仍 `await Task.WhenAll(pending.Concat(...))`——pending 含超时前
    /// 已卡住的 entry（DisposeStarted=true），强制路径不会重试它，WhenAll 永久挂起，
    /// 超时兜底形同虚设。现改为超时后不再等待未完成的 entry，只对未启动的 entry 强制释放，
    /// 并再等待一个短窗口；仍不完成则放弃，交由进程退出回收。
    /// </summary>
    public async ValueTask DisposeAsync(TimeSpan timeout)
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
        if (pending.Length == 0) return;

        var all = Task.WhenAll(pending);
        var completed = await Task.WhenAny(
            all,
            Task.Delay(timeout > TimeSpan.Zero ? timeout : DefaultDisposeTimeout)).ConfigureAwait(false);
        if (completed == all)
        {
            await all.ConfigureAwait(false);
            return;
        }

        // 超时：不再等待超时前已启动的 entry（可能卡死且强制无法解救），只对未启动的
        // entry 强制释放；泄漏租约后续 Release 幂等无害（DisposeStarted 已置位，不会二次 Dispose）。
        List<Entry> forced = [];
        lock (_sync)
        {
            foreach (var entry in _entries)
            {
                if (!entry.DisposeStarted)
                {
                    entry.DisposeStarted = true;
                    forced.Add(entry);
                }
            }
        }
        foreach (var entry in forced) _ = DisposeEntryAsync(entry);
        if (forced.Count == 0) return;

        // 只等新启动的强制释放（刚启动的 entry 正常应快速完成），再给短窗口兜底后放弃。
        var forcedAll = Task.WhenAll(forced.Select(entry => entry.Disposed.Task));
        await Task.WhenAny(forcedAll, Task.Delay(ForcedDisposeTimeout)).ConfigureAwait(false);
    }

    /// <summary>强制释放后的额外等待窗口：超过即放弃（进程退出即回收，不能阻塞退出）。</summary>
    private static readonly TimeSpan ForcedDisposeTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan DefaultDisposeTimeout = TimeSpan.FromSeconds(30);

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
