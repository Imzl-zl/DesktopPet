using System.Text.Json;
using DesktopPet.Core.Scheduling;
using DesktopPet.Core.SpriteSkill;

namespace DesktopPet.Core.Tests.SpriteSkill;

public class SpritePromptGeneratorTests
{
    private sealed class FakeProvider : IModelProvider
    {
        public string Id => "fake";
        public ModelCapabilities Capabilities => ModelCapabilities.Chat;
        public string Reply { get; set; } = "";
        public ChatRequest? LastRequest { get; private set; }

        public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new ChatResult(Reply, 10));
        }

        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
    }

    private static readonly SpriteSkillDefinition Skill = new(
        "sprite-pet", "动作精灵图", "生成自定义动作精灵图",
        "你是宠物精灵图生成助手。输出严格 JSON。");

    // LLM 只输出短姿势描述（<15 词），完整生图提示词由本地模板展开。
    private const string ValidPlanJson = """
    {
      "identityDescription": "a cute orange tabby cat, white paws, round face",
      "actions": [
        {"id": "idle", "frameCount": 3, "durations": [], "loop": true,
         "frames": ["breathing pose 1", "breathing pose 2", "breathing pose 3"]},
        {"id": "jump", "frameCount": 2, "durations": [], "loop": false,
         "frames": ["crouching", "leaping up"]}
      ]
    }
    """;

    [Fact]
    public async Task GenerateActionPlan_ReturnsParsedPlan_WithExpandedPrompts()
    {
        var provider = new FakeProvider { Reply = ValidPlanJson };
        var generator = new SpritePromptGenerator(provider);

        var plan = await generator.GenerateActionPlanAsync(
            Skill, "给猫做 idle 和 jump", null, new SpriteSkillOptions(), CancellationToken.None);

        Assert.Equal("a cute orange tabby cat, white paws, round face", plan.IdentityDescription);
        Assert.Equal(2, plan.Actions.Count);
        Assert.Equal("idle", plan.Actions[0].Spec.Id);
        Assert.Equal(3, plan.Actions[0].Spec.FrameCount);
        // 本地展开：身份 + 姿势描述 + 风格/背景模板
        Assert.Equal(3, plan.Actions[0].FramePrompts!.Count);
        Assert.Contains("a cute orange tabby cat, white paws, round face", plan.Actions[0].FramePrompts[0]);
        Assert.Contains("breathing pose 2", plan.Actions[0].FramePrompts[1]);
        Assert.Contains("pure green background #00FF00", plan.Actions[0].FramePrompts[0]);
        // rowPrompt 也由本地生成（RowStrip 模式用）
        Assert.Contains("3 separate poses side by side in one row", plan.Actions[0].RowPrompt);
    }

    [Fact]
    public async Task GenerateActionPlan_DoesNotSendMaxTokens()
    {
        // 紧凑 JSON（短描述）不需要 max_tokens——很多模型/端点不传更好。
        var provider = new FakeProvider { Reply = ValidPlanJson };
        var generator = new SpritePromptGenerator(provider);

        await generator.GenerateActionPlanAsync(
            Skill, "做动作", null, new SpriteSkillOptions(), CancellationToken.None);

        Assert.NotNull(provider.LastRequest);
        Assert.Null(provider.LastRequest!.MaxTokens);
    }

    [Fact]
    public async Task GenerateActionPlan_IncludesSkillSystemPromptAndReference()
    {
        var provider = new FakeProvider { Reply = ValidPlanJson };
        var generator = new SpritePromptGenerator(provider);

        await generator.GenerateActionPlanAsync(
            Skill, "给猫做 idle", referenceDescription: "参考图：photo1.png", new SpriteSkillOptions(), CancellationToken.None);

        var req = provider.LastRequest;
        Assert.NotNull(req);
        Assert.Contains(Skill.SystemPrompt, req.SystemPrompt);
        Assert.Contains("不超过 15 词", req.SystemPrompt);
        Assert.Contains("参考图：photo1.png", req.Messages[^1].Content);
        Assert.Contains("给猫做 idle", req.Messages[^1].Content);
    }

    [Fact]
    public async Task GenerateActionPlan_Throws_OnInvalidJson()
    {
        var provider = new FakeProvider { Reply = "抱歉，我不能生成 JSON。" };
        var generator = new SpritePromptGenerator(provider);

        await Assert.ThrowsAsync<JsonException>(() =>
            generator.GenerateActionPlanAsync(Skill, "做动作", null, new SpriteSkillOptions(), CancellationToken.None));
    }

    [Fact]
    public async Task GenerateActionPlan_Throws_OnMissingRequiredFields()
    {
        var provider = new FakeProvider
        {
            Reply = """{"identityDescription": "猫", "actions": [{"id": "idle"}]}""" // 缺 frameCount/frames
        };
        var generator = new SpritePromptGenerator(provider);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            generator.GenerateActionPlanAsync(Skill, "做动作", null, new SpriteSkillOptions(), CancellationToken.None));
    }

    [Fact]
    public async Task GenerateActionPlan_StripsMarkdownJsonFence()
    {
        // LLM 常把 JSON 用 ```json 代码块包裹（真实端点行为）——解析前必须剥离。
        var provider = new FakeProvider { Reply = "```json\n" + ValidPlanJson + "\n```" };
        var generator = new SpritePromptGenerator(provider);

        var plan = await generator.GenerateActionPlanAsync(
            Skill, "做动作", null, new SpriteSkillOptions(), CancellationToken.None);

        Assert.Equal(2, plan.Actions.Count);
        Assert.Equal("idle", plan.Actions[0].Spec.Id);
    }

    [Fact]
    public async Task GenerateActionPlan_Throws_WhenFramesCountMismatch()
    {
        var provider = new FakeProvider
        {
            Reply = """
            {"identityDescription": "橘猫", "actions": [
              {"id": "idle", "frameCount": 3, "frames": ["a", "b"]}
            ]}
            """
        };
        var generator = new SpritePromptGenerator(provider);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            generator.GenerateActionPlanAsync(Skill, "做 idle", null, new SpriteSkillOptions(Mode: SpriteGenMode.PerFrame), CancellationToken.None));
    }
}
