# Phase 6 端到端验收：本地 mock 模型 → 记忆/亲密度/主动互动/每日总结/总结图全链路
# 前置：python scripts/mock-openai.py 已在 18080 运行；Release build 已存在
$ErrorActionPreference = "Stop"
$root = "C:\sudy\github\DesktopPet\windows-native"
$exe = Join-Path $root "src\DesktopPet.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\DesktopPet.App.exe"
$dataDir = Join-Path $env:APPDATA "DesktopPet"
$backupDir = Join-Path $env:TEMP "desktoppet-e2e-backup"
$log = Join-Path $env:TEMP "desktoppet-mock-requests.log"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

# 0) 备份 + 写入测试配置（干净起点）
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
foreach ($f in @("app-settings.json", "providers.json", "memory.json", "intimacy.json", "diary-meta.json")) {
    $src = Join-Path $dataDir $f
    if (Test-Path $src) { Copy-Item $src (Join-Path $backupDir $f) -Force }
}
if (Test-Path (Join-Path $dataDir "diary")) { Copy-Item (Join-Path $dataDir "diary") (Join-Path $backupDir "diary") -Recurse -Force }

$providers = @{
    models = @(@{ id = "mock"; name = "Mock"; baseUrl = "http://127.0.0.1:18080/v1"; apiKeyRef = ""; modelName = "mock-model"; capabilities = @("chat", "vision"); isDefault = $true })
    image  = @{ baseUrl = "http://127.0.0.1:18080/v1"; apiKeyRef = ""; modelName = "mock-image"; size = "1024x1024" }
} | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText((Join-Path $dataDir "providers.json"), $providers, [System.Text.UTF8Encoding]::new($false))

$settings = @{
    theme = "system"; bubbleOpacity = 92; fontSize = 12; fontFamily = "system"
    showIdleChatter = $true; bobAnimation = $false; petSizePercent = 100
    leftClickAction = "none"; quickBubbleDurationSeconds = 4
    quickBubblePresets = @("辛苦了~", "摸摸头")
    roam = @{ enabled = $true; mode = "wander"; speed = 5; pauseMinMs = 1200; pauseMaxMs = 3500 }
    lang = "zhHans"
    ai = @{
        enabled = $true; screenAnalysis = $false; outputMode = "chat"
        screenContextEnabled = $false; providerId = "mock"
        memoryEnabled = $true; activeInteraction = $true; interactionFrequency = "high"
        screenAwareness = $false; intimacyEnabled = $true; dailySummary = $true
        summaryImage = $true; ttsEnabled = $true; allReply = $false
    }
} | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText((Join-Path $dataDir "app-settings.json"), $settings, [System.Text.UTF8Encoding]::new($false))
Remove-Item (Join-Path $dataDir "memory.json") -ErrorAction SilentlyContinue
Remove-Item (Join-Path $dataDir "intimacy.json") -ErrorAction SilentlyContinue
Remove-Item (Join-Path $dataDir "diary-meta.json") -ErrorAction SilentlyContinue

Write-Host "== 配置就绪（AI 全开 / mock 端点 / 干净状态）=="

# 1) 启动 App
$app = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 6
Write-Host "App pid=$($app.Id) alive=$(-not $app.HasExited)"

# 2) 模式 = 配置的 chat（启动即生效；不再按 M 以免切走）

# 3) 等待主动互动/每日总结 tick（30s 周期；evening 窗口或总结补昨日）
Start-Sleep -Seconds 40

# 4) 打开对话窗并发送消息（ChatWindow 若已被互动打开则直接输入）
$desktop = [System.Windows.Automation.AutomationElement]::RootElement
$chatWin = $null
foreach ($w in $desktop.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($w.Current.Name -match "对话") { $chatWin = $w; break }
}
if ($null -eq $chatWin) {
    # 无对话窗：通过浮球打开太复杂，用模式路由触发（主动互动输出会自动开窗）——再等一个 tick
    Start-Sleep -Seconds 35
    foreach ($w in $desktop.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($w.Current.Name -match "对话") { $chatWin = $w; break }
    }
}
if ($null -eq $chatWin) {
    Write-Host "WARN: 对话窗未出现，全部窗口："
    foreach ($w in $desktop.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
        Write-Host "  - [$($w.Current.ProcessId)] $($w.Current.Name)"
    }
} else {
    Write-Host "PASS: 对话窗出现（主动互动输出）"
    # 聚焦输入框并输入（UIAutomation 找 Edit 控件）
    $edit = $chatWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Edit)))
    if ($null -ne $edit) {
        $edit.SetFocus()
        [System.Windows.Forms.SendKeys]::SendWait("叫我小美，今天加班到很晚")
        Start-Sleep -Milliseconds 500
        [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
        Write-Host "PASS: 对话消息已发送"
    }
}

# 5) 立即检查语音 wav（SAPI 合成约 11s 播放中，文件应存在）
Start-Sleep -Seconds 2
$liveTts = Get-ChildItem (Join-Path $env:TEMP 'desktoppet-tts-*.wav') -ErrorAction SilentlyContinue
Write-Host "  语音播放中检查: $(if ($liveTts.Count -gt 0) { 'PASS (' + $liveTts[0].Name + ')' } else { 'FAIL' })"

# 6) 等待对话处理 + 画像更新 + 总结生成
Start-Sleep -Seconds 15

# 6) 验证产物
$mem = Join-Path $dataDir "memory.json"
$int = Join-Path $dataDir "intimacy.json"
$diaryDir = Join-Path $dataDir "diary"
Write-Host ""
Write-Host "== 验证结果 =="
if (Test-Path $mem) {
    $m = Get-Content $mem -Raw | ConvertFrom-Json
    Write-Host "memory.json: 称呼=$($m.callName) 话题=$($m.topics -join ',') 摘要长度=$($m.summary.Length)"
    Write-Host "  记忆写入: $(if ($m.callName -eq '小美' -or $m.topics.Count -gt 0) { 'PASS' } else { 'FAIL' })"
} else { Write-Host "memory.json: 不存在 FAIL" }
if (Test-Path $int) {
    $i = Get-Content $int -Raw | ConvertFrom-Json
    Write-Host "intimacy.json: 值=$($i.value)（>0 = 对话已记账）"
    Write-Host "  亲密度增长: $(if ($i.value -gt 0) { 'PASS' } else { 'FAIL' })"
} else { Write-Host "intimacy.json: 不存在 FAIL" }
$txts = @()
if (Test-Path $diaryDir) { $txts = Get-ChildItem $diaryDir -Filter *.txt }
$pngs = @()
if (Test-Path $diaryDir) { $pngs = Get-ChildItem $diaryDir -Filter *.png }
Write-Host "  每日总结文本: $(if ($txts.Count -gt 0) { 'PASS (' + $txts[0].Name + ')' } else { 'FAIL' })"
Write-Host "  总结图: $(if ($pngs.Count -gt 0) { 'PASS (' + $pngs[0].Name + ')' } else { 'FAIL' })"
if (Test-Path $log) {
    $reqs = Get-Content $log
    $chatReqs = ($reqs | Where-Object { $_ -match "chat/completions" }).Count
    $imgReqs = ($reqs | Where-Object { $_ -match "images/generations" }).Count
    $interactionReqs = ($reqs | Where-Object { $_ -match "主动说|主动" }).Count
    Write-Host "  mock 请求: chat=$chatReqs image=$imgReqs（含主动互动=检查 system 含宠物名）"
    $sysHasPet = ($reqs | Where-Object { $_ -match "你是宠物" }).Count
    Write-Host "  多宠物人格注入: $(if ($sysHasPet -gt 0) { 'PASS (' + $sysHasPet + ' 条)' } else { 'N/A（单宠物或无触发）' })"
}

# 7) 退出并恢复备份
[System.Windows.Forms.SendKeys]::SendWait("^%q")
Start-Sleep -Seconds 3
if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force }
foreach ($f in @("app-settings.json", "providers.json", "memory.json", "intimacy.json", "diary-meta.json")) {
    $src = Join-Path $backupDir $f
    if (Test-Path $src) { Copy-Item $src (Join-Path $dataDir $f) -Force } else { Remove-Item (Join-Path $dataDir $f) -ErrorAction SilentlyContinue }
}
if (Test-Path (Join-Path $backupDir "diary")) { Remove-Item (Join-Path $dataDir "diary") -Recurse -Force; Copy-Item (Join-Path $backupDir "diary") (Join-Path $dataDir "diary") -Recurse -Force }
Write-Host "== 备份已恢复 =="
