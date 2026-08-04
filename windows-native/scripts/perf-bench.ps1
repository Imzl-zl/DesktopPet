param(
    [string]$Configuration = "Release",
    [int]$IdleSeconds = 10,
    [int]$DragMs = 10000,
    [int]$DragIterations = 5,
    [switch]$SkipBuild
)

<#
.SYNOPSIS
# (ascii rewrite)

# (ascii rewrite)
  1. (optional) Release build
  2. Drag bench: App --bench-drag mode, SendInput drag sequence, GetMessageTime sampling, assert <16ms
  3. Idle bench: App --bench-idle mode, PerformanceCounter CPU/mem sampling, assert <1% / <120MB
  4. Report; non-zero exit on assertion failure
#>

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$appDir = Join-Path $root "src\DesktopPet.App"
# Phase 5: TFM now net8.0-windows10.0.19041.0 + win-x64 (WinAppSDK self-contained needs Platform=x64)
$exe = Join-Path $appDir "bin\$Configuration\net8.0-windows10.0.19041.0\win-x64\DesktopPet.App.exe"
$tmp = [System.IO.Path]::GetTempPath()
$dragReady = Join-Path $tmp "desktoppet-bench-drag.ready"
$dragJson = Join-Path $tmp "desktoppet-bench-drag.json"
$idleReady = Join-Path $tmp "desktoppet-bench-idle.ready"

# ---- user32 input simulation (bench-input.cs, separate file to avoid here-string issues) ----
Add-Type -Path (Join-Path $PSScriptRoot "bench-input.cs")

function Wait-ForFile([string]$Path, [int]$TimeoutSeconds = 30) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path $Path)) {
        if ($sw.Elapsed.TotalSeconds -gt $TimeoutSeconds) {
            throw "timed out waiting for $Path"
        }
        Start-Sleep -Milliseconds 100
    }
}

function Remove-BenchArtifacts {
    foreach ($p in @($dragReady, $dragJson, $idleReady)) {
        if (Test-Path $p) { Remove-Item $p -Force }
    }
}

$results = [ordered]@{}
$failed = @()

# ---- Drag bench ----
function Invoke-DragBench {
    Write-Host "`n== Drag latency bench (SendInput, $DragIterations iterations) ==" -ForegroundColor Cyan
    Remove-BenchArtifacts
    $proc = Start-Process -FilePath $exe -ArgumentList "--bench-drag=$DragMs" -PassThru
    try {
        Wait-ForFile $dragReady
        $pos = Get-Content $dragReady -Raw | ConvertFrom-Json
# (ascii rewrite)
        $startX = [int]$pos.centerX
        $startY = [int]$pos.centerY

        for ($i = 0; $i -lt $DragIterations; $i++) {
            [BenchInput]::Move($startX, $startY)
            Start-Sleep -Milliseconds 30
            [BenchInput]::Down()
            [BenchInput]::DragMoves($startX, $startY, 24, 4, 3, 8)   # ~125Hz sampling
            [BenchInput]::Up()
            Start-Sleep -Milliseconds 120
            Write-Host "  iteration $($i + 1) done"
        }

        Wait-Process -Id $proc.Id -Timeout 30 -ErrorAction SilentlyContinue
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force; throw "bench app did not exit" }
        if (-not (Test-Path $dragJson)) { throw "drag result file missing" }

        $r = Get-Content $dragJson -Raw | ConvertFrom-Json
        $results["drag"] = "procAvg=$([math]::Round($r.processingAvgMs, 2))ms procMax=$([math]::Round($r.processingMaxMs, 2))ms e2eAvg=$([math]::Round($r.endToEndAvgMs, 2))ms e2eP95=$([math]::Round($r.endToEndP95Ms, 2))ms e2eMax=$([math]::Round($r.endToEndMaxMs, 2))ms samples=$($r.sampleCount)"
        Write-Host "  result: processing avg=$([math]::Round($r.processingAvgMs, 2))ms max=$([math]::Round($r.processingMaxMs, 2))ms | end-to-end avg=$([math]::Round($r.endToEndAvgMs, 2))ms p95=$([math]::Round($r.endToEndP95Ms, 2))ms max=$([math]::Round($r.endToEndMaxMs, 2))ms samples=$($r.sampleCount)" -ForegroundColor Yellow
# (ascii rewrite)
        if ($r.processingMaxMs -ge 16 -or $r.endToEndAvgMs -ge 16) {
            $script:failed += "drag latency: processingMax=$([math]::Round($r.processingMaxMs, 2))ms e2eAvg=$([math]::Round($r.endToEndAvgMs, 2))ms"
        }
    } finally {
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    }
}

# ---- Idle bench ----
function Invoke-IdleBench {
    Write-Host "`n== Idle bench (${IdleSeconds}s, animation loop stopped) ==" -ForegroundColor Cyan
    Remove-BenchArtifacts
    $proc = Start-Process -FilePath $exe -ArgumentList "--bench-idle=$($IdleSeconds * 1000)" -PassThru
    try {
        Wait-ForFile $idleReady
# (ascii rewrite)

        $cores = [System.Environment]::ProcessorCount
        $samples = @()
        for ($i = 0; $i -lt 3; $i++) {
            $t0 = $proc.TotalProcessorTime
            $wall0 = [System.Diagnostics.Stopwatch]::GetTimestamp()
            Start-Sleep -Seconds 2
            if ($proc.HasExited) { throw "bench app exited during idle sampling (IdleSeconds too short)" }
            $proc.Refresh()
            $t1 = $proc.TotalProcessorTime
            $wall1 = [System.Diagnostics.Stopwatch]::GetTimestamp()
            $wallSec = ($wall1 - $wall0) / [System.Diagnostics.Stopwatch]::Frequency
            $cpuPct = ($t1 - $t0).TotalSeconds / $wallSec / $cores * 100
            $memMb = [math]::Round($proc.WorkingSet64 / 1MB, 1)
            $privateMb = [math]::Round($proc.PrivateMemorySize64 / 1MB, 1)
            $samples += [pscustomobject]@{ CpuPct = $cpuPct; MemMb = $memMb; PrivateMb = $privateMb }
            Write-Host ("  sample {0}: cpu={1:N3}% ws={2}MB private={3}MB" -f ($i + 1), $cpuPct, $memMb, $privateMb)
        }

        Wait-Process -Id $proc.Id -Timeout 30 -ErrorAction SilentlyContinue
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force; throw "bench app did not exit" }

        $steady = $samples[-1]
        # baseline: WebView2 ~100MB resident; measure PrivateMemorySize64
        $results["idle"] = "cpu=$([math]::Round($steady.CpuPct, 3))% ws=$($steady.MemMb)MB private=$($steady.PrivateMb)MB"
        if ($steady.CpuPct -ge 1.0) { $script:failed += "idle CPU >= 1% ($([math]::Round($steady.CpuPct, 3))%)" }
        if ($steady.PrivateMb -ge 120) { $script:failed += "idle private memory >= 120MB ($($steady.PrivateMb)MB)" }
    } finally {
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    }
}

# ---- main ----
if (-not $SkipBuild) {
    Write-Host "Building Release..." -ForegroundColor Cyan
    & dotnet build (Join-Path $root "DesktopPet.sln") -c $Configuration --nologo -v q -p:Platform=x64 -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}

if (-not (Test-Path $exe)) { throw "app exe not found: $exe" }

Invoke-DragBench
Invoke-IdleBench

Write-Host "`n========== PERF BENCH REPORT ==========" -ForegroundColor Green
foreach ($k in $results.Keys) { Write-Host ("{0,-6} {1}" -f $k, $results[$k]) }
if ($failed.Count -gt 0) {
    Write-Host "`nFAILED:" -ForegroundColor Red
    foreach ($f in $failed) { Write-Host "  - $f" -ForegroundColor Red }
    exit 1
}
Write-Host "`nALL CHECKS PASSED (drag <16ms, idle CPU <1%, mem <120MB)" -ForegroundColor Green
exit 0
