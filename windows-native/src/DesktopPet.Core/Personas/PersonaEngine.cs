namespace DesktopPet.Core.Personas;

/// <summary>
/// 人格引擎（docs/ai-personas.md §1）：
/// 最终 System Prompt = Base Prompt（场景约束） + 人格 Prompt（性格差异）。
/// 防人格漂移：每轮请求都携带完整 System Prompt，不依赖上下文记忆。
/// </summary>
public static class PersonaEngine
{
    /// <summary>Base Prompt（所有宠物共用，固定；ai-personas.md §1）。</summary>
    public const string BasePrompt =
        "你是用户桌面上的一只 AI 宠物伙伴，用户会和你闲聊、分享正在做的事。" +
        "规则：回复必须简短（不超过 50 字），口语化、像真人发消息；" +
        "不要复述用户的话；不要解释你是 AI；永远保持当前人格设定，人格是身份不是表演。";

    /// <summary>采样参数：性格风格稳定区间（ai-personas.md §1）。</summary>
    public const double Temperature = 0.7;

    /// <summary>配合 50 字约束的回复上限（ai-personas.md §1）。</summary>
    public const int MaxTokens = 120;

    /// <summary>拼接最终 System Prompt：Base 在前（约束优先），人格在后（性格差异）。</summary>
    public static string BuildSystemPrompt(Persona persona)
        => BasePrompt + "\n\n" + persona.Prompt;
}
