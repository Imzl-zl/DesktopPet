namespace DesktopPet.Core.SpriteSkill;

/// <summary>生成模式：多帧一行（1 次生成 N 帧，快）｜逐帧（N 次生成、每帧单独，稳/质量高/避超时）。</summary>
public enum SpriteGenMode { RowStrip, PerFrame }

/// <summary>风格档位：Simple（稳定，扁平卡通）｜Full（细节丰富，但复杂 prompt 更易触发模型超时）。</summary>
public enum SpriteStyleLevel { Simple, Full }

/// <summary>技能运行选项（UI 可选配，默认值可被用户覆盖）。</summary>
public sealed record SpriteSkillOptions(
    int DefaultFrameCount = 3,
    SpriteGenMode Mode = SpriteGenMode.RowStrip,
    SpriteStyleLevel Style = SpriteStyleLevel.Simple);

/// <summary>单个动作 + 行贴图生成提示词（LLM 产出的动作计划元素）。</summary>
public sealed record SpriteActionSpec(
    ActionSpec Spec,
    string RowPrompt,
    IReadOnlyList<string>? FramePrompts = null);

/// <summary>LLM 产出的完整动作计划：身份描述 + 动作列表。</summary>
public sealed record ActionPlan(string IdentityDescription, IReadOnlyList<SpriteActionSpec> Actions);

/// <summary>技能定义（W2 起用；W3 挂到 App 层资源目录）。</summary>
public sealed record SpriteSkillDefinition(
    string Id,
    string Name,
    string Description,
    string SystemPrompt);
