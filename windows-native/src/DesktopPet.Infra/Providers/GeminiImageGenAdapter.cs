using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopPet.Core.ImageGen;
using DesktopPet.Core.Scheduling;

namespace DesktopPet.Infra.Providers;

/// <summary>
/// Gemini 族生图适配器（windows-imagegen-design.md §5.2）：
/// POST {baseUrl}/models/{model}:generateContent，Nano Banana 全系
/// （gemini-2.5-flash-image / gemini-3.1-flash-image / gemini-3-pro-image-preview）。
/// 差异点：x-goog-api-key 鉴权头、contents 多模态结构、aspectRatio+imageSize 档位、
/// 响应取 candidates[].parts[].inlineData。全系无 alpha，透明由门面走绿幕策略。
/// </summary>
public sealed class GeminiImageGenAdapter : HttpImageAdapterBase
{
    public const string GoogleFamily = "google";

    private readonly string _modelId;

    public GeminiImageGenAdapter(
        ImageConnection connection,
        ICredentialStore credentials,
        HttpClient httpClient,
        TimeSpan? requestTimeout = null)
        : base(connection, credentials, httpClient, requestTimeout)
    {
        _modelId = connection.Models.FirstOrDefault() ?? throw new ArgumentException(
            "Google 兼容连接至少需要一个模型", nameof(connection));
    }

    public override string Family => GoogleFamily;

    protected override void ApplyAuth(HttpRequestMessage request, string? apiKey)
    {
        // Gemini 官方协议：x-goog-api-key 头（非 Bearer）；本地/中转若用 Bearer 属 openai family
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
    }

    protected override string EndpointPath(bool references)
        => $"models/{_modelId}:generateContent"; // 生成与编辑同端点，差异在 contents

    protected override bool HasReducibleParameters(ImageGenSpec spec) => false; // Gemini 无 quality/background 参数，400 直接报错

    protected override JsonObject BuildGenerateBody(ImageGenSpec spec, bool reduced)
    {
        var parts = new JsonArray { new JsonObject { ["text"] = spec.Prompt } };

        var generationConfig = new JsonObject
        {
            ["responseModalities"] = new JsonArray { "IMAGE" }, // 必须显式声明，否则可能只回文本
        };
        var imageConfig = new JsonObject();
        if (spec.AspectRatio != ImageAspectRatio.Auto)
            imageConfig["aspectRatio"] = AspectRatioName(spec.AspectRatio);
        imageConfig["imageSize"] = ScaleName(spec.Scale);
        generationConfig["imageConfig"] = imageConfig;

        return new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["parts"] = parts },
            },
            ["generationConfig"] = generationConfig,
        };
    }

    /// <summary>编辑：参考图以 inlineData 嵌进 contents parts（Gemini 多模态形态）。</summary>
    protected override JsonObject BuildEditBody(ImageGenSpec spec, IReadOnlyList<ReferenceImage> references)
    {
        var parts = new JsonArray();
        foreach (var r in references)
        {
            parts.Add(new JsonObject
            {
                ["inlineData"] = new JsonObject
                {
                    ["mimeType"] = r.MimeType,
                    ["data"] = Convert.ToBase64String(r.Bytes),
                },
            });
        }
        parts.Add(new JsonObject { ["text"] = spec.Prompt });

        var generationConfig = new JsonObject { ["responseModalities"] = new JsonArray { "IMAGE" } };
        var imageConfig = new JsonObject();
        if (spec.AspectRatio != ImageAspectRatio.Auto)
            imageConfig["aspectRatio"] = AspectRatioName(spec.AspectRatio);
        imageConfig["imageSize"] = ScaleName(spec.Scale);
        generationConfig["imageConfig"] = imageConfig;

        return new JsonObject
        {
            ["contents"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["parts"] = parts },
            },
            ["generationConfig"] = generationConfig,
        };
    }

    protected override Task<byte[]> ParseResponseAsync(JsonDocument response, CancellationToken ct)
    {
        var root = response.RootElement;
        if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            throw new ProviderException("invalid-response", "Gemini 生图响应无候选");

        var parts = candidates[0].GetProperty("content").GetProperty("parts");
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("inlineData", out var inline)
                && inline.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.String)
            {
                return Task.FromResult(Convert.FromBase64String(data.GetString()!));
            }
        }
        throw new ProviderException("invalid-response", "Gemini 生图响应缺少 inlineData 图片");
    }

    private static string AspectRatioName(ImageAspectRatio ratio) => ratio switch
    {
        ImageAspectRatio.R3x2 => "3:2",
        ImageAspectRatio.R2x3 => "2:3",
        ImageAspectRatio.R4x3 => "4:3",
        ImageAspectRatio.R3x4 => "3:4",
        ImageAspectRatio.R16x9 => "16:9",
        ImageAspectRatio.R9x16 => "9:16",
        ImageAspectRatio.R21x9 => "21:9",
        _ => "1:1",
    };

    private static string ScaleName(ImageScale scale) => scale switch
    {
        ImageScale.S2K => "2K",
        ImageScale.S4K => "4K",
        _ => "1K",
    };
}
