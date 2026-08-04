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

    private readonly IModelProvider _provider;
    private readonly SemaphoreSlim _gate;
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
    private long _seq;
    private Task? _loop;

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
        _gate = new SemaphoreSlim(concurrency, concurrency);
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
            _queue.Enqueue(job, (-(int)priority, Interlocked.Increment(ref _seq)));
        }
        _loop ??= Task.Run(() => RunLoopAsync(_loopCts.Token));
        _wake.Release();
        return job.Completion.Task;
    }

    private async Task RunLoopAsync(CancellationToken ct)
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

            await _gate.WaitAsync(ct);
            try
            {
                var result = await ExecuteWithPolicyAsync(job);
                job.Completion.TrySetResult(result);
            }
            catch (OperationCanceledException) when (job.Ct.IsCancellationRequested)
            {
                job.Completion.TrySetCanceled(job.Ct);
            }
            catch (Exception ex)
            {
                job.Completion.TrySetException(ex);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    /// <summary>按优先级策略执行：超时 + 对话重试（指数退避 2 次）。</summary>
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
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Ct);
            linked.CancelAfter(timeout);
            try
            {
                return await _provider.CompleteAsync(job.Request, linked.Token);
            }
            catch (OperationCanceledException) when (!job.Ct.IsCancellationRequested && attempt < maxRetries)
            {
                // 超时（非外部取消）且还有重试次数 → 指数退避后重试
                await Task.Delay(TimeSpan.FromMilliseconds(_backoffBaseMs * (1 << attempt)), job.Ct);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _loopCts.Cancel();
        try { if (_loop is not null) await _loop; } catch (OperationCanceledException) { }
        _wake.Dispose();
        _gate.Dispose();
        _loopCts.Dispose();
    }

    private sealed record Job(RequestPriority Priority, ChatRequest Request, CancellationToken Ct)
    {
        public TaskCompletionSource<ChatResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
