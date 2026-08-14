using System.Reflection;
using System.Text.Json;

namespace DesktopPet.Core.ImageGen;

/// <summary>
/// 渠道模板目录（v2 修订，windows-imagegen-v2-design.md §2/§4）：
/// 用户显式选择厂家/渠道，渠道行为（编辑形态/尺寸形态/鉴权等）进数据，不依赖模型 id 推断。
/// 能力解析优先级：模型级声明（modelCapabilities）> 渠道模板（connection.Channel）> 目录/推断。
/// 新增渠道 = 改 Resources/channels.json，零代码。
/// </summary>
public sealed class ImageChannelCatalog
{
    public const string ResourceName = "DesktopPet.Core.Resources.channels.json";
    public const string CustomChannel = "custom"; // 自定义端点（无模板行为，走 family + 推断）

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IReadOnlyList<ImageChannelTemplate> _channels;
    private readonly Dictionary<string, ImageChannelTemplate> _byId;

    public ImageChannelCatalog(IReadOnlyList<ImageChannelTemplate> channels)
    {
        _channels = channels;
        _byId = channels.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ImageChannelTemplate> All => _channels;

    /// <summary>精确匹配模板；未知/空 id 返回 null（自定义端点语义）。</summary>
    public ImageChannelTemplate? Find(string channelId)
        => string.IsNullOrWhiteSpace(channelId) ? null : _byId.TryGetValue(channelId, out var t) ? t : null;

    /// <summary>渠道级默认能力（无模板/无声明返回 null）。</summary>
    public CustomImageCapabilities? CapabilitiesFor(string channelId)
        => Find(channelId)?.Capabilities;

    public static ImageChannelCatalog LoadBuiltIn()
    {
        var assembly = typeof(ImageChannelCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"缺失内置资源 {ResourceName}");
        return Load(stream);
    }

    public static ImageChannelCatalog Load(Stream json)
    {
        var file = JsonSerializer.Deserialize<ImageChannelCatalogFile>(json, JsonOptions)
            ?? throw new InvalidOperationException("channels.json 解析失败");
        return new ImageChannelCatalog(file.Channels ?? []);
    }
}

public sealed record ImageChannelCatalogFile(List<ImageChannelTemplate>? Channels);
