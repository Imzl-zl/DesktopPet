using DesktopPet.Core.Tts;

namespace DesktopPet.Core.Tests;

/// <summary>
/// TTS Provider 注册表（windows-tts-design.md §4.2）：引擎选择 + 音色解析/回退。
/// 纯逻辑可单测：引擎不存在回退默认；音色空/失效回退自动（按语言）。
/// </summary>
public class TtsProviderRegistryTests
{
    private sealed class FakeProvider(string id, bool network = false) : ITtsProvider
    {
        public string Id { get; } = id;
        public bool RequiresNetwork { get; } = network;
        public Task<IReadOnlyList<TtsVoiceInfo>> ListVoicesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<TtsVoiceInfo>>([]);
        public Task<Stream> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken ct)
            => Task.FromResult<Stream>(new MemoryStream());
    }

    private static readonly ITtsProvider[] Providers =
        [new FakeProvider("sapi"), new FakeProvider("onecore"), new FakeProvider("openai", network: true)];

    [Fact]
    public void ResolveProvider_ExactId_Wins()
    {
        Assert.Equal("onecore", TtsProviderRegistry.ResolveProvider(Providers, "onecore").Id);
        Assert.Equal("openai", TtsProviderRegistry.ResolveProvider(Providers, "openai").Id);
    }

    [Fact]
    public void ResolveProvider_UnknownId_FallsBackToDefault()
    {
        // 未知/空引擎 id（如旧数据或用户删了端点配置）→ 默认 sapi 兜底
        Assert.Equal("sapi", TtsProviderRegistry.ResolveProvider(Providers, "edge").Id);
        Assert.Equal("sapi", TtsProviderRegistry.ResolveProvider(Providers, "").Id);
    }

    [Fact]
    public void ResolveProvider_MissingFallback_ReturnsFirstAvailable()
    {
        // 默认引擎不在列表（极端：无 SAPI）→ 第一个可用
        var only = new[] { new FakeProvider("onecore") };
        Assert.Equal("onecore", TtsProviderRegistry.ResolveProvider(only, "sapi").Id);
    }

    [Fact]
    public void ResolveVoice_EmptyVoiceId_PicksLanguageMatch()
    {
        var voices = new TtsVoiceInfo[]
        {
            new("en-US-Jenny", "Jenny", "en-US", "female"),
            new("zh-CN-Xiaoxiao", "晓晓", "zh-CN", "female"),
            new("vi-VN-Hoai", "Hoai", "vi-VN", "female"),
        };
        var picked = TtsProviderRegistry.ResolveVoice(voices, "", "zhHans");
        Assert.NotNull(picked);
        Assert.Equal("zh-CN-Xiaoxiao", picked!.Id);
    }

    [Fact]
    public void ResolveVoice_EmptyVoiceId_NoLanguageMatch_TakesFirst()
    {
        var voices = new TtsVoiceInfo[] { new("en-US-Jenny", "Jenny", "en-US", "female") };
        var picked = TtsProviderRegistry.ResolveVoice(voices, "", "zhHans");
        Assert.NotNull(picked);
        Assert.Equal("en-US-Jenny", picked!.Id);
    }

    [Fact]
    public void ResolveVoice_ExactVoiceId_Wins()
    {
        var voices = new TtsVoiceInfo[] { new("A", "a", "en-US"), new("B", "b", "zh-CN") };
        var picked = TtsProviderRegistry.ResolveVoice(voices, "B", "en");
        Assert.Equal("B", picked!.Id);
    }

    [Fact]
    public void ResolveVoice_StaleVoiceId_FallsBackToAuto()
    {
        // 引擎切换后旧音色 id 失效（如 SAPI 名切到 OneCore）→ 按语言回退，不报错
        var voices = new TtsVoiceInfo[] { new("zh-CN-Yaoyao", "瑶瑶", "zh-CN") };
        var picked = TtsProviderRegistry.ResolveVoice(voices, "zh-CN-XiaoxiaoNeural", "zhHans");
        Assert.NotNull(picked);
        Assert.Equal("zh-CN-Yaoyao", picked!.Id);
    }

    [Fact]
    public void ResolveVoice_EmptyList_ReturnsNull()
    {
        Assert.Null(TtsProviderRegistry.ResolveVoice([], "anything", "zhHans"));
        Assert.Null(TtsProviderRegistry.ResolveVoice([], "", "zhHans"));
    }

    [Fact]
    public void ResolveVoice_LanguageMapping_CoversAllUiLanguages()
    {
        var voices = new TtsVoiceInfo[]
        {
            new("en-US-Jenny", "Jenny", "en-US"),
            new("zh-CN-Xiaoxiao", "晓晓", "zh-CN"),
            new("zh-TW-Hsiao", "曉臻", "zh-TW"),
            new("vi-VN-Hoai", "Hoai", "vi-VN"),
        };
        Assert.Equal("en-US-Jenny", TtsProviderRegistry.ResolveVoice(voices, "", "en")!.Id);
        Assert.Equal("zh-CN-Xiaoxiao", TtsProviderRegistry.ResolveVoice(voices, "", "zhHans")!.Id);
        Assert.Equal("zh-TW-Hsiao", TtsProviderRegistry.ResolveVoice(voices, "", "zhHant")!.Id);
        Assert.Equal("vi-VN-Hoai", TtsProviderRegistry.ResolveVoice(voices, "", "vi")!.Id);
    }
}
