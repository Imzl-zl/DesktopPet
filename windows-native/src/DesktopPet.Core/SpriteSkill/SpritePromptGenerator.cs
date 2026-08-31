using System.Text.Json;
using DesktopPet.Core.Scheduling;

namespace DesktopPet.Core.SpriteSkill;

/// <summary>
/// 把用户的自然语言需求翻译成"动作计划"（每行的帧数 + 生图提示词）。
/// 支持两种生成模式：RowStrip（每个动作一个"一行 N 帧"提示词）与
/// PerFrame（每个动作 N 个单帧提示词，逐帧生成）。复用现有 IModelProvider
/// （纯文本对话，无 function calling 依赖），保持 Core 层零 IO。
/// </summary>
public sealed class SpritePromptGenerator
{
    private readonly IModelProvider _model;

    // 模型输出的大小写不可控，解析一律大小写不敏感。
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public SpritePromptGenerator(IModelProvider model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
    }

    /// <summary>一次调用产出完整动作计划。LLM 只输出短描述（帧姿势 <15 词），完整生图提示词由本地模板展开（避免长 JSON 截断，无需 max_tokens）。JSON 解析失败抛 JsonException；字段缺失抛 ArgumentException。</summary>
    public async Task<ActionPlan> GenerateActionPlanAsync(
        SpriteSkillDefinition skill,
        string userRequest,
        string? referenceDescription,
        SpriteSkillOptions options,
        CancellationToken ct)
    {
        var modeInstruction = options.Mode switch
        {
            SpriteGenMode.PerFrame =>
                "输出要求：每个动作输出 frames 数组（N 个短姿势描述字符串，每个不超过 15 词，如 breathing pose 1 of 8），" +
                "不要输出完整生图提示词（会由程序拼接），不要输出 rowPrompt。",
            _ =>
                "输出要求：每个动作输出 frames 数组（N 个短姿势描述字符串，每个不超过 15 词），" +
                "不要输出完整生图提示词（会由程序拼接）。",
        };
        var styleInstruction = options.Style == SpriteStyleLevel.Simple
            ? "所有描述要求简单扁平卡通风格（flat simple cartoon）。"
            : "描述允许细节丰富的风格要求。";
        var systemPrompt = $"{skill.SystemPrompt}\n\n{modeInstruction}\n{styleInstruction}\nidentityDescription 不超过 30 词。";

        var userContent = userRequest;
        if (!string.IsNullOrEmpty(referenceDescription))
        {
            userContent = $"{referenceDescription}\n\n{userContent}";
        }

        // 不传 MaxTokens：紧凑 JSON（短描述）远小于模型默认输出窗口。
        var request = new ChatRequest(
            SystemPrompt: systemPrompt,
            Messages: [new ChatMessage(ChatRole.User, userContent)]);
        var result = await _model.CompleteAsync(request, ct);

        var text = result.Text.Trim();
        // LLM 常把 JSON 用 ```json 代码块包裹或加解释文字（真实端点行为）——
        // 提取第一个 { 到最后一个 } 的 JSON 主体，最健壮。
        var jsonStart = text.IndexOf('{');
        var jsonEnd = text.LastIndexOf('}');
        if (jsonStart >= 0 && jsonEnd > jsonStart)
            text = text[jsonStart..(jsonEnd + 1)];

        var dto = JsonSerializer.Deserialize<ActionPlanDto>(text, JsonOptions);
        if (dto is null) throw new JsonException("模型输出不是有效 JSON");
        var identity = (dto.IdentityDescription ?? "").Trim();
        if (identity.Length == 0) throw new ArgumentException("动作计划缺少 identityDescription");

        var actions = new List<SpriteActionSpec>();
        foreach (var action in dto.Actions ?? [])
        {
            if (string.IsNullOrEmpty(action.Id)
                || action.FrameCount is null or <= 0)
            {
                throw new ArgumentException("动作计划缺少必需字段（id / frameCount）");
            }
            var descriptions = action.Frames ?? [];
            if (descriptions.Count != action.FrameCount.Value
                || descriptions.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException(
                    $"动作 {action.Id} 的 frames 数量（{descriptions.Count}）与 frameCount（{action.FrameCount}）不符或含空值");
            }

            // 本地展开完整生图提示词（身份 + 姿势短描述 + 风格/背景模板）。
            var styleSuffix = options.Style == SpriteStyleLevel.Simple
                ? "flat simple cartoon style, pure green background #00FF00, no shadows no text"
                : "detailed style, pure green background #00FF00, no shadows no text";
            var framePrompts = descriptions
                .Select(d => $"{identity}, {d.Trim()}, {styleSuffix}")
                .ToList();
            var rowPrompt = $"{identity}, {descriptions.Count} separate poses side by side in one row: " +
                            $"{string.Join("; ", descriptions.Select(d => d.Trim()))}, each pose in its own cell with clear gaps, {styleSuffix}";

            actions.Add(new SpriteActionSpec(
                new ActionSpec(action.Id, action.FrameCount.Value, action.Durations ?? [], action.Loop),
                rowPrompt,
                framePrompts));
        }
        if (actions.Count == 0) throw new ArgumentException("动作计划为空");

        return new ActionPlan(identity, actions);
    }

    // ---- Wire DTO（System.Text.Json 反射反序列化，.NET 8 运行时可用）----

    private sealed class ActionPlanDto
    {
        public string? IdentityDescription { get; set; }
        public List<ActionDto>? Actions { get; set; }
    }

    private sealed class ActionDto
    {
        public string? Id { get; set; }
        public int? FrameCount { get; set; }
        public List<double>? Durations { get; set; }
        public bool Loop { get; set; } = true;
        public string? RowPrompt { get; set; }
        public List<string>? Frames { get; set; }
    }
}
