using DesktopPet.Core.ImageGen;
using DesktopPet.Core.Scheduling;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace DesktopPet.Core.SpriteSkill;

/// <summary>技能编排结果。</summary>
public sealed record SpriteSkillResult(
    bool Ok,
    byte[]? SheetPng,
    IReadOnlyList<ActionSpec>? Actions,
    SheetReport? Report,
    string? Error);

/// <summary>
/// 动作精灵图技能编排器：LLM 产出动作计划 → 逐动作调生图生成行贴图 →
/// SpritePipeline 切帧/拼图/校验 → 输出桌宠可加载的图集 PNG。
/// 复用现有 IModelProvider（对话）与 IImageGenProvider（生图），Core 层零 IO
/// （字节进、字节出，文件读写由 App 层负责）。
/// </summary>
public sealed class SpriteSkillEngine
{
    private readonly SpritePromptGenerator _promptGenerator;
    private readonly IImageGenProvider _imageGen;
    private readonly SpriteSkillDefinition _skill;
    private readonly CellSpec _cell;
    private readonly int _maxStripRetries;
    private readonly SpriteSkillOptions _options;

    public SpriteSkillEngine(
        IModelProvider model,
        IImageGenProvider imageGen,
        SpriteSkillDefinition skill,
        CellSpec cell,
        int maxStripRetries = 2,
        SpriteSkillOptions? options = null)
    {
        _promptGenerator = new SpritePromptGenerator(model);
        _imageGen = imageGen ?? throw new ArgumentNullException(nameof(imageGen));
        _skill = skill ?? throw new ArgumentNullException(nameof(skill));
        _cell = cell;
        _maxStripRetries = Math.Max(1, maxStripRetries);
        _options = options ?? new SpriteSkillOptions();
    }

    /// <summary>执行完整编排。任何一步失败返回 Ok=false + Error（不抛未捕获异常）。</summary>
    public async Task<SpriteSkillResult> RunAsync(
        string userRequest,
        string? referenceDescription,
        IReadOnlyList<ReferenceImage>? references,
        CancellationToken ct)
    {
        try
        {
            var plan = await _promptGenerator.GenerateActionPlanAsync(_skill, userRequest, referenceDescription, _options, ct);

            var rows = new List<IReadOnlyList<Image<Rgba32>>>();
            foreach (var action in plan.Actions)
            {
                rows.Add(await GenerateRowFramesAsync(action, references, ct));
            }

            using var sheet = SpritePipeline.ComposeSheet(rows, _cell);
            var report = SpritePipeline.ValidateSheet(sheet, plan.Actions.Select(a => a.Spec).ToList(), _cell);
            if (!report.Ok)
            {
                return new SpriteSkillResult(false, null, null, report,
                    $"拼图校验失败：{string.Join("; ", report.Issues)}");
            }

            byte[] png;
            using (var ms = new MemoryStream())
            {
                // 显式 RGBA 编码：SaveAsPng 对 RGB 源图会丢 alpha（见 Chromakey 修复）。
                var encoder = new PngEncoder { ColorType = PngColorType.RgbWithAlpha };
                sheet.Save(ms, encoder);
                png = ms.ToArray();
            }

            return new SpriteSkillResult(true, png, plan.Actions.Select(a => a.Spec).ToList(), report, null);
        }
        catch (Exception ex)
        {
            return new SpriteSkillResult(false, null, null, null, ex.Message);
        }
    }

    /// <summary>单个动作：按所选模式生成帧（RowStrip=一行多帧｜PerFrame=逐帧）。帧数不匹配/全透明则重试（最多 maxStripRetries 次）。</summary>
    private async Task<IReadOnlyList<Image<Rgba32>>> GenerateRowFramesAsync(
        SpriteActionSpec action,
        IReadOnlyList<ReferenceImage>? references,
        CancellationToken ct)
    {
        if (_options.Mode == SpriteGenMode.PerFrame)
            return await GeneratePerFrameAsync(action, references, ct);
        return await GenerateRowStripAsync(action, references, ct);
    }

    /// <summary>逐帧模式：每帧单姿势生成，但必须图生图锚定——首帧带用户参考图（base 身份锚），后续帧额外带上一帧（链式连续性）。无参考图时回退文生图（身份靠 prompt，稳定性差）。</summary>
    private async Task<IReadOnlyList<Image<Rgba32>>> GeneratePerFrameAsync(
        SpriteActionSpec action,
        IReadOnlyList<ReferenceImage>? references,
        CancellationToken ct)
    {
        var prompts = action.FramePrompts ?? [];
        if (prompts.Count != action.Spec.FrameCount)
        {
            throw new SpritePipelineException(
                $"动作 {action.Spec.Id} 的帧提示词数量（{prompts.Count}）与帧数（{action.Spec.FrameCount}）不符");
        }
        var frames = new List<Image<Rgba32>>();
        ReferenceImage? prevFrame = null;
        for (var i = 0; i < prompts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var spec = new ImageGenSpec(
                Prompt: prompts[i],
                AspectRatio: ImageAspectRatio.R1x1, // 单帧方形
                Scale: ImageScale.S1K,
                Transparent: true,
                ReferenceImages: references);

            // 链式参考图：base 身份锚（用户参考图）+ 上一帧（连续性）。
            var chainRefs = BuildChainReferences(references, prevFrame);
            var output = chainRefs.Count > 0
                ? await _imageGen.EditAsync(spec, chainRefs, ct)
                : await _imageGen.GenerateAsync(spec, ct);
            using var frame = Image.Load<Rgba32>(output.Bytes);
            frames.Add(frame.Clone());
            prevFrame = new ReferenceImage(output.Bytes, output.MimeType, $"{action.Spec.Id}-f{i}");
        }
        return frames;
    }

    /// <summary>链式参考图：[用户 base 参考图…] + 上一帧（若存在）。</summary>
    private static IReadOnlyList<ReferenceImage> BuildChainReferences(
        IReadOnlyList<ReferenceImage>? baseReferences,
        ReferenceImage? prevFrame)
    {
        var refs = new List<ReferenceImage>();
        if (baseReferences is { Count: > 0 })
            refs.AddRange(baseReferences);
        if (prevFrame is not null)
            refs.Add(prevFrame);
        return refs;
    }

    /// <summary>行贴图模式：一次生成 N 帧横排，切帧；帧数不匹配/全透明则重试。</summary>
    private async Task<IReadOnlyList<Image<Rgba32>>> GenerateRowStripAsync(
        SpriteActionSpec action,
        IReadOnlyList<ReferenceImage>? references,
        CancellationToken ct)
    {
        SpritePipelineException? lastError = null;
        for (var attempt = 0; attempt < _maxStripRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var spec = new ImageGenSpec(
                Prompt: action.RowPrompt,
                AspectRatio: ImageAspectRatio.R21x9,   // 行贴图：横向宽幅
                Scale: ImageScale.S1K,
                Transparent: true,                      // 透明由 provider 的透明策略保证
                ReferenceImages: references);
            var output = references is { Count: > 0 }
                ? await _imageGen.EditAsync(spec, references, ct)
                : await _imageGen.GenerateAsync(spec, ct);

            using var strip = Image.Load<Rgba32>(output.Bytes);
            try
            {
                var frames = SpritePipeline.SliceStrip(strip, action.Spec.FrameCount);
                if (frames.Count == 0)
                {
                    lastError = new SpritePipelineException("行贴图全透明（无内容）");
                    continue;
                }
                return frames;
            }
            catch (SpritePipelineException ex)
            {
                lastError = ex; // 帧数不匹配等 → 重试重新生成
            }
        }
        throw new SpritePipelineException(
            $"动作 {action.Spec.Id} 行贴图生成失败（重试 {_maxStripRetries} 次）：{lastError?.Message}");
    }
}
