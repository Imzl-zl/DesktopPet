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

        await Assert.ThrowsAsync<TaskCanceledException>(() => Enqueue(s, RequestPriority.Conversation, "slow"));
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

        await Assert.ThrowsAsync<TaskCanceledException>(() => Enqueue(s, RequestPriority.Interactive, "slow"));
        Assert.Equal(1, provider.HandlerCallCount); // 互动超时跳过本轮，不重试
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
}
