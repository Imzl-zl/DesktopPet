using DesktopPet.Core.ImageGen;
using DesktopPet.Core.Scheduling;

namespace DesktopPet.Infra.Providers;

/// <summary>
/// 生图门面（windows-imagegen-design.md §3）：生图页与总结图的统一入口。
/// 职责：连接→适配器解析（按 family）、模型能力判断、透明策略选择（两段式编排）、
/// 适配器缓存。透明能力过滤在此层：适配器不做能力判断（Transparent=true 即直传）。
/// </summary>
public sealed class ImageGenService
{
    private readonly ImageModelCatalog _catalog;
    private readonly ICredentialStore _credentials;
    private readonly HttpClient _http;
    private readonly TimeSpan _requestTimeout;
    private readonly bool _strictParams;
    private readonly Dictionary<string, IImageGenProvider> _adapters = new();

    public ImageGenService(
        ImageModelCatalog catalog,
        ICredentialStore credentials,
        HttpClient httpClient,
        TimeSpan? requestTimeout = null,
        bool strictParams = false)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(300);
        _strictParams = strictParams;
    }

    /// <summary>连接可用模型的能力（UI 参数面板渲染用）。</summary>
    public ImageGenCapabilities CapabilitiesFor(ImageConnection connection, string modelId)
        => _catalog.Resolve(modelId, connection.Family).Capabilities;

    /// <summary>文生图（透明请求按模型能力自动分流：原生直传 / 绿幕两段式）。</summary>
    public async Task<ImageGenOutput> GenerateAsync(
        ImageConnection connection, string modelId, ImageGenSpec spec, CancellationToken ct)
    {
        var descriptor = _catalog.Resolve(modelId, connection.Family);
        var provider = GetAdapter(connection, modelId);
        var transparent = spec.Transparent;

        if (!transparent || descriptor.Capabilities.NativeTransparency)
            return await provider.GenerateAsync(spec, ct);

        // 绿幕两段式：请求前增强 prompt（纯绿背景规范），响应后 HSV 键控
        var strategy = new ChromakeyTransparencyStrategy();
        var enhanced = spec with
        {
            Prompt = strategy.EnhancePrompt(spec.Prompt),
            Transparent = false, // 适配器侧不发 background（模型不支持）
        };
        var output = await provider.GenerateAsync(enhanced, ct);
        return await strategy.PostProcessAsync(output, ct);
    }

    /// <summary>图生图/编辑（参考图 + 提示词）；透明请求同样走绿幕后处理。</summary>
    public async Task<ImageGenOutput> EditAsync(
        ImageConnection connection, string modelId, ImageGenSpec spec,
        IReadOnlyList<ReferenceImage> references, CancellationToken ct)
    {
        if (references is null || references.Count == 0)
            throw new ProviderException("invalid-request", "编辑至少需要一张参考图");

        var descriptor = _catalog.Resolve(modelId, connection.Family);
        var provider = GetAdapter(connection, modelId);

        if (!spec.Transparent || descriptor.Capabilities.NativeTransparency)
            return await provider.EditAsync(spec, references, ct);

        var strategy = new ChromakeyTransparencyStrategy();
        var enhanced = spec with
        {
            Prompt = strategy.EnhancePrompt(spec.Prompt),
            Transparent = false,
        };
        var output = await provider.EditAsync(enhanced, references, ct);
        return await strategy.PostProcessAsync(output, ct);
    }

    /// <summary>
    /// 多模型容错生成（总结图用，windows-imagegen-design.md §8）：首选模型失败时依次换
    /// 同连接其他模型重试；auth/rate-limit 不换（同凭据换模型无意义），直接抛。
    /// 透明请求由 GenerateAsync 内按能力分流（原生直传 / 绿幕两段式）。
    /// </summary>
    public async Task<ImageGenOutput> GenerateWithFallbackAsync(
        ImageConnection connection, string preferredModelId, ImageGenSpec spec, CancellationToken ct)
    {
        ProviderException? last = null;
        foreach (var modelId in ModelsInOrder(connection, preferredModelId))
        {
            try
            {
                return await GenerateAsync(connection, modelId, spec, ct);
            }
            catch (ProviderException ex) when (IsFallbackable(ex.Code))
            {
                last = ex; // 换下一个模型
            }
        }
        throw last ?? new ProviderException("invalid-request", "生图连接未配置可用模型");
    }

    private static IEnumerable<string> ModelsInOrder(ImageConnection connection, string preferred)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(preferred) && seen.Add(preferred))
            yield return preferred;
        foreach (var m in connection.Models)
        {
            if (seen.Add(m))
                yield return m;
        }
    }

    private static bool IsFallbackable(string code)
        => code is "network" or "server" or "timeout" or "invalid-response";

    private IImageGenProvider GetAdapter(ImageConnection connection, string modelId)
    {
        // 适配器按 (连接, 模型) 缓存：模型 id 在适配器构造时固定，换模型必须换适配器实例
        var key = $"{connection.Id}/{modelId}";
        if (_adapters.TryGetValue(key, out var cached)) return cached;

        var adapter = CreateAdapter(connection, modelId);
        _adapters[key] = adapter;
        return adapter;
    }

    private IImageGenProvider CreateAdapter(ImageConnection connection, string modelId)
    {
        if (string.Equals(connection.Family, ImageModelCatalog.FamilyOpenAi, StringComparison.OrdinalIgnoreCase))
            return new OpenAiImageGenAdapter(connection, modelId, _credentials, _http, _requestTimeout, _strictParams);

        // Gemini 族（Nano Banana 全系）
        if (string.Equals(connection.Family, ImageModelCatalog.FamilyGoogle, StringComparison.OrdinalIgnoreCase))
            return new GeminiImageGenAdapter(connection, modelId, _credentials, _http, _requestTimeout);

        throw new ProviderException("unsupported-family", $"未知生图协议族: {connection.Family}");
    }
}
