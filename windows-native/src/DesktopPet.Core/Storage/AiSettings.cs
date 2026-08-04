namespace DesktopPet.Core.Storage;

/// <summary>
/// AI 设置（Phase 5；对齐迁移计划 §5：AI 总开关 / 分析开关 / 输出模式 / 屏幕上下文开关）。
/// 人格选择不在此处（唯一真值 = personas.json 的 selectedId，见 PersonasFileModel）。
/// </summary>
public sealed record AiSettings(
    bool Enabled,               // AI 总开关：关 = 纯桌宠（无截屏/无网络/无后台进程）
    bool ScreenAnalysis,        // 截屏分析开关（默认关，隐私优先）
    string OutputMode,          // danmaku | chat | silent（模式只决定 AI 主动输出形式）
    bool ScreenContextEnabled,  // 对话携带屏幕上下文（默认关，隐私）
    string ProviderId)          // 选中的模型 provider id（空 = 未配置）
{
    public static AiSettings Defaults => new(
        Enabled: false,
        ScreenAnalysis: false,
        OutputMode: "silent",
        ScreenContextEnabled: false,
        ProviderId: "");

    public static AiSettings Normalize(AiSettings? raw)
    {
        if (raw is null) return Defaults;
        var mode = raw.OutputMode switch
        {
            "danmaku" => "danmaku",
            "chat" => "chat",
            _ => "silent",
        };
        return new AiSettings(
            raw.Enabled,
            raw.ScreenAnalysis,
            mode,
            raw.ScreenContextEnabled,
            raw.ProviderId ?? "");
    }
}
