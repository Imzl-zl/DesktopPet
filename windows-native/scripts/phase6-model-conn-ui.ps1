# 验证：设置页 AI 页 → 模型连接编辑（✏ 编辑，下拉旁）→ 改值保存 → providers.json 生效
$ErrorActionPreference = "Stop"
$root = "C:\sudy\github\DesktopPet\windows-native"
$exe = Join-Path $root "src\DesktopPet.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\DesktopPet.App.exe"
$providersPath = Join-Path $env:APPDATA "DesktopPet\providers.json"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$before = Get-Content $providersPath -Raw

$app = Start-Process -FilePath $exe -ArgumentList "--settings" -PassThru
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

# AI 页导航
$aiBtn = $null
foreach ($b in $settingsWin.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($b.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
        $b.Current.Name -match "AI|ai|智能") { $aiBtn = $b; break }
}
if ($null -eq $aiBtn) { Write-Host "FAIL: AI 导航按钮未找到"; Stop-Process -Id $app.Id -Force; exit 1 }
$aiBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 1
Write-Host "PASS: AI 页导航成功"

# 找编辑按钮（下拉旁，无需滚动；找不到则滚动后找）
$editBtn = $null
foreach ($b in $settingsWin.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($b.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
        $b.Current.Name -match "编辑") { $editBtn = $b; break }
}
if ($null -eq $editBtn) {
    foreach ($el in $settingsWin.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)) {
        try {
            $sp = $null
            if ($el.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern, [ref]$sp)) {
                $sp.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 100)
            }
        } catch {}
    }
    Start-Sleep -Seconds 1
    foreach ($b in $settingsWin.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($b.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
            $b.Current.Name -match "编辑") { $editBtn = $b; break }
    }
}
if ($null -eq $editBtn) { Write-Host "FAIL: 编辑按钮未找到"; Stop-Process -Id $app.Id -Force; exit 1 }
Write-Host "PASS: 编辑按钮可见于 $($editBtn.Current.BoundingRectangle)"
$editBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 2

# 弹窗（含 ≥3 输入框的 App 顶层窗口）
$dlg = $null
foreach ($w in $desktop.FindAll([System.Windows.Automation.TreeScope]::Children,
    [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($w.Current.ProcessId -ne $app.Id) { continue }
    $edits = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Edit)))
    if ($edits.Count -ge 3) { $dlg = $w; break }
}
if ($null -eq $dlg) { Write-Host "FAIL: 模型连接弹窗未出现"; Stop-Process -Id $app.Id -Force; exit 1 }
Write-Host "PASS: 模型连接弹窗已出现"

$boxes = $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)))
$i = 0
foreach ($box in $boxes) {
    $val = $box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    Write-Host "  输入框[$i]: $($val.Substring(0, [Math]::Min(60, $val.Length)))"
    $i++
}
$box0 = $boxes[0].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
$box1 = $boxes[1].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
$hasUrl = $box0.Current.Value -match "newapi"
Write-Host "  端点回显: $(if ($hasUrl) { 'PASS' } else { 'FAIL' })"

# 改模型名并保存（追加 -ui-test 后缀验证保存链路）
$newModel = $box1.Current.Value + "-ui-test"
$box1.SetValue($newModel)
Start-Sleep -Milliseconds 300
$saveBtn = $null
foreach ($b in $dlg.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($b.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and
        $b.Current.Name -match "保存") { $saveBtn = $b; break }
}
if ($null -eq $saveBtn) { Write-Host "FAIL: 保存按钮未找到"; Stop-Process -Id $app.Id -Force; exit 1 }
$saveBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 2

# 验证 providers.json 已更新
$after = Get-Content $providersPath -Raw
if ($after -match "ui-test") {
    Write-Host "PASS: providers.json 已更新（模型名=$newModel）"
} else {
    Write-Host "FAIL: providers.json 未更新"
    Write-Host "  before: $($before.Substring(0, [Math]::Min(200, $before.Length)))"
    Write-Host "  after:  $($after.Substring(0, [Math]::Min(200, $after.Length)))"
}

# 恢复原配置（改回模型名）
$clean = $after -replace "-ui-test", ""
[System.IO.File]::WriteAllText($providersPath, $clean, [System.Text.UTF8Encoding]::new($false))
Write-Host "== 验证完成（配置已恢复）=="
Stop-Process -Id $app.Id -Force
