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
    /// <summary>队列看门狗间隔：清理已过排队预算但无人取走的 P0（全忙场景）。</summary>
    private static readonly TimeSpan QueueBudgetCheckInterval = TimeSpan.FromMilliseconds(250);

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
        // P0 排队预算从入队起算：worker 全忙时排队等待也受对话超时约束。
        // 修复：原实现 deadline 在 worker 取到任务后才创建，P0 对话可无限排队
        // （最坏 ~90s 无响应），与架构 §3.3「对话不能等」矛盾。
        // 预算只约束排队：取到后执行/重试仍按每次尝试独立 deadline。
        if (priority == RequestPriority.Conversation)
        {
            job.ConversationQueueBudgetDeadlineUtc = DateTime.UtcNow + _conversationTimeout;
        }
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
    /// 有界并行 worker 池：worker 数 = concurrency + 1（最后一个为 P0 预留槽位）。
    /// 每个 worker 独立 dequeue → 执行 → 再取；任何时刻在飞请求 ≤ concurrency。
    /// 空闲 worker 总是取队列中最高优先级（P0 插队语义由队列保证）。
    /// 修复：原实现 worker 数 = concurrency，全忙时 P0 对话必须排队等待；
    /// 现在预留一个只服务 P0 的 worker，P0 到达即执行（P1/P2 永远占不满它）。
    /// </summary>
    private async Task RunLoopAsync(CancellationToken ct)
    {
        var workerCount = Math.Max(1, _concurrency) + 1;
        var workers = new Task[workerCount + 1];
        for (var i = 0; i < workerCount; i++)
        {
            workers[i] = WorkerAsync(ct, reservedForConversation: i == workerCount - 1);
        }
        workers[workerCount] = QueueWatchdogAsync(ct);
        try
        {
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>周期性清理已过排队预算的 P0（worker 全忙时无人取走它，必须主动失败）。
    /// 队列头是最高优先级，过期的 P0 必然在队头（P0 永不被 P1/P2 压住）。</summary>
    private async Task QueueWatchdogAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(QueueBudgetCheckInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            ExpireOverdueQueuedConversations();
        }
    }

    private void ExpireOverdueQueuedConversations()
    {
        List<Job> expired = [];
        lock (_queueLock)
        {
            while (_queue.TryPeek(out var top, out _)
                   && top.Priority == RequestPriority.Conversation
                   && top.ConversationQueueBudgetDeadlineUtc is { } deadline
                   && DateTime.UtcNow > deadline)
            {
                _queue.TryDequeue(out var job, out _);
                if (job is not null) expired.Add(job);
            }
        }
        foreach (var job in expired)
        {
            job.Completion.TrySetException(CreateTimeoutException(_conversationTimeout, null));
        }
    }

    private async Task WorkerAsync(CancellationToken ct, bool reservedForConversation)
    {
        while (!ct.IsCancellationRequested)
        {
            Job? job;
            lock (_queueLock)
            {
                if (reservedForConversation)
                {
                    // 预留 worker 只取 P0；队列头非 P0 时保持等待（不消费 P1/P2）。
                    if (_queue.TryPeek(out var top, out _)
                        && top.Priority == RequestPriority.Conversation)
                    {
                        _queue.TryDequeue(out job, out _);
                    }
                    else
                    {
                        job = null;
                    }
                }
                else if (!_queue.TryDequeue(out job, out _))
                {
                    job = null;
                }
            }

            if (job is null)
            {
                try { await _wake.WaitAsync(ct); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            // P0 排队预算：入队后等待超过对话超时 → 直接超时（不开始执行）。
            if (job.ConversationQueueBudgetDeadlineUtc is { } budgetDeadline
                && DateTime.UtcNow > budgetDeadline)
            {
                job.Completion.TrySetException(CreateTimeoutException(_conversationTimeout, null));
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

    /// <summary>按优先级策略执行：由调度器拥有截止时间；超时按对话策略重试。
    /// 执行/重试每次尝试独立 deadline（预算只约束排队，见 WorkerAsync）。</summary>
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

    private static ProviderException CreateTimeoutException(TimeSpan timeout, Exception? inner)
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

        /// <summary>P0 专属：排队预算截止时刻（入队时间 + conversationTimeout）。
        /// worker 取到前超时 → 直接失败；非 P0 为 null（尽力语义）。</summary>
        public DateTime? ConversationQueueBudgetDeadlineUtc;
    }
}
