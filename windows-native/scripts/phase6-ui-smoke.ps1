﻿﻿# Phase 6 UI 冒烟：设置窗 → AI 页 → 截图（验证陪伴功能开关组/人格编辑/生图连接/日记入口）
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\DesktopPet.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\DesktopPet.App.exe"
$out = Join-Path $root "phase6-ai-page.png"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

function Find-ByName([System.Windows.Automation.AutomationElement]$rootEl, [string]$name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $rootEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

$app = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 6

# Win+Ctrl+U 打开设置
[System.Windows.Forms.SendKeys]::SendWait("^#u")
Start-Sleep -Seconds 2

$desktop = [System.Windows.Automation.AutomationElement]::RootElement
$settingsWin = $null
foreach ($w in $desktop.FindAll([System.Windows.Automation.TreeScope]::Children,
    [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($w.Current.Name -eq "DesktopPet") { $settingsWin = $w; break }
}
if ($null -eq $settingsWin) { Write-Host "FAIL: 设置窗口未找到"; Stop-Process -Id $app.Id -Force; exit 1 }
Write-Host "PASS: 设置窗口已打开"

# 找 AI 导航按钮（图标按钮，Name 可能是 AI 相关）
$aiBtn = $null
foreach ($b in $settingsWin.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($b.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
        $b.Current.Name -match "AI|ai|智能") { $aiBtn = $b; break }
}
if ($null -eq $aiBtn) { Write-Host "FAIL: AI 导航按钮未找到"; Stop-Process -Id $app.Id -Force; exit 1 }
$aiBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 2
Write-Host "PASS: AI 页导航成功（$($aiBtn.Current.Name)）"

# 截图设置窗口（UIAutomation BoundingRectangle + CopyFromScreen）
Add-Type -AssemblyName System.Drawing
$rect = $settingsWin.Current.BoundingRectangle
$w = [int]$rect.Width; $h = [int]$rect.Height
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen([int]$rect.X, [int]$rect.Y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "PASS: AI 页截图已保存 $out"

# 清理
[System.Windows.Forms.SendKeys]::SendWait("^%q")
Start-Sleep -Seconds 2
if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force }
Write-Host "DONE"
