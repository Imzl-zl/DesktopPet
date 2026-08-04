using DesktopPet.Core.Ai;
using DesktopPet.Core.I18n;
using DesktopPet.Core.Personas;
using DesktopPet.Core.Storage;
using DesktopPet.Core.Scheduling;

namespace DesktopPet.Core.Tests;

/// <summary>
/// Phase 5h：Agent 配置构建 + personas.json 持久化（映射逻辑单测）。
/// </summary>
public class AgentConfigBuilderTests
{
    private static AppSettings SettingsWith(bool enabled, bool analysis, string providerId = "")
        => AppSettings.Defaults(AppLang.En) with { Ai = new AiSettings(
            Enabled: enabled, ScreenAnalysis: analysis, OutputMode: "silent",
            ScreenContextEnabled: false, ProviderId: providerId) };

    private static ProvidersFileModel ProvidersWith(params ProviderConfig[] configs)
        => new() { Models = configs.ToList() };

    private static readonly ProviderConfig Ollama = new(
        "ollama-local", "本地", "http://localhost:11434/v1", "", "qwen2.5-vl:7b",
        ModelCapabilities.Chat | ModelCapabilities.Vision, IsDefault: false);

    private static readonly ProviderConfig OpenAi = new(
        "openai-default", "OpenAI", "https://api.openai.com/v1", "openai-key", "gpt-4o",
        ModelCapabilities.Chat | ModelCapabilities.Vision, IsDefault: true);

    [Fact]
    public void Build_AnalysisRequiresMasterSwitchAndAnalysisFlag()
    {
        var personas = new PersonasFileModel();
        var providers = ProvidersWith(Ollama);

        var off = AgentConfigBuilder.Build(SettingsWith(enabled: false, analysis: true), personas, providers);
        Assert.False(off.ScreenAnalysis); // 总开关关 → 分析关（即使分析开关开）

        var on = AgentConfigBuilder.Build(SettingsWith(enabled: true, analysis: true), personas, providers);
        Assert.True(on.ScreenAnalysis);

        var noAnalysis = AgentConfigBuilder.Build(SettingsWith(enabled: true, analysis: false), personas, providers);
        Assert.False(noAnalysis.ScreenAnalysis);
    }

    [Fact]
    public void Build_PersonaPrompt_IsFullSystemPrompt()
    {
        var personas = new PersonasFileModel { SelectedId = "puppy" };
        var cfg = AgentConfigBuilder.Build(SettingsWith(true, true), personas, ProvidersWith(Ollama));

        Assert.NotNull(cfg.AnalysisPersonaPrompt);
        Assert.StartsWith(PersonaEngine.BasePrompt, cfg.AnalysisPersonaPrompt);
        Assert.Contains("小奶狗", cfg.AnalysisPersonaPrompt); // 当前人格生效
    }

    [Fact]
    public void Build_ProviderSelection_UsesSelectedThenFirst()
    {
        var personas = new PersonasFileModel();
        var providers = ProvidersWith(Ollama, OpenAi);

        var selected = AgentConfigBuilder.Build(
            SettingsWith(true, true, providerId: "openai-default"), personas, providers);
        Assert.Equal("https://api.openai.com/v1", selected.ProviderBaseUrl);
        Assert.Equal("openai-key", selected.ProviderApiKeyRef);

        var unselected = AgentConfigBuilder.Build(SettingsWith(true, true, providerId: "ghost"), personas, providers);
        Assert.Equal("http://localhost:11434/v1", unselected.ProviderBaseUrl); // 回退第一个
    }

    [Fact]
    public void Build_NoProvider_KeepsAnalysisWithNullModel()
    {
        var cfg = AgentConfigBuilder.Build(SettingsWith(true, true), new PersonasFileModel(), new ProvidersFileModel());
        Assert.True(cfg.ScreenAnalysis);
        Assert.Null(cfg.ProviderBaseUrl); // 只检测不评论（Agent 降级 Unknown 事件）
    }

    // ---- personas.json 持久化 ----

    [Fact]
    public void PersonasFile_StoreRoundtrip_CamelCase()
    {
        var store = new InMemoryJsonStore();
        var file = new PersonasFileModel
        {
            SelectedId = "custom-1",
            CustomPersonas = [new Persona("custom-1", "我的", "测试", "你是我的专属宠物。", Builtin: false)],
        };
        store.SavePersonasFile(file);
        var back = store.LoadPersonasFile();
        Assert.NotNull(back);
        Assert.Equal("custom-1", back!.SelectedId);
        Assert.Single(back.CustomPersonas);
        Assert.Equal("我的", back.CustomPersonas[0].Name);
    }

    [Fact]
    public void PersonasFile_JsonUsesCamelCaseNames()
    {
        var store = new FileJsonStore(Path.Combine(Path.GetTempPath(), "desktoppet-test-" + Guid.NewGuid().ToString("N")));
        try
        {
            var file = new PersonasFileModel { SelectedId = "warm-guy" };
            store.SavePersonasFile(file);
            var raw = File.ReadAllText(Path.Combine(store.DirectoryPath, "personas.json"));
            Assert.Contains("\"selectedId\"", raw);
            Assert.Contains("\"customPersonas\"", raw);
        }
        finally
        {
            Directory.Delete(store.DirectoryPath, recursive: true);
        }
    }
}
