# Verify 2025-06 settings fixes: B1 font radios / B2 all-reply toggle / D2 slider commit-on-release
# Real app smoke via UI Automation + mouse drag simulation. English output (PS 5.1 safe).
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\DesktopPet.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\DesktopPet.App.exe"
$settingsJson = Join-Path $env:APPDATA "DesktopPet\app-settings.json"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class MouseSim {
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    public const uint LEFTDOWN = 0x02; public const uint LEFTUP = 0x04;
}
"@

function Find-ByName($rootEl, $name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $rootEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-CheckBoxByName($rootEl, $name) {
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $name)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::CheckBox)))
    return $rootEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Get-JsonField($path, $field) {
    if (-not (Test-Path $path)) { return $null }
    $json = Get-Content $path -Raw | ConvertFrom-Json
    return $json.$field
}

# scroll the page's ScrollViewer until $el has a clickable point (it may be below the viewport)
function Get-ClickablePointOrScroll($win, $el) {
    $panes = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Pane)))
    $scroll = $null
    for ($i = 0; $i -lt $panes.Count; $i++) {
        $p = $panes.Item($i)
        if (-not $p.Current.IsScrollPatternAvailable) { continue }
        $sp = $p.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
        if ($sp.Current.VerticallyScrollable) { $scroll = $sp; break }
    }
    for ($i = 0; $i -lt 12; $i++) {
        try { return $el.GetClickablePoint() } catch { }
        if ($null -ne $scroll) {
            $scroll.Scroll([System.Windows.Automation.ScrollAmount]::LargeIncrement,
                [System.Windows.Automation.ScrollAmount]::NoAmount)
            Start-Sleep -Milliseconds 400
        }
    }
    throw "no clickable point after scrolling"
}

function Fail($msg) {
    Write-Host "FAIL: $msg"
    Stop-Process -Name "DesktopPet.App" -Force -ErrorAction SilentlyContinue
    exit 1
}

$beforeMtime = if (Test-Path $settingsJson) { (Get-Item $settingsJson).LastWriteTimeUtc } else { [DateTime]::MinValue }

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
if ($null -eq $settingsWin) { Fail "settings window not found" }
Write-Host "PASS: settings window open"

# ---- D2: roam speed slider — drag must NOT save while dragging, MUST save on release ----
$roamNav = Find-ByName $settingsWin "漫游"
if ($null -eq $roamNav) { Fail "roam nav not found" }
$roamNav.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 2

$sliders = $settingsWin.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Slider)))
if ($sliders.Count -lt 2) { Fail "expected 2 roam sliders, got $($sliders.Count)" }
$speedSlider = $sliders.Item(0)
$speedBefore = $speedSlider.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value
# press on the Thumb itself (clicking the track only does PageUp/Down, no DragCompleted)
$thumb = $speedSlider.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Thumb)))
if ($null -eq $thumb) { Fail "speed slider thumb not found" }
$mtimeBeforeDrag = (Get-Item $settingsJson).LastWriteTimeUtc

$pt = $thumb.GetClickablePoint()
$cursor = [System.Windows.Forms.Cursor]::Position
[System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]$pt.X, [int]$pt.Y)
Start-Sleep -Milliseconds 300
[MouseSim]::mouse_event([MouseSim]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 300
# drag by ~120px (direction depends on current value so there is room to move)
$dir = if ($speedBefore -ge 9) { -1 } else { 1 }
for ($i = 0; $i -lt 6; $i++) {
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point([int]($pt.X + 20 * ($i + 1) * $dir), [int]$pt.Y)
    Start-Sleep -Milliseconds 150
}
Start-Sleep -Milliseconds 400
$mtimeDuringDrag = (Get-Item $settingsJson).LastWriteTimeUtc
if ($mtimeDuringDrag -gt $mtimeBeforeDrag) {
    [MouseSim]::mouse_event([MouseSim]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
    Fail "settings.json was written DURING slider drag (commit-on-release broken)"
}
Write-Host "PASS: no save during drag (mtime unchanged)"

[MouseSim]::mouse_event([MouseSim]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Seconds 1
$speedDuring = $speedSlider.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern).Current.Value
if ($speedDuring -eq $speedBefore) { Fail "slider value did not change during drag (before=$speedBefore after=$speedDuring)" }
$mtimeAfterRelease = (Get-Item $settingsJson).LastWriteTimeUtc
if ($mtimeAfterRelease -le $mtimeBeforeDrag) { Fail "settings.json not written after drag release" }
$speedAfter = (Get-JsonField $settingsJson "roam").speed
if ($speedAfter -eq $speedBefore) { Fail "roam speed not persisted after release (before=$speedBefore after=$speedAfter)" }
Write-Host "PASS: save on release, roam.speed=$speedBefore -> $speedAfter"
[System.Windows.Forms.Cursor]::Position = $cursor

# ---- B1: bubble page font family radios ----
$bubbleNav = Find-ByName $settingsWin "气泡"
if ($null -eq $bubbleNav) { Fail "bubble nav not found" }
$bubbleNav.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 2

foreach ($label in @("系统默认", "圆体", "等宽")) {
    $radio = Find-ByName $settingsWin $label
    if ($null -eq $radio) { Fail "font radio '$label' not found" }
    Write-Host "PASS: font radio '$label' exists"
}
$mono = Find-ByName $settingsWin "等宽"
$mono.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Seconds 2
$ff = Get-JsonField $settingsJson "fontFamily"
if ($ff -ne "mono") { Fail "fontFamily=$ff expected mono" }
Write-Host "PASS: fontFamily persisted as mono"
$sys = Find-ByName $settingsWin "系统默认"
$sys.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Seconds 1

# ---- B2: AI page all-reply toggle ----
$aiNav = Find-ByName $settingsWin "AI 助手"
if ($null -eq $aiNav) { Fail "ai nav not found" }
$aiNav.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 2
$allReplyToggle = Find-CheckBoxByName $settingsWin "全员回应"
if ($null -eq $allReplyToggle) { Fail "all-reply toggle not found" }
Write-Host "PASS: all-reply toggle exists"
# keyboard space = real Click (UIA TogglePattern only flips IsChecked without raising Click - WPF OnToggle());
# SetFocus makes the ScrollViewer bring the element into view
$allReplyToggle.SetFocus()
Start-Sleep -Milliseconds 800
[System.Windows.Forms.SendKeys]::SendWait(" ")
Start-Sleep -Seconds 2
$ar = (Get-JsonField $settingsJson "ai").allReply
if ($ar -ne $true) { Fail "allReply=$ar expected true" }
Write-Host "PASS: allReply persisted as true"
# restore original state via keyboard space
$allReplyToggle.SetFocus()
Start-Sleep -Milliseconds 800
[System.Windows.Forms.SendKeys]::SendWait(" ")
Start-Sleep -Seconds 1

Write-Host "ALL CHECKS PASSED"
Stop-Process -Id $app.Id -Force
