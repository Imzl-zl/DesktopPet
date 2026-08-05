using DesktopPet.Core.Personas;
using DesktopPet.Core.Scheduling;

namespace DesktopPet.Core.Ai;

public sealed record ChatPipelineOptions(
    int MaxInputLength = 500,
    int ScreenContextMaxEvents = 4);

/// <summary>
/// 管道结果：校验失败（Ok=false + Error 码）或成功（Text + TokensUsed）。
/// Error 码供 UI 映射 i18n 文案：empty / too-long。
/// </summary>
public sealed record PipelineResult(bool Ok, string? Error, string? Text, int TokensUsed);

/// <summary>
/// 对话请求管道（架构文档 §4；Phase 5 范围）：
/// ① 校验 → ② 人格拼接（每轮完整 System Prompt，防人格漂移）→
/// ③ 屏幕上下文（可选，默认关）→ ④ Scheduler P0 调用 → ⑤ token 记账回调。
/// 记忆注入/亲密度修饰为 Phase 6 预留（在 ②/③ 之间插入）。
/// 纯逻辑可单测：scheduler 注入，persona 通过 resolver 每轮解析（切换立即生效）。
/// </summary>
public sealed class ChatPipeline
{
    private readonly ModelRequestScheduler _scheduler;
    private readonly Func<Persona> _personaResolver;
    private readonly ScreenEventLog _eventLog;
    private readonly ChatPipelineOptions _options;

    public ChatPipeline(
        ModelRequestScheduler scheduler,
        Func<Persona> personaResolver,
        ScreenEventLog eventLog,
        ChatPipelineOptions? options = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _personaResolver = personaResolver ?? throw new ArgumentNullException(nameof(personaResolver));
        _eventLog = eventLog ?? throw new ArgumentNullException(nameof(eventLog));
        _options = options ?? new ChatPipelineOptions();
    }

    public async Task<PipelineResult> RunAsync(
        string userInput,
        IReadOnlyList<ChatMessage> history,
        bool includeScreenContext,
        Func<string>? memoryInjector = null,    // Phase 6：记忆画像注入（空返回 = 不注入）
        Func<string>? systemPromptSuffix = null, // Phase 6c：亲密度档位语气指令（空返回 = 不追加）
        int? maxTokens = null,                  // 对话路径：模型连接配置的最大输出（null = 不发送，上游默认）
        Action<int>? onTokensUsed = null,
        CancellationToken ct = default)
    {
        var text = userInput?.Trim() ?? "";
        if (text.Length == 0) return new PipelineResult(false, "empty", null, 0);
        if (text.Length > _options.MaxInputLength) return new PipelineResult(false, "too-long", null, 0);

        var persona = _personaResolver();
        var messages = new List<ChatMessage>(history);
        // ③ 记忆注入（架构文档 §4）：人格拼接后、屏幕上下文前；记忆开关关 = 不传 injector
        if (memoryInjector is not null)
        {
            var memory = memoryInjector();
            if (memory.Length > 0) messages.Add(new ChatMessage(ChatRole.System, memory));
        }
        if (includeScreenContext)
        {
            var context = ScreenContextFormatter.Format(_eventLog.Recent(), _options.ScreenContextMaxEvents);
            if (context.Length > 0) messages.Add(new ChatMessage(ChatRole.User, context));
        }
        messages.Add(new ChatMessage(ChatRole.User, text));

        var systemPrompt = PersonaEngine.BuildSystemPrompt(persona);
        if (systemPromptSuffix is { } suffixFactory)
        {
            var suffix = suffixFactory();
            if (suffix.Length > 0) systemPrompt += "\n\n" + suffix;
        }

        var request = new ChatRequest(
            SystemPrompt: systemPrompt,
            Messages: messages,
            Temperature: PersonaEngine.Temperature,
            MaxTokens: maxTokens);

        var result = await _scheduler.EnqueueAsync(RequestPriority.Conversation, request, ct);
        onTokensUsed?.Invoke(result.TokensUsed);
        return new PipelineResult(true, null, result.Text, result.TokensUsed);
    }
}
