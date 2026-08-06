using DesktopPet.Core.Personas;
using DesktopPet.Core.Scheduling;
using DesktopPet.Core.Storage;

namespace DesktopPet.Core.Ai;

/// <summary>
/// Agent 配置构建（App 侧 → IPC 下发；映射逻辑在 Core 可单测）。
/// 规则：分析开关 = AI 总开关 ∧ 分析开关；人格 prompt 每轮构建（防漂移）；
/// 模型连接取选中 provider（无选中 → 第一个；无任何配置 → 只检测不评论）。
/// </summary>
public static class AgentConfigBuilder
{
    public static AgentConfig Build(
        AppSettings settings, PersonasFileModel personas, ProvidersFileModel providers)
    {
        var persona = personas.ResolveSelected();
        var provider = SelectProvider(providers, settings.Ai.ProviderId);

        return new AgentConfig(
            ScreenAnalysis: settings.Ai.Enabled && settings.Ai.ScreenAnalysis,
            AnalysisPersonaPrompt: PersonaEngine.BuildSystemPrompt(persona),
            ProviderBaseUrl: provider?.BaseUrl,
            ProviderModel: provider?.ModelName,
            ProviderApiKeyRef: string.IsNullOrEmpty(provider?.ApiKeyRef) ? null : provider.ApiKeyRef,
            ProviderReasoningEffort: provider?.ReasoningEffort,
            MinAnalysisIntervalSeconds: Math.Clamp(settings.Ai.ScreenAnalysisIntervalSeconds, 3, 30));
    }

    /// <summary>选中 id 匹配优先，否则取第一个；无配置 → null（Agent 只做变化检测）。</summary>
    public static ProviderConfig? SelectProvider(ProvidersFileModel providers, string selectedId)
    {
        var models = providers.Models;
        if (models.Count == 0) return null;
        var selected = models.FirstOrDefault(p => p.Id == selectedId);
        return selected ?? models[0];
    }
}
