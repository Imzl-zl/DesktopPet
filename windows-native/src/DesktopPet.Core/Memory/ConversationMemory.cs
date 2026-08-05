using DesktopPet.Core.Scheduling;

namespace DesktopPet.Core.Memory;

/// <summary>
/// 分层会话记忆（L1 工作区 + L2 滚动摘要；参考成熟 agent 记忆分层的简洁版）：
/// - L1：最近对话消息，按 token 预算保留（预算 = 模型上下文 50%；256k 配置下
///   可容纳几百轮，日常对话几乎不会触发裁剪）
/// - L2：真超预算时，最早轮次**不丢弃**，压缩为滚动摘要（复用
///   <see cref="MemoryProfileExtractor.Compress"/>），以 system 消息注入后续请求
///   ——会话不割裂，老细节进摘要而非消失
/// - L3（画像/亲密度/每日总结）由上层管理；会话摘要可由上层合并进画像
/// 纯逻辑零依赖，可单测。
/// </summary>
public sealed class ConversationMemory
{
    private readonly List<ChatMessage> _messages = new();
    private string _summary = "";

    /// <summary>未配置模型上下文时的默认上下文（32k），预算 = 其 50%。</summary>
    public const int DefaultContextTokens = 32768;

    /// <summary>会话预算占模型上下文的比例（其余留给 system/记忆/当前输入/输出）。</summary>
    public const double BudgetRatio = 0.5;

    private const int SummaryMaxChars = 200;

    public string Summary => _summary;

    public int Count => _messages.Count;

    public void Append(string userText, string assistantText)
    {
        _messages.Add(new ChatMessage(ChatRole.User, userText));
        _messages.Add(new ChatMessage(ChatRole.Assistant, assistantText));
    }

    /// <summary>重开对话：清空 L1 消息与 L2 摘要（记忆画像/亲密度不受影响）。</summary>
    public void Clear()
    {
        _messages.Clear();
        _summary = "";
    }

    /// <summary>
    /// 构建请求上下文：L2 摘要（若有）+ 预算内最近消息。
    /// 超预算的最早轮次压缩进滚动摘要（下次请求注入，不静默丢弃）。
    /// </summary>
    public IReadOnlyList<ChatMessage> BuildContext(int contextTokens)
    {
        var budget = Math.Max(1024, (int)(Math.Max(0, contextTokens) * BudgetRatio));
        var kept = new List<ChatMessage>();
        var dropped = new List<ChatMessage>();
        var approxTokens = 0;
        for (var i = _messages.Count - 1; i >= 0; i--)
        {
            var message = _messages[i];
            var approx = ApproxTokens(message.Content);
            // 至少保留最新一条（预算再小也不至于空上下文）
            if (kept.Count > 0 && approxTokens + approx > budget)
            {
                dropped.Insert(0, message);
                continue;
            }
            approxTokens += approx;
            kept.Insert(0, message);
        }
        if (dropped.Count > 0)
        {
            var droppedTurns = dropped
                .Where(m => m.Role == ChatRole.User)
                .Select(m => (m, DateTime.Now))
                .ToList();
            var compressed = MemoryProfileExtractor.Compress(droppedTurns, SummaryMaxChars);
            if (compressed.Length > 0)
            {
                _summary = _summary.Length > 0
                    ? MemoryProfileExtractor.Compress(
                        [(new ChatMessage(ChatRole.User, _summary), DateTime.Now),
                         (new ChatMessage(ChatRole.User, compressed), DateTime.Now)],
                        SummaryMaxChars)
                    : compressed;
            }
        }

        var result = new List<ChatMessage>();
        if (_summary.Length > 0)
        {
            result.Add(new ChatMessage(ChatRole.System, "之前你们聊过（摘要，供你回忆）：\n" + _summary));
        }
        result.AddRange(kept);
        return result;
    }

    /// <summary>token 粗算：1 汉字 ≈ 1.5 token（够用于预算裁剪的近似即可）。</summary>
    private static int ApproxTokens(string? text)
        => (text?.Length ?? 0) * 3 / 2;
}
