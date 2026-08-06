using DesktopPet.Core.Scheduling;

namespace DesktopPet.Core.Tests;

/// <summary>
/// Phase 5c：模型请求调度器（架构文档 §3.3）。
/// P0 对话（30s 超时，重试 2 次）/ P1 主动互动（8s 超时，不重试）/ P2 后台；
/// SemaphoreSlim 并发闸（默认 3，per-provider）；优先级插队；对话永不被主动互动阻塞。
/// </summary>
public class SchedulerTests
{
    private sealed class FakeProvider : IModelProvider
    {
        public string Id => "fake";
        public ModelCapabilities Capabilities => ModelCapabilities.Chat | ModelCapabilities.Vision;
        public int InFlight { get; private set; }
        public int MaxInFlight { get; private set; }
        public int HandlerCallCount { get; private set; }
        public List<string> ExecutionOrder { get; } = [];
        public Func<ChatRequest, CancellationToken, Task<ChatResult>> Handler { get; set; } =
            static (req, _) => Task.FromResult(new ChatResult($"回复:{req.SystemPrompt}", 5));

        public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct)
        {
            HandlerCallCount++;
            InFlight++;
            MaxInFlight = Math.Max(MaxInFlight, InFlight);
            try
            {
                var result = await Handler(request, ct);
                lock (ExecutionOrder) ExecutionOrder.Add(request.SystemPrompt);
                return result;
            }
            finally
            {
                InFlight--;
            }
        }

        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ModelInfo>>(
                [new ModelInfo("fake-1", "Fake", ModelCapabilities.Chat | ModelCapabilities.Vision)]);
    }

    private static ChatRequest Req(string tag) => new("p:" + tag, [new ChatMessage(ChatRole.User, "hi")]);

    private static Task<ChatResult> Enqueue(
        ModelRequestScheduler s, RequestPriority p, string tag, CancellationToken ct = default)
        => s.EnqueueAsync(p, Req(tag), ct);

    [Fact]
    public async Task Scheduler_CompletesRequest()
    {
        await using var s = new ModelRequestScheduler(new FakeProvider());
        var result = await Enqueue(s, RequestPriority.Conversation, "a");
        Assert.Equal("回复:p:a", result.Text);
    }

    [Fact]
    public async Task Scheduler_EnforcesConcurrencyGate()
    {
        var provider = new FakeProvider();
        provider.Handler = async (_, ct) =>
        {
            await Task.Delay(50, ct);
            return new ChatResult("ok", 1);
        };
        await using var s = new ModelRequestScheduler(provider, concurrency: 2);
        var tasks = Enumerable.Range(0, 8).Select(i => Enqueue(s, RequestPriority.Background, $"t{i}")).ToArray();
        await Task.WhenAll(tasks);
        Assert.True(provider.MaxInFlight <= 2, $"并发超限: {provider.MaxInFlight}");
        Assert.Equal(8, provider.ExecutionOrder.Count);
    }

    [Fact]
    public async Task Scheduler_ConversationPreemptsQueuedBackgroundWork()
    {
        // 闸被 P1 占住，P2 排队中；P0 到达 → P0 必须插到 P2 前面
        var provider = new FakeProvider();
        var gate = new TaskCompletionSource();
        provider.Handler = (req, _) => req.SystemPrompt == "p:interactive"
            ? gate.Task.ContinueWith(_ => new ChatResult("互动完成", 1))
            : Task.FromResult(new ChatResult("ok", 1));

        await using var s = new ModelRequestScheduler(provider, concurrency: 1);
        var interactive = Enqueue(s, RequestPriority.Interactive, "interactive");
        await Task.Delay(50); // 等 interactive 占住唯一闸位
        var background = Enqueue(s, RequestPriority.Background, "bg");
        var conversation = Enqueue(s, RequestPriority.Conversation, "conv");
        await Task.Delay(50);
        Assert.Empty(provider.ExecutionOrder);

        gate.SetResult(); // 释放闸：下一个执行的必须是 P0（conv），不是 P2（bg）

        await Task.WhenAll(interactive, conversation, background);
        Assert.Equal(["p:interactive", "p:conv", "p:bg"], provider.ExecutionOrder);
    }

    [Fact]
    public async Task Scheduler_SamePriorityIsFifo()
    {
        var provider = new FakeProvider();
        provider.Handler = async (_, ct) =>
        {
            await Task.Delay(20, ct);
            return new ChatResult("ok", 1);
        };
        await using var s = new ModelRequestScheduler(provider, concurrency: 1);
        var tasks = Enumerable.Range(0, 5).Select(i => Enqueue(s, RequestPriority.Background, $"f{i}")).ToArray();
        await Task.WhenAll(tasks);
        Assert.Equal(["p:f0", "p:f1", "p:f2", "p:f3", "p:f4"], provider.ExecutionOrder);
    }

    [Fact]
    public async Task Scheduler_ConversationTimeout_RetriesTwiceThenFails()
    {
        // P0 超时走重试（指数退避 2 次 = 共 3 次尝试），全部超时后失败
        var provider = new FakeProvider();
        provider.Handler = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new ChatResult("never", 0);
        };
        await using var s = new ModelRequestScheduler(
            provider, concurrency: 1,
            conversationTimeout: TimeSpan.FromMilliseconds(80),
            interactiveTimeout: TimeSpan.FromMilliseconds(80),
            backgroundTimeout: TimeSpan.FromMilliseconds(80),
            backoffBaseMs: 10);

        var ex = await Assert.ThrowsAsync<ProviderException>(
            () => Enqueue(s, RequestPriority.Conversation, "slow"));
        Assert.Equal("timeout", ex.Code);
        Assert.Equal(3, provider.HandlerCallCount); // 超时重试 2 次 = 共 3 次尝试
    }

    [Fact]
    public async Task Scheduler_InteractiveTimeout_DoesNotRetry()
    {
        var provider = new FakeProvider();
        provider.Handler = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new ChatResult("never", 0);
        };
        await using var s = new ModelRequestScheduler(
            provider, concurrency: 1,
            conversationTimeout: TimeSpan.FromMilliseconds(80),
            interactiveTimeout: TimeSpan.FromMilliseconds(80),
            backgroundTimeout: TimeSpan.FromMilliseconds(80),
            backoffBaseMs: 10);

        var ex = await Assert.ThrowsAsync<ProviderException>(
            () => Enqueue(s, RequestPriority.Interactive, "slow"));
        Assert.Equal("timeout", ex.Code);
        Assert.Equal(1, provider.HandlerCallCount); // 互动超时跳过本轮，不重试
    }

    [Fact]
    public async Task Scheduler_ProviderTimeout_UsesConversationRetryPolicy()
    {
        var provider = new FakeProvider
        {
            Handler = (_, _) => throw new ProviderException("timeout", "provider deadline"),
        };
        await using var s = new ModelRequestScheduler(provider, backoffBaseMs: 1);

        var ex = await Assert.ThrowsAsync<ProviderException>(
            () => Enqueue(s, RequestPriority.Conversation, "provider-timeout"));

        Assert.Equal("timeout", ex.Code);
        Assert.Equal(3, provider.HandlerCallCount);
    }

    [Fact]
    public async Task Scheduler_Deadline_CompletesWhenProviderIgnoresCancellation()
    {
        var completion = new TaskCompletionSource<ChatResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider
        {
            Handler = (_, _) => completion.Task,
        };
        var s = new ModelRequestScheduler(
            provider,
            concurrency: 1,
            conversationTimeout: TimeSpan.FromMilliseconds(50),
            backoffBaseMs: 1);
        var request = Enqueue(s, RequestPriority.Conversation, "non-cooperative");
        Exception? error;
        try
        {
            error = await Record.ExceptionAsync(
                () => request.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            completion.TrySetResult(new ChatResult("late", 0));
            await s.DisposeAsync();
        }

        var ex = Assert.IsType<ProviderException>(error);
        Assert.Equal("timeout", ex.Code);
        Assert.Equal(1, provider.HandlerCallCount);
    }

    [Fact]
    public async Task Scheduler_Dispose_CompletesWhenProviderIgnoresCancellation()
    {
        var completion = new TaskCompletionSource<ChatResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider
        {
            Handler = (_, _) => completion.Task,
        };
        var s = new ModelRequestScheduler(provider, concurrency: 1);
        var request = Enqueue(s, RequestPriority.Background, "non-cooperative");
        using var startedCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (provider.HandlerCallCount == 0) await Task.Delay(5, startedCts.Token);

        var disposeTask = s.DisposeAsync().AsTask();
        Exception? disposeError;
        try
        {
            disposeError = await Record.ExceptionAsync(
                () => disposeTask.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            completion.TrySetResult(new ChatResult("late", 0));
            await disposeTask;
        }

        Assert.Null(disposeError);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => request.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Scheduler_CallerCancellationDuringDeadlineDrain_WinsOverTimeout()
    {
        var completion = new TaskCompletionSource<ChatResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider { Handler = (_, _) => completion.Task };
        var s = new ModelRequestScheduler(
            provider,
            concurrency: 1,
            conversationTimeout: TimeSpan.FromMilliseconds(30));
        using var caller = new CancellationTokenSource();
        var request = Enqueue(s, RequestPriority.Conversation, "cancel-during-drain", caller.Token);
        while (provider.HandlerCallCount == 0) await Task.Delay(5);
        await Task.Delay(80);

        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => request.WaitAsync(TimeSpan.FromSeconds(1)));
        completion.TrySetResult(new ChatResult("late", 0));
        await s.DisposeAsync();
    }

    [Fact]
    public async Task Scheduler_DisposeDuringDeadlineDrain_CancelsRequest()
    {
        var completion = new TaskCompletionSource<ChatResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new FakeProvider { Handler = (_, _) => completion.Task };
        var s = new ModelRequestScheduler(
            provider,
            concurrency: 1,
            conversationTimeout: TimeSpan.FromMilliseconds(30));
        var request = Enqueue(s, RequestPriority.Conversation, "dispose-during-drain");
        while (provider.HandlerCallCount == 0) await Task.Delay(5);
        await Task.Delay(80);

        await s.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => request.WaitAsync(TimeSpan.FromSeconds(1)));
        completion.TrySetResult(new ChatResult("late", 0));
    }

    [Fact]
    public async Task Scheduler_Dispose_CancelsQueuedAndInFlightRequests()
    {
        var provider = new FakeProvider
        {
            Handler = async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new ChatResult("never", 0);
            },
        };
        var s = new ModelRequestScheduler(provider, concurrency: 1);
        var inFlight = Enqueue(s, RequestPriority.Background, "in-flight");
        var queued = Enqueue(s, RequestPriority.Background, "queued");
        using var startedCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (provider.HandlerCallCount == 0) await Task.Delay(5, startedCts.Token);

        await s.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlight.WaitAsync(TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, provider.HandlerCallCount);
    }

    [Fact]
    public async Task Scheduler_ConversationRetry_EventuallySucceeds()
    {
        // 前两次超时，第三次成功 → 重试机制恢复
        var provider = new FakeProvider();
        var attempts = 0;
        provider.Handler = async (req, ct) =>
        {
            attempts++;
            if (attempts < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
                return new ChatResult("never", 0);
            }
            return new ChatResult("终于成功", 7);
        };
        await using var s = new ModelRequestScheduler(
            provider, concurrency: 1,
            conversationTimeout: TimeSpan.FromMilliseconds(80),
            interactiveTimeout: TimeSpan.FromMilliseconds(80),
            backgroundTimeout: TimeSpan.FromMilliseconds(80),
            backoffBaseMs: 10);

        var result = await Enqueue(s, RequestPriority.Conversation, "retry");
        Assert.Equal("终于成功", result.Text);
        Assert.Equal(3, provider.HandlerCallCount);
    }

    [Fact]
    public async Task Scheduler_ExternalCancellation_CancelsRequest()
    {
        var provider = new FakeProvider();
        provider.Handler = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new ChatResult("never", 0);
        };
        await using var s = new ModelRequestScheduler(
            provider, concurrency: 1,
            conversationTimeout: TimeSpan.FromSeconds(30),
            interactiveTimeout: TimeSpan.FromSeconds(8),
            backgroundTimeout: TimeSpan.FromSeconds(60));

        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Enqueue(s, RequestPriority.Conversation, "cancel", cts.Token));
        Assert.Equal(1, provider.HandlerCallCount); // 外部取消不触发重试
    }

    [Fact]
    public async Task Scheduler_ProviderError_PropagatesToCaller()
    {
        var provider = new FakeProvider();
        provider.Handler = (_, _) => throw new InvalidOperationException("模型挂了");
        await using var s = new ModelRequestScheduler(provider);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Enqueue(s, RequestPriority.Conversation, "err"));
        Assert.Equal("模型挂了", ex.Message);
    }

    [Fact]
    public async Task Scheduler_RunsConcurrently_NotSerialized()
    {
        // 回归：原单 worker 循环把请求全部串行化（并发闸形同虚设），MaxInFlight<=2 的旧断言无法暴露。
        // 修复后：worker 池并发度 = concurrency，多宠物并行请求真正并行。
        var provider = new FakeProvider();
        provider.Handler = async (_, ct) =>
        {
            await Task.Delay(100, ct);
            return new ChatResult("ok", 1);
        };
        await using var s = new ModelRequestScheduler(provider, concurrency: 3);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, 6).Select(i => Enqueue(s, RequestPriority.Background, $"t{i}")).ToArray();
        await Task.WhenAll(tasks);
        sw.Stop();
        Assert.Equal(3, provider.MaxInFlight); // 真实并行度 = concurrency（串行实现永远为 1）
        // 6×100ms 串行 = 600ms；并行两波 ≈ 200ms（阈值 500ms 留 CI 余量）
        Assert.True(sw.ElapsedMilliseconds < 500, $"被串行化: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task Scheduler_PriorityPreempts_UnderConcurrency()
    {
        // 并发下 P0 仍不被队列中先入队的 P1/P2 阻塞：空闲 worker 必须取最高优先级。
        // p1 挂起占 worker1，p2a 挂起占 worker2 → P0 到达时两个 worker 都忙，只能排队；
        // 释放 p1 后空闲 worker 必须先处理队列中的 P0（而不是先入队的 p2b）。
        var provider = new FakeProvider();
        var gateP1 = new TaskCompletionSource();
        var gateP2 = new TaskCompletionSource();
        provider.Handler = async (req, ct) =>
        {
            if (req.SystemPrompt == "p:p1") { await gateP1.Task.WaitAsync(ct); return new ChatResult("p1", 1); }
            if (req.SystemPrompt == "p:p2a") { await gateP2.Task.WaitAsync(ct); return new ChatResult("p2a", 1); }
            return new ChatResult("ok", 1);
        };
        await using var s = new ModelRequestScheduler(provider, concurrency: 2);
        var p1 = Enqueue(s, RequestPriority.Interactive, "p1");
        await Task.Delay(50); // p1 占住 worker1
        var p2a = Enqueue(s, RequestPriority.Background, "p2a");
        var p2b = Enqueue(s, RequestPriority.Background, "p2b");
        var p0 = Enqueue(s, RequestPriority.Conversation, "p0");
        await Task.Delay(100); // worker2: p0 完成 → 立即取 p2a 并挂起占位；p2b 仍在队列
        Assert.Equal(["p:p0"], provider.ExecutionOrder); // P0 已先于 p2a/p2b 完成
        gateP1.SetResult();
        gateP2.SetResult();
        await Task.WhenAll(p1, p2a, p2b, p0);
        Assert.Equal(4, provider.ExecutionOrder.Count);
        Assert.Equal("p:p0", provider.ExecutionOrder[0]); // P0 最先完成（未被 P1/P2 阻塞）
    }
}
