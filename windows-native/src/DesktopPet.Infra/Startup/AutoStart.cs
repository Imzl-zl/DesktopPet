using Microsoft.Win32;

namespace DesktopPet.Infra.Startup;

/// <summary>
/// 开机自启（HKCU Run 键；无需管理员权限，仅当前用户生效）。
/// 对齐 macOS 版 LoginItem.swift 语义（feature-migration 表保留项）。
/// </summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DesktopPet";

    /// <summary>当前是否已注册开机自启（值存在即视为启用，容错路径差异）。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>设置开机自启（true = 注册当前 exe；false = 移除注册项）。</summary>
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath)
            ?? throw new IOException("无法打开启动项注册表键");
        if (enabled)
        {
            var path = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法定位应用可执行文件路径");
            key.SetValue(ValueName, $"\"{path}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
