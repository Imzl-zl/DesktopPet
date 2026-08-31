using DesktopPet.Core.ImageGen;
using DesktopPet.Core.Scheduling;
using DesktopPet.Core.SpriteSkill;

namespace DesktopPet.App.Ai;

/// <summary>
/// 动作精灵图技能会话：把 Core 层编排器装配到真实 Provider 上。
/// 对话用注入的 IModelProvider；生图通过 AiImageGenAdapter 包装
/// AiCoordinator.GenerateImageAsync（复用现有连接/模型/透明管线配置）。
/// </summary>
public sealed class SpriteSkillSession
{
    private readonly SpriteSkillEngine _engine;
    public SpriteSkillDefinition Skill { get; }
    public string ConnectionId { get; }
    public string ImageModelId { get; }

    public SpriteSkillSession(
        AiCoordinator ai,
        IModelProvider model,
        string connectionId,
        string imageModelId,
        SpriteSkillDefinition skill,
        CellSpec cell,
        SpriteSkillOptions? options = null)
    {
        Skill = skill;
        ConnectionId = connectionId;
        ImageModelId = imageModelId;
        _engine = new SpriteSkillEngine(
            model,
            new AiImageGenAdapter(ai, connectionId, imageModelId),
            skill,
            cell,
            options: options);
    }

    public Task<SpriteSkillResult> RunAsync(
        string userRequest,
        string? referenceDescription,
        IReadOnlyList<ReferenceImage>? references,
        CancellationToken ct)
        => _engine.RunAsync(userRequest, referenceDescription, references, ct);
}

/// <summary>
/// 把 AiCoordinator.GenerateImageAsync 包装成 Core 层依赖的 IImageGenProvider。
/// 行贴图生成 = 文生图（spec 内可带参考图），编辑接口不需要。
/// </summary>
public sealed class AiImageGenAdapter : IImageGenProvider
{
    private readonly AiCoordinator _ai;
    private readonly string _connectionId;
    private readonly string _modelId;

    public string Family => "ai-coordinator";

    public AiImageGenAdapter(AiCoordinator ai, string connectionId, string modelId)
    {
        _ai = ai ?? throw new ArgumentNullException(nameof(ai));
        _connectionId = connectionId;
        _modelId = modelId;
    }

    public Task<ImageGenOutput> GenerateAsync(ImageGenSpec spec, CancellationToken ct)
        => _ai.GenerateImageAsync(_connectionId, _modelId, spec, ct);

    public Task<ImageGenOutput> EditAsync(ImageGenSpec spec, IReadOnlyList<ReferenceImage> references, CancellationToken ct)
        => _ai.EditImageAsync(_connectionId, _modelId, spec, references, ct);
}
