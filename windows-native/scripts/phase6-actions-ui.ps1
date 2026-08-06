# Phase 6 动作页 + 气泡页 UI 冒烟：验证宠物选择器/待机轮播/触发器网格/时长滑块/台词池 → 截图
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\DesktopPet.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\DesktopPet.App.exe"
$out = Join-Path $root "phase6-actions-page.png"

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

[System.Windows.Forms.SendKeys]::SendWait("^%s")
Start-Sleep -Seconds 2

$desktop = [System.Windows.Automation.AutomationElement]::RootElement
$settingsWin = $null
foreach ($w in $desktop.FindAll([System.Windows.Automation.TreeScope]::Children,
    [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($w.Current.Name -eq "DesktopPet") { $settingsWin = $w; break }
}
if ($null -eq $settingsWin) { Write-Host "FAIL: 设置窗口未找到"; Stop-Process -Id $app.Id -Force; exit 1 }
Write-Host "PASS: 设置窗口已打开"

# 动作导航按钮（AutomationProperties.SetName → Name="动作"）
$actionsBtn = Find-ByName $settingsWin "动作"
if ($null -eq $actionsBtn) { Write-Host "FAIL: 动作导航按钮未找到"; Stop-Process -Id $app.Id -Force; exit 1 }
$actionsBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 2
Write-Host "PASS: 动作页导航成功"

# 宠物选择器（ComboBox）
$combo = $settingsWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ComboBox)))
if ($null -eq $combo) { Write-Host "FAIL: 宠物选择器未找到"; Stop-Process -Id $app.Id -Force; exit 1 }
Write-Host "PASS: 宠物选择器存在"

# 待机轮播开关
$idleToggle = Find-ByName $settingsWin "待机动作轮播"
if ($null -eq $idleToggle) { Write-Host "FAIL: 待机轮播开关未找到"; Stop-Process -Id $app.Id -Force; exit 1 }
Write-Host "PASS: 待机轮播开关存在"

# 播放模式单选
$randomRadio = Find-ByName $settingsWin "随机（每次随机挑一个动作，可能有重复）"
if ($null -eq $randomRadio) { Write-Host "FAIL: 随机模式单选未找到"; Stop-Process -Id $app.Id -Force; exit 1 }
Write-Host "PASS: 播放模式单选存在"

# 恢复默认按钮
$resetBtn = Find-ByName $settingsWin "恢复默认"
if ($null -eq $resetBtn) { Write-Host "FAIL: 恢复默认按钮未找到"; Stop-Process -Id $app.Id -Force; exit 1 }
Write-Host "PASS: 恢复默认按钮存在"

# 时长滑块（动作页新增：点击/庆祝）
$clickDuration = Find-ByName $settingsWin "点击动作时长"
if ($null -eq $clickDuration) { Write-Host "FAIL: 点击动作时长滑块未找到"; Stop-Process -Id $app.Id -Force; exit 1 }
Write-Host "PASS: 点击动作时长滑块存在"
$celebrateDuration = Find-ByName $settingsWin "庆祝时长"
if ($null -eq $celebrateDuration) { Write-Host "FAIL: 庆祝时长滑块未找到"; Stop-Process -Id $app.Id -Force; exit 1 }
Write-Host "PASS: 庆祝时长滑块存在"

# 气泡页：台词池编辑框（新增）
$bubbleNav = Find-ByName $settingsWin "气泡"
if ($null -eq $bubbleNav) { Write-Host "FAIL: 气泡导航未找到"; Stop-Process -Id $app.Id -Force; exit 1 }
$bubbleNav.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 2
$chatter = Find-ByName $settingsWin "闲谈台词池"
if ($null -eq $chatter) { Write-Host "FAIL: 闲谈台词池编辑框未找到"; Stop-Process -Id $app.Id -Force; exit 1 }
Write-Host "PASS: 闲谈台词池编辑框存在"
$hungry = Find-ByName $settingsWin "饥饿台词池"
if ($null -eq $hungry) { Write-Host "FAIL: 饥饿台词池编辑框未找到"; Stop-Process -Id $app.Id -Force; exit 1 }
Write-Host "PASS: 饥饿台词池编辑框存在"

# 截图设置窗口
$rect = $settingsWin.Current.BoundingRectangle
$w = [int]$rect.Width; $h = [int]$rect.Height
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen([int]$rect.X, [int]$rect.Y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Host "PASS: 动作页截图已保存 $out"

# 清理
[System.Windows.Forms.SendKeys]::SendWait("^%q")
Start-Sleep -Seconds 2
if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force }
Write-Host "DONE"
