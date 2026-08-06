namespace DesktopPet.Core.Scheduling;

/// <summary>
/// 请求优先级（架构文档 §3.3）：P0 对话 / P1 主动互动 / P2 后台。
/// 数值越大优先级越高。
/// </summary>
public enum RequestPriority
{
    /// <summary>P2：每日总结/画像更新等后台工作（可让路）。</summary>
    Background = 0,

    /// <summary>P1：主动互动（多宠物并行，事件驱动；8s 超时，超时跳过本轮不重试）。</summary>
    Interactive = 1,

    /// <summary>P0：用户对话（交互，不能等；30s 超时，指数退避重试 2 次）。</summary>
    Conversation = 2,
}

/// <summary>
/// 模型请求调度器（架构文档 §3.3）：
/// 优先级队列（P0 插队）+ SemaphoreSlim 并发闸（默认 3，per-provider 实例一个调度器）。
/// 规则：P0 永不被主动互动阻塞（队列取最高优先级）；互动超时跳过本轮；
/// 对话超时走指数退避重试（2 次）；外部取消不重试。
/// </summary>
public sealed class ModelRequestScheduler : IAsyncDisposable
{
    public static readonly TimeSpan DefaultConversationTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultInteractiveTimeout = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan DefaultBackgroundTimeout = TimeSpan.FromSeconds(60);
    public const int DefaultConcurrency = 3;
    private static readonly TimeSpan ProviderCancellationDrainTimeout = TimeSpan.FromMilliseconds(250);

    private readonly IModelProvider _provider;
    private readonly int _concurrency;
    private readonly TimeSpan _conversationTimeout;
    private readonly TimeSpan _interactiveTimeout;
    private readonly TimeSpan _backgroundTimeout;
    private readonly int _backoffBaseMs;

    // 队列优先级 = (-priority, seq)：最小堆先弹 P0（-2 < -1 < 0），同优先级按 seq FIFO
    private readonly PriorityQueue<Job, (int Priority, long Seq)> _queue = new();
    // 唤醒信号（"可能有活干"）：残留无害（worker 空转一次再阻塞），计数取 int.MaxValue 防溢出
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly CancellationTokenSource _loopCts = new();
    private readonly object _queueLock = new();
    private readonly object _disposeSync = new();
    private Task? _disposeTask;
    private bool _disposed;
    private long _seq;
    private Task? _loop; // 初始化在锁内（防并发 Enqueue 双启动 worker 池）

    public ModelRequestScheduler(
        IModelProvider provider,
        int concurrency = DefaultConcurrency,
        TimeSpan? conversationTimeout = null,
        TimeSpan? interactiveTimeout = null,
        TimeSpan? backgroundTimeout = null,
        int backoffBaseMs = 500)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        if (concurrency < 1) throw new ArgumentOutOfRangeException(nameof(concurrency));
        _concurrency = concurrency;
        _conversationTimeout = conversationTimeout ?? DefaultConversationTimeout;
        _interactiveTimeout = interactiveTimeout ?? DefaultInteractiveTimeout;
        _backgroundTimeout = backgroundTimeout ?? DefaultBackgroundTimeout;
        _backoffBaseMs = Math.Max(1, backoffBaseMs);
    }

    /// <summary>入队一个请求；返回的 Task 在完成/失败/取消时结束。</summary>
    public Task<ChatResult> EnqueueAsync(
        RequestPriority priority, ChatRequest request, CancellationToken ct = default)
    {
        var job = new Job(priority, request, ct);
        lock (_queueLock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ModelRequestScheduler));
            _queue.Enqueue(job, (-(int)priority, Interlocked.Increment(ref _seq)));
            _loop ??= Task.Run(() => RunLoopAsync(_loopCts.Token)); // 锁内初始化：并发 Enqueue 不双启
            _wake.Release();
        }
        return job.Completion.Task;
    }

    /// <summary>
    /// 有界并行 worker 池（worker 数 = concurrency）：每个 worker 独立 dequeue → 执行 → 再取，
    /// 任何时刻在飞请求 ≤ concurrency；空闲 worker 总是取队列中最高优先级（P0 插队语义由队列保证，
    /// 不被先入队的 P1/P2 阻塞）。修复：原单 worker 循环把请求全部串行化，并发闸形同虚设。
    /// </summary>
    private async Task RunLoopAsync(CancellationToken ct)
    {
        var workers = new Task[Math.Max(1, _concurrency)];
        for (var i = 0; i < workers.Length; i++)
        {
            workers[i] = WorkerAsync(ct);
        }
        try
        {
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task WorkerAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Job? job;
            lock (_queueLock)
            {
                _queue.TryDequeue(out job, out _);
            }

            if (job is null)
            {
                try { await _wake.WaitAsync(ct); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                var result = await ExecuteWithPolicyAsync(job);
                job.Completion.TrySetResult(result);
            }
            catch (OperationCanceledException) when (job.Ct.IsCancellationRequested)
            {
                job.Completion.TrySetCanceled(job.Ct);
            }
            catch (OperationCanceledException) when (_loopCts.IsCancellationRequested)
            {
                job.Completion.TrySetCanceled(_loopCts.Token);
            }
            catch (Exception ex)
            {
                job.Completion.TrySetException(ex);
            }
        }
    }

    /// <summary>按优先级策略执行：由调度器拥有截止时间；超时按对话策略重试。</summary>
    private async Task<ChatResult> ExecuteWithPolicyAsync(Job job)
    {
        var timeout = job.Priority switch
        {
            RequestPriority.Conversation => _conversationTimeout,
            RequestPriority.Interactive => _interactiveTimeout,
            _ => _backgroundTimeout,
        };
        var maxRetries = job.Priority == RequestPriority.Conversation ? 2 : 0;

        for (var attempt = 0; ; attempt++)
        {
            using var deadline = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                job.Ct, _loopCts.Token, deadline.Token);
            Task<ChatResult>? operation = null;
            try
            {
                operation = _provider.CompleteAsync(job.Request, linked.Token);
                return await operation.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (ProviderException ex) when (ex.Code == "timeout")
            {
                job.Ct.ThrowIfCancellationRequested();
                _loopCts.Token.ThrowIfCancellationRequested();
                if (attempt >= maxRetries) throw CreateTimeoutException(timeout, ex);
                await DelayBeforeRetryAsync(job, attempt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (job.Ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (_loopCts.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex) when (deadline.IsCancellationRequested)
            {
                using (var ownerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                           job.Ct, _loopCts.Token))
                {
                    await DrainCanceledOperationAsync(
                        operation,
                        ownerCancellation.Token).ConfigureAwait(false);
                }
                job.Ct.ThrowIfCancellationRequested();
                _loopCts.Token.ThrowIfCancellationRequested();
                // 不响应取消的 Provider 已有一个悬挂调用，不能通过重试放大并发泄漏。
                if (operation is { IsCompleted: false } || attempt >= maxRetries)
                    throw CreateTimeoutException(timeout, ex);
                await DelayBeforeRetryAsync(job, attempt).ConfigureAwait(false);
            }
            finally
            {
                if (operation is not null && linked.IsCancellationRequested)
                    ObserveDetachedTask(operation);
            }
        }
    }

    private static async Task DrainCanceledOperationAsync(
        Task? operation,
        CancellationToken ownerCancellation)
    {
        if (operation is null || operation.IsCompleted) return;
        await Task.WhenAny(
            operation,
            Task.Delay(ProviderCancellationDrainTimeout, ownerCancellation)).ConfigureAwait(false);
    }

    private static void ObserveDetachedTask(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task DelayBeforeRetryAsync(Job job, int attempt)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Ct, _loopCts.Token);
        await Task.Delay(
            TimeSpan.FromMilliseconds(_backoffBaseMs * (1 << attempt)),
            linked.Token).ConfigureAwait(false);
    }

    private static ProviderException CreateTimeoutException(TimeSpan timeout, Exception inner)
        => new("timeout", $"模型请求超时（{timeout.TotalSeconds:0.#} 秒）", inner);

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        List<Job> queued = [];
        Task? loop;
        lock (_queueLock)
        {
            _disposed = true;
            while (_queue.TryDequeue(out var job, out _)) queued.Add(job);
            loop = _loop;
        }

        _loopCts.Cancel();
        foreach (var job in queued) job.Completion.TrySetCanceled(_loopCts.Token);
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _wake.Dispose();
        _loopCts.Dispose();
    }

    private sealed record Job(RequestPriority Priority, ChatRequest Request, CancellationToken Ct)
    {
        public TaskCompletionSource<ChatResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
