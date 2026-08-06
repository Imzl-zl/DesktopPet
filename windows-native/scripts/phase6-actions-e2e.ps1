# Phase 6 动作页 E2E：打开动作页 → 点击 clip 格子 → 验证 pet-store.json 持久化 → 重启后保留
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\DesktopPet.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\DesktopPet.App.exe"
$storePath = Join-Path $env:APPDATA "DesktopPet\pet-store.json"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

function Find-ByName([System.Windows.Automation.AutomationElement]$rootEl, [string]$name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $rootEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Get-StoreJson {
    if (-not (Test-Path $storePath)) { return "{}" }
    return Get-Content $storePath -Raw
}

$backup = Get-StoreJson
$before = $backup | ConvertFrom-Json
$selectedBefore = $before.instances | Where-Object { $_.id -eq $before.selectedId } | Select-Object -First 1
$hadActions = $selectedBefore.actions -ne $null

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

$actionsBtn = Find-ByName $settingsWin "动作"
if ($null -eq $actionsBtn) { Write-Host "FAIL: 动作导航按钮未找到"; Stop-Process -Id $app.Id -Force; exit 1 }
$actionsBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 4

# 点击 idle 段第一个格子（动作格子 #0，多选）
$cell = Find-ByName $settingsWin "动作格子 #0"
if ($null -eq $cell) { Write-Host "FAIL: 动作格子 #0 未找到"; Stop-Process -Id $app.Id -Force; exit 1 }
# 官方 API：ScrollItemPattern.ScrollIntoView 把元素滚入容器可视区（未实现则忽略）
try {
    $scrollItem = $cell.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern)
    $scrollItem.ScrollIntoView()
    Start-Sleep -Milliseconds 500
} catch { }
# 官方 API：ButtonAutomationPeer 支持 InvokePattern（等价于鼠标点击 Button.Click）
$invoke = $cell.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
$invoke.Invoke()
Start-Sleep -Seconds 1

# 验证持久化：selectedId 实例的 actions 出现（动作页默认编辑选中宠物）
$after = Get-StoreJson | ConvertFrom-Json
$selected = $after.instances | Where-Object { $_.id -eq $after.selectedId } | Select-Object -First 1
$actions = $selected.actions
if ($null -eq $actions -or $null -eq $actions.idleClips) {
    Write-Host "FAIL: 点击格子后 actions 未持久化"; Stop-Process -Id $app.Id -Force; exit 1
}
Write-Host "PASS: 点击后 idleClips=[$($actions.idleClips -join ',')] mode=$($actions.idleMode) interval=$($actions.idleIntervalSeconds)"

# 重启验证保留
[System.Windows.Forms.SendKeys]::SendWait("^%q")
Start-Sleep -Seconds 3
if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force }
Start-Sleep -Seconds 1

$app2 = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 6
[System.Windows.Forms.SendKeys]::SendWait("^%s")
Start-Sleep -Seconds 2
$desktop2 = [System.Windows.Automation.AutomationElement]::RootElement
$settingsWin2 = $null
foreach ($w in $desktop2.FindAll([System.Windows.Automation.TreeScope]::Children,
    [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($w.Current.Name -eq "DesktopPet") { $settingsWin2 = $w; break }
}
if ($null -eq $settingsWin2) { Write-Host "FAIL: 重启后设置窗口未找到"; Stop-Process -Id $app2.Id -Force; exit 1 }
$actionsBtn2 = Find-ByName $settingsWin2 "动作"
if ($null -eq $actionsBtn2) { Write-Host "FAIL: 重启后动作导航未找到"; Stop-Process -Id $app2.Id -Force; exit 1 }
$actionsBtn2.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 4

$cell2 = Find-ByName $settingsWin2 "动作格子 #0"
if ($null -eq $cell2) { Write-Host "FAIL: 重启后动作格子 #0 未找到"; Stop-Process -Id $app2.Id -Force; exit 1 }
Write-Host "PASS: 重启后动作页正常渲染（格子存在）"

$persisted = Get-StoreJson | ConvertFrom-Json
$persistedSelected = $persisted.instances | Where-Object { $_.id -eq $persisted.selectedId } | Select-Object -First 1
$clips = $persistedSelected.actions.idleClips
if ($null -eq $clips -or $clips.Count -lt 1) {
    Write-Host "FAIL: 重启后 actions.idleClips 丢失"; Stop-Process -Id $app2.Id -Force; exit 1
}
Write-Host "PASS: 重启后 idleClips 保留 [$($clips -join ',')]"

# 恢复现场（还原测试前数据，避免污染用户数据）
Set-Content -Path $storePath -Value $backup -Encoding UTF8 -NoNewline

[System.Windows.Forms.SendKeys]::SendWait("^%q")
Start-Sleep -Seconds 2
if (-not $app2.HasExited) { Stop-Process -Id $app2.Id -Force }
Write-Host "DONE (store restored)"
