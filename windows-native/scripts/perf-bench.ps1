param(
    [string]$Configuration = "Release",
    [int]$IdleSeconds = 10,
    [int]$DragMs = 10000,
    [int]$DragIterations = 5,
    [switch]$SkipBuild
)

<#
.SYNOPSIS
Phase 0 性能基线（迁移计划 §7 指标）：拖拽延迟 <16ms、空闲 CPU <1%、内存 <120MB。

流程：
  1. （可选）Release 构建
  2. Drag 基准：启动 App --bench-drag 模式 → SendInput 真实拖拽序列 →
     读取 App 内 GetMessageTime 差值采样（pet 窗口 MoveWindow 延迟）→ 断言 <16ms
  3. Idle 基准：启动 App --bench-idle 模式（静止：渲染循环停止）→ PerformanceCounter
     采样 CPU/内存 → 断言 CPU <1%、内存 <120MB
  4. 输出报告，断言失败返回非零退出码
#>

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$appDir = Join-Path $root "src\DesktopPet.App"
$exe = Join-Path $appDir "bin\$Configuration\net8.0-windows\DesktopPet.App.exe"
$tmp = [System.IO.Path]::GetTempPath()
$dragReady = Join-Path $tmp "desktoppet-bench-drag.ready"
$dragJson = Join-Path $tmp "desktoppet-bench-drag.json"
$idleReady = Join-Path $tmp "desktoppet-bench-idle.ready"

# ---- user32 输入模拟（bench-input.cs，独立文件避免 here-string 解析问题） ----
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

# ---- Drag 基准 ----
function Invoke-DragBench {
    Write-Host "`n== Drag latency bench (SendInput, $DragIterations iterations) ==" -ForegroundColor Cyan
    Remove-BenchArtifacts
    $proc = Start-Process -FilePath $exe -ArgumentList "--bench-drag=$DragMs" -PassThru
    try {
        Wait-ForFile $dragReady
        $pos = Get-Content $dragReady -Raw | ConvertFrom-Json
        # 精灵中心（App 按 DPI 换算输出的物理坐标）
        $startX = [int]$pos.centerX
        $startY = [int]$pos.centerY

        for ($i = 0; $i -lt $DragIterations; $i++) {
            [BenchInput]::Move($startX, $startY)
            Start-Sleep -Milliseconds 30
            [BenchInput]::Down()
            [BenchInput]::DragMoves($startX, $startY, 24, 4, 3, 8)   # ~125Hz 鼠标节奏
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
        # 验收（迁移计划 §7）：跟手 = 端到端平均 <16ms；处理成本 max <16ms
        if ($r.processingMaxMs -ge 16 -or $r.endToEndAvgMs -ge 16) {
            $script:failed += "drag latency: processingMax=$([math]::Round($r.processingMaxMs, 2))ms e2eAvg=$([math]::Round($r.endToEndAvgMs, 2))ms"
        }
    } finally {
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    }
}

# ---- Idle 基准 ----
function Invoke-IdleBench {
    Write-Host "`n== Idle bench (${IdleSeconds}s, animation loop stopped) ==" -ForegroundColor Cyan
    Remove-BenchArtifacts
    $proc = Start-Process -FilePath $exe -ArgumentList "--bench-idle=$($IdleSeconds * 1000)" -PassThru
    try {
        Wait-ForFile $idleReady
        Start-Sleep -Seconds 3   # 跳过启动/加载瞬态

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
        # 对比口径：WebView2 的 ~100MB 常驻是私有内存，因此断言用 PrivateMemorySize64
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
    & dotnet build (Join-Path $root "DesktopPet.sln") -c $Configuration --nologo -v q
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
