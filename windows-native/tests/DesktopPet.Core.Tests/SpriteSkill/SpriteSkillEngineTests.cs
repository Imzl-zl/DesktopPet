using DesktopPet.Core.ImageGen;
using DesktopPet.Core.Scheduling;
using DesktopPet.Core.SpriteSkill;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DesktopPet.Core.Tests.SpriteSkill;

public class SpriteSkillEngineTests
{
    // ---- Fakes ----

    private sealed class FakeProvider : IModelProvider
    {
        public string Id => "fake";
        public ModelCapabilities Capabilities => ModelCapabilities.Chat;
        public string Reply { get; set; } = "";
        public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken ct)
            => Task.FromResult(new ChatResult(Reply, 10));
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
    }

    private sealed class FakeImageGen : IImageGenProvider
    {
        public string Family => "fake";
        public Func<int, ImageGenSpec, ImageGenOutput>? OnGenerate { get; set; }
        /// <summary>记录每次 EditAsync 的参考图数量（链式行为断言用）。</summary>
        public List<int> EditReferenceCounts { get; } = [];
        private int _calls;

        public Task<ImageGenOutput> GenerateAsync(ImageGenSpec spec, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _calls);
            return Task.FromResult(OnGenerate!(n, spec));
        }

        public Task<ImageGenOutput> EditAsync(ImageGenSpec spec, IReadOnlyList<ReferenceImage> references, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _calls);
            EditReferenceCounts.Add(references.Count);
            return Task.FromResult(OnGenerate!(n, spec));
        }
    }

    // ---- Helpers ----

    private static byte[] StripPng(int frameCount, int cellW = 48, int cellH = 52, int gutter = 6)
    {
        using var img = new Image<Rgba32>(frameCount * cellW + (frameCount - 1) * gutter, cellH);
        for (var i = 0; i < frameCount; i++)
        {
            for (var y = 0; y < cellH; y++)
                for (var x = 0; x < cellW; x++)
                    img[i * (cellW + gutter) + x, y] = new Rgba32((byte)(50 + i * 40), 120, 180, 255);
        }
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    private const string PlanJson = """
    {
      "identityDescription": "a cute orange cat",
      "actions": [
        {"id": "idle", "frameCount": 3, "durations": [], "loop": true, "frames": ["pose 1", "pose 2", "pose 3"]},
        {"id": "jump", "frameCount": 2, "durations": [], "loop": false, "frames": ["crouch", "leap"]}
      ]
    }
    """;

    private static SpriteSkillEngine MakeEngine(
        FakeProvider provider,
        FakeImageGen imageGen,
        SpriteSkillDefinition skill,
        int maxStripRetries = 2,
        SpriteSkillOptions? options = null)
    {
        return new SpriteSkillEngine(provider, imageGen, skill, new CellSpec(48, 52), maxStripRetries, options);
    }

    private static readonly SpriteSkillDefinition Skill = new(
        "sprite-pet", "动作精灵图", "生成自定义动作精灵图", "你是宠物精灵图生成助手。输出严格 JSON。");

    // ---- Tests ----

    [Fact]
    public async Task RunAsync_ComposesValidSheet_FromMockedRows()
    {
        var provider = new FakeProvider { Reply = PlanJson };
        var imageGen = new FakeImageGen();
        var results = new Queue<byte[]>(new[]
        {
            StripPng(3), // idle 3 帧
            StripPng(2), // jump 2 帧
        });
        imageGen.OnGenerate = (_, _) => new ImageGenOutput(results.Dequeue(), "image/png");

        var engine = MakeEngine(provider, imageGen, Skill);
        var result = await engine.RunAsync("做两个动作", referenceDescription: null, references: null, ct: CancellationToken.None);

        Assert.True(result.Ok, result.Error ?? "");
        Assert.NotNull(result.SheetPng);
        Assert.True(result.Report!.Ok, string.Join("; ", result.Report.Issues));
        Assert.Equal(2, result.Actions!.Count);
        Assert.Equal(3, result.Actions[0].FrameCount);
    }

    [Fact]
    public async Task RunAsync_RetriesFailedStrip_ThenSucceeds()
    {
        var provider = new FakeProvider { Reply = PlanJson };
        var attempts = 0;
        var imageGen = new FakeImageGen
        {
            OnGenerate = (_, _) =>
            {
                attempts++;
                return new ImageGenOutput(attempts switch
                {
                    1 => StripPng(2),   // idle 重试 1：错误帧数
                    2 => StripPng(3),   // idle 重试 2：成功
                    _ => StripPng(2),   // jump
                }, "image/png");
            },
        };

        var engine = MakeEngine(provider, imageGen, Skill, maxStripRetries: 2);
        var result = await engine.RunAsync("做两个动作", null, null, CancellationToken.None);

        Assert.True(result.Ok, result.Error ?? "");
        Assert.True(attempts >= 3, $"expected at least 3 image-gen calls, got {attempts}");
        Assert.Equal(3, result.Actions![0].FrameCount);
    }

    [Fact]
    public async Task RunAsync_ReportsFailure_WhenPlanInvalid()
    {
        var provider = new FakeProvider { Reply = "不是 JSON" };
        var imageGen = new FakeImageGen { OnGenerate = (_, _) => throw new InvalidOperationException("不应生成") };

        var engine = MakeEngine(provider, imageGen, Skill);
        var result = await engine.RunAsync("做动作", null, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Null(result.SheetPng);
    }

    [Fact]
    public async Task RunAsync_ReportsFailure_WhenStripRetriesExhausted()
    {
        var provider = new FakeProvider { Reply = PlanJson };
        var imageGen = new FakeImageGen
        {
            OnGenerate = (_, _) => new ImageGenOutput(StripPng(2), "image/png"), // 永远 2 帧（idle 要 3）
        };

        var engine = MakeEngine(provider, imageGen, Skill, maxStripRetries: 1);
        var result = await engine.RunAsync("做两个动作", null, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Contains("idle", result.Error);
    }

    [Fact]
    public async Task RunAsync_PerFrameMode_GeneratesEachFrameAndComposes()
    {
        var perFramePlan = """
        {"identityDescription": "橘猫", "actions": [
          {"id": "idle", "frameCount": 3, "durations": [], "loop": true,
           "frames": ["pose 1", "pose 2", "pose 3"]}
        ]}
        """;
        var provider = new FakeProvider { Reply = perFramePlan };
        var genCalls = 0;
        var imageGen = new FakeImageGen
        {
            OnGenerate = (_, _) =>
            {
                genCalls++;
                using var frame = new Image<Rgba32>(48, 52);
                for (var y = 0; y < 52; y++)
                    for (var x = 0; x < 48; x++)
                        frame[x, y] = new Rgba32((byte)(50 + genCalls * 40), 120, 180, 255);
                using var ms = new MemoryStream();
                frame.SaveAsPng(ms);
                return new ImageGenOutput(ms.ToArray(), "image/png");
            },
        };

        var engine = MakeEngine(provider, imageGen, Skill,
            options: new SpriteSkillOptions(DefaultFrameCount: 3, Mode: SpriteGenMode.PerFrame));
        var result = await engine.RunAsync("做 idle", null, null, CancellationToken.None);

        Assert.True(result.Ok, result.Error ?? "");
        Assert.Equal(3, genCalls); // 逐帧 = 3 次生图
        Assert.Equal(3, result.Actions![0].FrameCount);
        Assert.NotNull(result.SheetPng);
        Assert.True(result.Report!.Ok, string.Join("; ", result.Report.Issues));
    }

    [Fact]
    public async Task RunAsync_PerFrameMode_ChainsPreviousFrameAsReference()
    {
        var perFramePlan = """
        {"identityDescription": "橘猫", "actions": [
          {"id": "idle", "frameCount": 3, "durations": [], "loop": true,
           "frames": ["pose 1", "pose 2", "pose 3"]}
        ]}
        """;
        var provider = new FakeProvider { Reply = perFramePlan };
        var imageGen = new FakeImageGen
        {
            OnGenerate = (_, _) =>
            {
                using var frame = new Image<Rgba32>(48, 52);
                for (var y = 0; y < 52; y++)
                    for (var x = 0; x < 48; x++)
                        frame[x, y] = new Rgba32(120, 90, 200, 255);
                using var ms = new MemoryStream();
                frame.SaveAsPng(ms);
                return new ImageGenOutput(ms.ToArray(), "image/png");
            },
        };

        // 用户提供 1 张 base 参考图 → 首帧 EditAsync 带 1 张；后续帧带 base+上一帧 = 2 张。
        var baseRefs = new[] { new ReferenceImage(StripPng(1), "image/png", "base.png") };
        var engine = MakeEngine(provider, imageGen, Skill,
            options: new SpriteSkillOptions(DefaultFrameCount: 3, Mode: SpriteGenMode.PerFrame));
        var result = await engine.RunAsync("做 idle", null, baseRefs, CancellationToken.None);

        Assert.True(result.Ok, result.Error ?? "");
        // 全部 3 帧都走图生图（EditAsync），首帧 1 张参考、后续帧 2 张（base+链式上一帧）。
        Assert.Equal(3, imageGen.EditReferenceCounts.Count);
        Assert.Equal(1, imageGen.EditReferenceCounts[0]);
        Assert.Equal(2, imageGen.EditReferenceCounts[1]);
        Assert.Equal(2, imageGen.EditReferenceCounts[2]);
    }
}
