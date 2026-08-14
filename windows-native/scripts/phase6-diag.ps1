$ErrorActionPreference = "Stop"
$root = "C:\sudy\github\DesktopPet\windows-native"
$exe = Join-Path $root "src\DesktopPet.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\DesktopPet.App.exe"
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$app = Start-Process -FilePath $exe -PassThru
for ($i = 0; $i -lt 12; $i++) {
    Start-Sleep -Seconds 5
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $mine = @()
    foreach ($w in $desktop.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($w.Current.ProcessId -eq $app.Id -and $w.Current.Name) { $mine += $w.Current.Name }
    }
    $log = Get-Content (Join-Path $env:TEMP "desktoppet-ai.log") -Tail 2 -ErrorAction SilentlyContinue
    Write-Host "[$($i*5)s] 窗口: $($mine -join ' | ') | 日志: $($log -join ' / ')"
    if ($i -eq 5) {
        # 第 30s：手动发一条对话（如果对话窗在）
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.SendKeys]::SendWait("^#t")
        Write-Host "  (Win+Ctrl+T 已按)"
    }
}
Stop-Process -Id $app.Id -Force
Write-Host "DIAG DONE"
