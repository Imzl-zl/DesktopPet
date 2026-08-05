$ErrorActionPreference = "Stop"
$root = "C:\sudy\github\DesktopPet\windows-native"
$exe = Join-Path $root "src\DesktopPet.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\DesktopPet.App.exe"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$app = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 6
[System.Windows.Forms.SendKeys]::SendWait("^%s")
Start-Sleep -Seconds 2
$desktop = [System.Windows.Automation.AutomationElement]::RootElement
$settingsWin = $null
foreach ($w in $desktop.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($w.Current.Name -eq "DesktopPet") { $settingsWin = $w; break }
}
foreach ($b in $settingsWin.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($b.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and $b.Current.Name -match "AI|ai|智能") {
        $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); break
    }
}
Start-Sleep -Seconds 2
$names = @()
foreach ($el in $settingsWin.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
    $t = $el.Current.ControlType
    if (($t -eq [System.Windows.Automation.ControlType]::CheckBox -or $t -eq [System.Windows.Automation.ControlType]::RadioButton -or $t -eq [System.Windows.Automation.ControlType]::Button) -and $el.Current.Name) {
        $names += $el.Current.Name
    }
}
Write-Host "=== AI 页控件清单 ==="
$names | ForEach-Object { Write-Host $_ }
[System.Windows.Forms.SendKeys]::SendWait("^%q")
Start-Sleep -Seconds 2
if (-not $app.HasExited) { Stop-Process -Id $app.Id -Force }
