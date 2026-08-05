# 真实模型对话验收：ChatWindow 发消息 → memory/intimacy 落盘
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$dataDir = Join-Path $env:APPDATA "DesktopPet"
$desktop = [System.Windows.Automation.AutomationElement]::RootElement

function Get-WindowByName([string]$pattern) {
    foreach ($w in $desktop.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($w.Current.Name -match $pattern) { return $w }
    }
    return $null
}

$chatWin = Get-WindowByName "对话"
if ($null -eq $chatWin) {
    Write-Host "FAIL: 对话窗未出现"
    exit 1
}
Write-Host "PASS: 对话窗出现"
$edit = $chatWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)))
if ($null -eq $edit) { Write-Host "FAIL: 无输入框"; exit 1 }
$edit.SetFocus()
[System.Windows.Forms.SendKeys]::SendWait("叫我小美，今天加班到很晚")
Start-Sleep -Milliseconds 500
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Write-Host "PASS: 消息已发送，等待真实模型回复（45s）"
Start-Sleep -Seconds 45

# 读取对话窗文本验证回复
$texts = $chatWin.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Text)))
$all = ($texts | ForEach-Object { $_.Current.Name } | Where-Object { $_ -ne "" }) -join " | "
Write-Host "对话内容: $($all.Substring(0, [Math]::Min(300, $all.Length)))"

$mem = Join-Path $dataDir "memory.json"
$int = Join-Path $dataDir "intimacy.json"
if (Test-Path $mem) {
    $m = Get-Content $mem -Raw | ConvertFrom-Json
    Write-Host "memory.json: 称呼=$($m.callName) 话题=$($m.topics -join ',') 摘要长度=$($m.summary.Length)"
} else { Write-Host "memory.json: 不存在" }
if (Test-Path $int) {
    $i = Get-Content $int -Raw | ConvertFrom-Json
    Write-Host "intimacy.json: 值=$($i.value)"
} else { Write-Host "intimacy.json: 不存在" }
