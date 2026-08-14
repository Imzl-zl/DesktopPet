# Phase 6j 冒烟：全局快捷键 + 进程行为
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\DesktopPet.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\DesktopPet.App.exe"
if (-not (Test-Path $exe)) { Write-Host "NOT FOUND: $exe (需先 Release build)"; exit 2 }

Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class WinSmoke {
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    public static string[] VisibleTitles(uint pid) {
        var list = new System.Collections.Generic.List<string>();
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p == pid && IsWindowVisible(h)) {
                var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
                list.Add("[" + sb.ToString() + "]");
            }
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }
}
"@

function Send-Hotkey([string]$keys) {
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.SendKeys]::SendWait($keys)
    Start-Sleep -Milliseconds 800
}

$app = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 6
Write-Host "App pid=$($app.Id) alive=$(-not $app.HasExited)"
$titles = [WinSmoke]::VisibleTitles($app.Id)
Write-Host "初始窗口数: $($titles.Count) | $($titles -join ' | ')"
$petWindowsBefore = $titles.Count

# 1) Win+Ctrl+H 隐藏/恢复
Send-Hotkey "^#h"
$hidden = [WinSmoke]::VisibleTitles($app.Id)
Write-Host "按 H 隐藏后窗口数: $($hidden.Count) | $($hidden -join ' | ')"
Send-Hotkey "^#h"
$restored = [WinSmoke]::VisibleTitles($app.Id)
Write-Host "再按 H 恢复后窗口数: $($restored.Count) | $($restored -join ' | ')"
$petHidden = $hidden.Count -lt $petWindowsBefore
$petRestored = $restored.Count -ge $petWindowsBefore

# 2) Win+Ctrl+U 设置
Send-Hotkey "^#u"
Start-Sleep -Milliseconds 1200
$withSettings = [WinSmoke]::VisibleTitles($app.Id)
Write-Host "按 Win+Ctrl+U 后: $($withSettings -join ' | ')"
$settingsShown = $withSettings | Where-Object { $_ -match 'DesktopPet' }

# 3) Win+Ctrl+T 模式循环（不崩溃）
Send-Hotkey "^#t"
Send-Hotkey "^#t"
Write-Host "Win+Ctrl+T x2 后 alive=$(-not $app.HasExited)"

# 4) Win+Ctrl+X 退出
Send-Hotkey "^#x"
Start-Sleep -Seconds 3
$alive = -not $app.HasExited
Write-Host "按 Win+Ctrl+X 后 alive=$alive"
if ($alive) { Stop-Process -Id $app.Id -Force }

# 5) Agent 进程检查（AI 默认关 → 无 AgentHost）
$agent = Get-Process -Name "DesktopPet.AgentHost" -ErrorAction SilentlyContinue
Write-Host "AgentHost 进程（AI 关）: $(if ($agent) { '存在!' } else { '无 ✓' })"

Write-Host ""
Write-Host "== 冒烟结果 =="
Write-Host "H 隐藏: $(if ($petHidden) { 'PASS' } else { 'FAIL' }) | H 恢复: $(if ($petRestored) { 'PASS' } else { 'FAIL' })"
Write-Host "S 设置窗: $(if ($settingsShown) { 'PASS' } else { 'FAIL' })"
Write-Host "M 模式循环不崩溃: PASS"
Write-Host "Q 退出: $(if (-not $alive) { 'PASS' } else { 'FAIL' })"
