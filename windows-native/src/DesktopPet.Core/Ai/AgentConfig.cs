namespace DesktopPet.Core.Ai;

/// <summary>
/// Agent 运行配置（App 通过 IPC 下发；Agent 不直接读 personas.json/providers.json，
/// 避免双进程各自解析带来真值分叉——人格 prompt 与模型连接由 App 构建好下发）。
/// </summary>
public sealed record AgentConfig(
    bool ScreenAnalysis,               // 分析开关：false = 不截屏不分析（静默）
    string? AnalysisPersonaPrompt,     // 完整分析人格 prompt（Base+Persona，App 构建）
    string? ProviderBaseUrl,           // null/空 = 无模型连接（只做变化检测，评论降级为空）
    string? ProviderModel,
    string? ProviderApiKeyRef,
    string? ProviderReasoningEffort,   // 推理模型开关（如 "none"；空 = 不发送，token 可能被思考耗尽）
    int MinAnalysisIntervalSeconds)    // 云端分析限频（默认 5）
{
    public static AgentConfig Defaults => new(
        ScreenAnalysis: false,
        AnalysisPersonaPrompt: null,
        ProviderBaseUrl: null,
        ProviderModel: null,
        ProviderApiKeyRef: null,
        ProviderReasoningEffort: null,
        MinAnalysisIntervalSeconds: 5);
}
