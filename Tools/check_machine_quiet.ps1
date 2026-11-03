# check_machine_quiet.ps1 — 性能类证据的"空载"护栏
#
# 背景（2026-10-29，B-R9-08 验收实测）：
#   本机 `CompatTelRunner` 会吃掉 CPU，帧率从 ~430fps 掉到 310–360fps，
#   甚至跑出"关态比原管线快 14%"的荒谬值。跨跑比较帧率因此在负载下完全不可信。
#
# 用法：
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools/check_machine_quiet.ps1
#   powershell ... -File Tools/check_machine_quiet.ps1 -WarnOnly      # 只警告，不返回非 0
#   powershell ... -File Tools/check_machine_quiet.ps1 -SampleSeconds 5
#
# 判定：采样窗口内，非白名单进程占用 CPU 超过总量 `-ThresholdPercent`（默认 20%）→ 判"忙"。
# 白名单：测试自身相关（Unity / zrender_test / dotnet / powershell / conhost / explorer 等）。
# 输出：一张 top-N 表 + 一行 VERDICT。忙时 exit 1（-WarnOnly 时恒 0）。
#
# 注意：这是**证据质量护栏**，不是性能测试。它只回答"这台机器现在适合取帧率证据吗"。

param(
    [int]$SampleSeconds = 3,
    [double]$ThresholdPercent = 20.0,
    [int]$Top = 8,
    [switch]$WarnOnly
)

$ErrorActionPreference = 'Stop'

# 白名单：被测程序自身与工具链，不算"干扰"
$allow = @(
    'Unity', 'Unity Hub', 'UnityCrashHandler64', 'UnityShaderCompiler',
    'zrender_test', 'zrhi_d3d12_window', 'zfg_probe_selftest', 'zkernel_bench',
    'dotnet', 'MSBuild', 'VBCSCompiler', 'cmake', 'ninja', 'cl', 'link',
    'powershell', 'pwsh', 'conhost', 'WindowsTerminal', 'OpenConsole',
    'explorer', 'SearchHost', 'StartMenuExperienceHost', 'dwm', 'csrss', 'wininit',
    # 宿主/调度进程：跑测试时它们本来就在工作，算进来只会造成假 BUSY
    'WorkBuddy', 'WorkBuddyAI', 'sandbox-center', 'node', 'git', 'bash', 'sh',
    'System', 'Registry', 'Memory Compression', 'Idle', 'smss', 'lsass', 'services',
    'svchost', 'fontdrvhost', 'winlogon', 'taskhostw', 'ctfmon', 'sihost', 'RuntimeBroker'
)

Write-Host ("== machine quiet check (sample {0}s, threshold {1}%) ==" -f $SampleSeconds, $ThresholdPercent) -ForegroundColor Cyan

# 两次快照求差值，避免累计 CPU 时间被历史污染。
# 注意两个实测坑：
#   ① 哈希表在 `powershell -File` 下作用域会失效（$first 为 null）→ 改用快照数组；
#   ② 脚本启动后的**第一次** Get-Process 偶发返回空（实测 snap1=0、snap2=287）→ 加重试兜底。
function Get-CpuSnapshot {
    for ($i = 0; $i -lt 3; $i++) {
        $s = @(Get-Process -ErrorAction SilentlyContinue |
            Where-Object { $_.Id -ne $PID } |
            Select-Object Id, ProcessName, CPU)
        if ($s.Count -gt 0) { return $s }
        Start-Sleep -Milliseconds 200
    }
    return @()
}

$snap1 = Get-CpuSnapshot
Start-Sleep -Seconds $SampleSeconds
$snap2 = Get-CpuSnapshot

$totalDelta = 0.0
$rows = @()
foreach ($p in $snap2) {
    if ($null -eq $p.CPU) { continue }
    $prev = $snap1 | Where-Object { $_.Id -eq $p.Id } | Select-Object -First 1
    if ($null -eq $prev) { continue }                    # 新进程，无基线，跳过
    if ($null -eq $prev.CPU) { continue }
    $delta = [double]$p.CPU - [double]$prev.CPU
    if ($delta -le 0) { continue }
    $totalDelta += $delta
    $rows += [pscustomobject]@{
        Name    = $p.ProcessName
        Pid     = $p.Id
        CpuSec  = [math]::Round($delta, 2)
        Allowed = ($allow -contains $p.ProcessName)
    }
}

if ($totalDelta -le 0) {
    Write-Host 'no measurable CPU activity in window (idle or all counters unavailable)' -ForegroundColor Yellow
    Write-Output 'VERDICT: QUIET (no data)'
    exit 0
}

foreach ($r in $rows) {
    $r | Add-Member -NotePropertyName SharePct -NotePropertyValue ([math]::Round(100.0 * $r.CpuSec / $totalDelta, 1)) -Force
}

$topRows = $rows | Sort-Object CpuSec -Descending | Select-Object -First $Top
$topRows | Format-Table Name, Pid, CpuSec, SharePct, Allowed -AutoSize | Out-String -Width 120 | Write-Host
Write-Host ''

$noisy = @($rows | Where-Object { (-not $_.Allowed) -and ($_.SharePct -ge $ThresholdPercent) })
if ($noisy.Count -eq 0) {
    Write-Output 'VERDICT: QUIET — 适合取性能/帧率证据'
    exit 0
}

Write-Output ('VERDICT: BUSY — 以下进程占 CPU ≥{0}%：{1}' -f $ThresholdPercent, (($noisy | ForEach-Object { '{0}({1}%)' -f $_.Name, $_.SharePct }) -join ', '))
Write-Host '  含义：此期间的帧率/耗时数字不可用于跨跑比较（见 B-R9-08 验收：CompatTelRunner 曾把 430fps 压到 310fps）。'
Write-Host '  建议：等负载降下来再取证据，或改用"同条件强制对照"（同一次运行内切换 A/B）而不是跨跑比较。'
if ($WarnOnly) { exit 0 }
exit 1
