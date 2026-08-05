# Phase 6 真实模型端到端验收：MyOVO sensenova（对话/视觉）+ agnes（生图）
# 前置：providers.json / app-settings.json 已配置真实端点（apiKey 在 Windows Credential Manager）；
#       Release build 已存在。
# 验收链路：主动互动触发 → ChatWindow 对话 → memory/intimacy 落盘 → 每日总结 txt+真实生图 png
$ErrorActionPreference = "Stop"
$root = "C:\sudy\github\DesktopPet\windows-native"
$exe = Join-Path $root "src\DesktopPet.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\DesktopPet.App.exe"
$dataDir = Join-Path $env:APPDATA "DesktopPet"
$backupDir = Join-Path $env:TEMP "desktoppet-real-e2e-backup"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

function Get-WindowByName([string]$pattern) {
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    foreach ($w in $desktop.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($w.Current.Name -match $pattern) { return $w }
    }
    return $null
}

# 0) 备份 + 干净起点（不动 providers/app-settings）
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
foreach ($f in @("memory.json", "intimacy.json", "diary-meta.json")) {
    $src = Join-Path $dataDir $f
    if (Test-Path $src) { Copy-Item $src (Join-Path $backupDir $f) -Force }
}
if (Test-Path (Join-Path $dataDir "diary")) { Copy-Item (Join-Path $dataDir "diary") (Join-Path $backupDir "diary") -Recurse -Force }
Remove-Item (Join-Path $dataDir "memory.json") -ErrorAction SilentlyContinue
Remove-Item (Join-Path $dataDir "intimacy.json") -ErrorAction SilentlyContinue
Remove-Item (Join-Path $dataDir "diary-meta.json") -ErrorAction SilentlyContinue
Remove-Item (Join-Path $dataDir "diary") -Recurse -ErrorAction SilentlyContinue
Write-Host "== 状态已清空（配置保留真实端点）=="

# 1) 启动 App
$app = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 8
Write-Host "App pid=$($app.Id) alive=$(-not $app.HasExited)"

# 2) 等待主动互动 tick（30s 周期；chat 模式会开 ChatWindow）
Write-Host "== 等待主动互动 tick（40s）=="
Start-Sleep -Seconds 40

# 3) 找对话窗（互动可能已开；没有则再等一个 tick）
$chatWin = Get-WindowByName "对话"
if ($null -eq $chatWin) {
    Write-Host "  互动未开窗，再等 35s…"
    Start-Sleep -Seconds 35
    $chatWin = Get-WindowByName "对话"
}
if ($null -eq $chatWin) {
    Write-Host "WARN: 对话窗未出现，全部窗口："
    foreach ($w in ([System.Windows.Automation.AutomationElement]::RootElement).FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
        Write-Host "  - [$($w.Current.ProcessId)] $($w.Current.Name)"
    }
} else {
    Write-Host "PASS: 对话窗出现"
    $edit = $chatWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Edit)))
    if ($null -ne $edit) {
        $edit.SetFocus()
        [System.Windows.Forms.SendKeys]::SendWait("叫我小美，今天加班到很晚")
        Start-Sleep -Milliseconds 500
        [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
        Write-Host "PASS: 对话消息已发送（真实模型）"
    }
}

# 4) 等真实 API：对话 5-30s + 总结生成 + 生图 10-60s
Write-Host "== 等待真实 API 链路（60s）=="
Start-Sleep -Seconds 60

# 5) 验证产物
Write-Host ""
Write-Host "== 验证结果 =="
$mem = Join-Path $dataDir "memory.json"
$int = Join-Path $dataDir "intimacy.json"
$diaryDir = Join-Path $dataDir "diary"
if (Test-Path $mem) {
    $m = Get-Content $mem -Raw | ConvertFrom-Json
    Write-Host "memory.json: 称呼=$($m.callName) 话题=$($m.topics -join ',') 摘要长度=$($m.summary.Length)"
    Write-Host "  记忆写入: $(if ($m.callName -eq '小美' -or $m.topics.Count -gt 0) { 'PASS' } else { 'FAIL' })"
} else { Write-Host "memory.json: 不存在 FAIL" }
if (Test-Path $int) {
    $i = Get-Content $int -Raw | ConvertFrom-Json
    Write-Host "intimacy.json: 值=$($i.value)"
    Write-Host "  亲密度增长: $(if ($i.value -gt 0) { 'PASS' } else { 'FAIL' })"
} else { Write-Host "intimacy.json: 不存在 FAIL" }
$txts = @(); $pngs = @()
if (Test-Path $diaryDir) { $txts = Get-ChildItem $diaryDir -Filter *.txt; $pngs = Get-ChildItem $diaryDir -Filter *.png }
Write-Host "  每日总结文本: $(if ($txts.Count -gt 0) { 'PASS (' + $txts[0].Name + ', ' + [math]::Round($txts[0].Length/1024,1) + 'KB)' } else { 'FAIL' })"
Write-Host "  总结图(真实生图): $(if ($pngs.Count -gt 0) { 'PASS (' + $pngs[0].Name + ', ' + [math]::Round($pngs[0].Length/1024,1) + 'KB)' } else { 'FAIL' })"
if ($txts.Count -gt 0) { Write-Host "--- 日记预览 ---"; Get-Content $txts[0].FullName -TotalCount 6 }

# 6) 退出并恢复备份
[System.Windows.Forms.SendKeys]::SendWait("^%q")
Start-Sleep -Seconds 3
if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force }
foreach ($f in @("memory.json", "intimacy.json", "diary-meta.json")) {
    $src = Join-Path $backupDir $f
    if (Test-Path $src) { Copy-Item $src (Join-Path $dataDir $f) -Force } else { Remove-Item (Join-Path $dataDir $f) -ErrorAction SilentlyContinue }
}
if (Test-Path (Join-Path $backupDir "diary")) { Remove-Item (Join-Path $dataDir "diary") -Recurse -Force; Copy-Item (Join-Path $backupDir "diary") (Join-Path $dataDir "diary") -Recurse -Force }
Write-Host "== 备份已恢复 =="
