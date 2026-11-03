# =============================================================================
# bench_regression.ps1 - automated benchmark regression vs committed baseline
# (TASK-B-R9-10)
#
# Pipeline: collect BENCH_JSON single-line programmatic output from the three
# benchmark programs (each program runs -RunsPerProgram times, default 3; the
# per-metric value is the MEDIAN across runs) -> compare each gated metric
# against Tools/bench_baseline.json within a generous tolerance -> table.
#
# Modes:
#   default         report-only: collect + compare + table, exit 0 (never
#                   fails the suite on tolerance; crashes are reported as
#                   ERROR rows in the table)
#   -Gate           enforcement (also enabled by env BENCH_GATE=1):
#                   exit 1 on any RED (beyond tolerance) or MISS (gated
#                   metric could not be collected - "cannot prove no
#                   regression" counts as a regression)
#   -UpdateBaseline collect and rewrite Tools/bench_baseline.json
#                   (machine marker + date; refused when the machine is busy)
#   -SelfTest       inline unit tests of the pure functions (JSON line
#                   parsing, fallback frame_ms parsing, tolerance judgment,
#                   baseline comparison); exit 0/1. Selected over Pester:
#                   zero new dependencies, same file, runnable on any box.
#
# Machine-quiet guard: check_machine_quiet.ps1 runs first (unless
# -SkipQuietCheck). BUSY => SKIP: load-contaminated numbers are worthless in
# both directions, so nothing is collected and nothing is judged. In gate
# mode SKIP is exit 0 with a loud note (a false red on a noisy box is worse
# than a skipped check; CI boxes are quiet).
#
# Tolerances (brief: generous, timing-jitter sensitive):
#   throughput (higher is better): RED when current < baseline * (1 - 0.20)
#   time       (lower is better):  RED when current > baseline * (1 + 0.30)
#   exactly -20% / +30% still passes; RED is strictly beyond the band.
#
# Metric sources (programmatic output first - all three programs grew a
# --json single-line output in this task; the human-readable frame_ms line of
# --lights is a documented fallback parsed by Get-FallbackFrameMs):
#   zkernel_bench.exe     --json                    (kernel metrics)
#   zrender_bench.exe     --json                    (draw list metrics)
#   zrhi_d3d12_window.exe --headless --lights --json (CPU wall per frame,
#                           offscreen: no vsync pinning of the measurement)
#   zrhi_d3d12_window.exe --backend vulkan --headless --lights --json
#                           (VK offscreen frame time - TASK-B-R9-11 gate
#                           metric #7; JSON program label
#                           'zrhi_d3d12_window_vk', keys _vk-suffixed)
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools/bench_regression.ps1
#   powershell ... -File Tools/bench_regression.ps1 -Gate
#   powershell ... -File Tools/bench_regression.ps1 -UpdateBaseline
#   powershell ... -File Tools/bench_regression.ps1 -SelfTest
#   powershell ... -File Tools/bench_regression.ps1 -SkipQuietCheck
# =============================================================================
param(
    [switch]$Gate,
    [switch]$UpdateBaseline,
    [switch]$SelfTest,
    [switch]$SkipQuietCheck,
    [string]$ExeDir = '',
    [int]$RunsPerProgram = 3
)

$ErrorActionPreference = 'Stop'

$RepoRoot     = Split-Path -Parent $PSScriptRoot
$BaselinePath = Join-Path $PSScriptRoot 'bench_baseline.json'
$QuietScript  = Join-Path $PSScriptRoot 'check_machine_quiet.ps1'
$LogDir       = Join-Path $RepoRoot 'TempLogs'
if (-not $ExeDir) { $ExeDir = Join-Path $RepoRoot 'Native\build\bin\Release' }

# The gated metric set: name = JSON key emitted by the program (unique across
# programs). Kind drives the tolerance band; optional TolThroughput/TolTime
# override the class defaults for a single metric (used where the measured
# run-to-run spread of the metric is wider than the class default - see the
# TASK-B-R9-10 construction report for the variance evidence). Extend here +
# re-run -UpdateBaseline to gate more metrics; the BENCH_JSON lines already
# carry the full metric set of each program.
$Script:MetricDefs = [ordered]@{
    # 2026-10-31 7-run spread 18.3M-30.8M rows/s (1.69x, bimodal CPU frequency
    # regimes on this box) - the class -20% band would false-red; measured -35%.
    'migrate_rows_per_sec' = @{ Program = 'zkernel_bench';         Kind = 'throughput'; TolThroughput = 0.35 }
    'compact_ms'           = @{ Program = 'zkernel_bench';         Kind = 'time' }
    'sort_ms_a'            = @{ Program = 'zrender_bench';         Kind = 'time' }
    'sort_ms_b'            = @{ Program = 'zrender_bench';         Kind = 'time' }
    'saved_pct_b'          = @{ Program = 'zrender_bench';         Kind = 'throughput' }
    'frame_ms_avg'         = @{ Program = 'zrhi_d3d12_window';     Kind = 'time' }
    # TASK-B-R9-11: VK offscreen lights frame time (gate metric #7). Same
    # collection profile as frame_ms_avg (offscreen, no vsync pinning; WaitIdle
    # per frame). The run emits BENCH_JSON with program label
    # 'zrhi_d3d12_window_vk' and _vk-suffixed keys so the D3D12 and VK lines
    # pass through one parser without colliding on frame_ms_avg.
    'frame_ms_avg_vk'      = @{ Program = 'zrhi_d3d12_window_vk';  Kind = 'time' }
}
$ProgramArgs = @{
    'zkernel_bench'         = @('--json')
    'zrender_bench'         = @('--json')
    'zrhi_d3d12_window'     = @('--headless', '--lights', '--json')
    'zrhi_d3d12_window_vk'  = @('--backend', 'vulkan', '--headless', '--lights', '--json')
}
# Pseudo-program -> real exe name (same binary, VK backend selection via args).
$ProgramExe = @{
    'zrhi_d3d12_window'    = 'zrhi_d3d12_window'
    'zrhi_d3d12_window_vk' = 'zrhi_d3d12_window'
}

# ---------------------------------------------------------------- pure fns ---
# Extract and parse `BENCH_JSON {...}` lines. Returns an array of PSCustomObject.
# Unparseable JSON after the prefix is skipped (never throws into the caller).
function Get-BenchJsonObjects {
    param([string]$Text, [string]$Program = '')
    $objs = @()
    if (-not $Text) { return ,$objs }
    foreach ($line in ($Text -split "`r?`n")) {
        # positive -match (NOT -notmatch: only -match populates $Matches)
        if ($line -match '^\s*BENCH_JSON\s+(\{.*\})\s*$') {
            try {
                $o = $Matches[1] | ConvertFrom-Json
            } catch { continue }
            if ($null -eq $o) { continue }
            if ($Program -and [string]$o.program -ne $Program) { continue }
            $objs += $o
        }
    }
    return ,$objs
}

# Documented fallback for the human-readable frame_ms line (no --json needed):
#   zrhi_d3d12_window: [LIGHTS] frame_ms avg=1.234 min=1.000 max=2.000 n=59 (frame1 excluded)
# Returns a PSCustomObject with frame_ms_avg / frame_ms_min / frame_ms_max /
# frames, or $null when the line is absent.
function Get-FallbackFrameMs {
    param([string]$Text)
    if (-not $Text) { return $null }
    foreach ($line in ($Text -split "`r?`n")) {
        if ($line -match 'frame_ms avg=([0-9.]+) min=([0-9.]+) max=([0-9.]+) n=(\d+)') {
            return [pscustomobject]@{
                frame_ms_avg = [double]$Matches[1]
                frame_ms_min = [double]$Matches[2]
                frame_ms_max = [double]$Matches[3]
                frames       = [int]$Matches[4]
            }
        }
    }
    return $null
}

# Median of a numeric array (pure). Even counts: lower middle element.
function Get-Median {
    param([double[]]$Values)
    if ($Values.Count -eq 0) { throw 'Get-Median: empty input' }
    $sorted = $Values | Sort-Object
    return [double]$sorted[[int][math]::Floor(($sorted.Count - 1) / 2)]
}

# Tolerance judgment (pure). $true = within band (PASS).
# Unknown kind throws - a typo in MetricDefs must never silently pass a gate.
function Test-WithinTolerance {
    param(
        [double]$Baseline,
        [double]$Current,
        [string]$Kind,
        [double]$ThroughputTol = 0.20,
        [double]$TimeTol = 0.30
    )
    if ($Kind -eq 'throughput') { return $Current -ge $Baseline * (1.0 - $ThroughputTol) }
    if ($Kind -eq 'time')       { return $Current -le $Baseline * (1.0 + $TimeTol) }
    throw ("unknown tolerance kind: '{0}' (expected throughput|time)" -f $Kind)
}

# Baseline comparison (pure). Returns rows for every gated metric:
#   OK (within band) / RED (beyond band) / MISS (no current value) / NEW (no baseline)
function Compare-Metrics {
    param(
        [hashtable]$Current,    # name -> double
        [hashtable]$Baseline,   # name -> double
        [hashtable]$KindByName  # name -> 'throughput'|'time'
    )
    $rows = @()
    foreach ($name in $Script:MetricDefs.Keys) {
        $kind = $KindByName[$name]
        $def = $Script:MetricDefs[$name]
        $tpTol = 0.20; if ($def.ContainsKey('TolThroughput')) { $tpTol = [double]$def['TolThroughput'] }
        $tTol  = 0.30; if ($def.ContainsKey('TolTime'))       { $tTol  = [double]$def['TolTime'] }
        $hasBase = $Baseline.ContainsKey($name)
        $hasCur  = $Current.ContainsKey($name)
        $base = 0.0; if ($hasBase) { $base = [double]$Baseline[$name] }
        $cur  = 0.0; if ($hasCur)  { $cur  = [double]$Current[$name] }
        $verdict = 'OK'
        if (-not $hasCur) { $verdict = 'MISS' }
        elseif (-not $hasBase) { $verdict = 'NEW' }
        elseif (-not (Test-WithinTolerance -Baseline $base -Current $cur -Kind $kind `
                    -ThroughputTol $tpTol -TimeTol $tTol)) {
            $verdict = 'RED'
        }
        $delta = $null
        if ($hasBase -and $hasCur -and [math]::Abs($base) -gt 0.0) {
            $delta = 100.0 * ($cur - $base) / [math]::Abs($base)
        }
        $rows += [pscustomobject]@{
            Name     = $name
            Kind     = $kind
            Baseline = $base
            Current  = $cur
            DeltaPct = $delta
            Verdict  = $verdict
        }
    }
    return ,$rows
}

# ------------------------------------------------------------ self test -----
if ($SelfTest) {
    Write-Host '== bench_regression self-test (pure functions, no benchmark runs) =='
    $failures = 0
    function Assert-True([bool]$Cond, [string]$Name) {
        if ($Cond) { Write-Host ("  ok    {0}" -f $Name) -ForegroundColor Gray }
        else {
            Write-Host ("  FAIL  {0}" -f $Name) -ForegroundColor Red
            $script:failures++
        }
    }

    # --- tolerance judgment: throughput band is [-20%, +inf)
    Assert-True (Test-WithinTolerance 100 80 'throughput') 'throughput exactly -20% passes (boundary, generous band)'
    Assert-True (-not (Test-WithinTolerance 100 79.999 'throughput')) 'throughput just beyond -20% is RED'
    Assert-True (Test-WithinTolerance 100 100 'throughput') 'throughput at baseline passes'
    Assert-True (Test-WithinTolerance 100 1000 'throughput') 'throughput improvement passes'
    Assert-True (-not (Test-WithinTolerance 100 0 'throughput')) 'throughput zero (metric collapsed) is RED'
    # --- tolerance judgment: time band is (-inf, +30%]
    Assert-True (Test-WithinTolerance 10 13 'time') 'time exactly +30% passes (boundary, generous band)'
    Assert-True (-not (Test-WithinTolerance 10 13.001 'time')) 'time just beyond +30% is RED'
    Assert-True (Test-WithinTolerance 10 5 'time') 'time improvement passes'
    Assert-True (-not (Test-WithinTolerance 10 25 'time')) 'time +150% is RED'
    # --- unknown kind must throw (defensive, never silently pass)
    $threw = $false
    try { Test-WithinTolerance 1 1 'bogus' | Out-Null } catch { $threw = $true }
    Assert-True $threw 'unknown tolerance kind throws (no silent pass)'
    # --- median (script-level run aggregation)
    Assert-True ((Get-Median -Values @(3.0, 1.0, 2.0)) -eq 2.0) 'median: odd count picks middle'
    Assert-True ((Get-Median -Values @(4.0, 1.0, 2.0, 3.0)) -eq 2.0) 'median: even count lower-middle'
    Assert-True ((Get-Median -Values @(5.0)) -eq 5.0) 'median: single value'

    # --- BENCH_JSON parsing
    $noise = @'
==== zkernel_bench (zkernel 0.1.0) ====
CPU: AMD Ryzen 7 7840U
[Compact    ] Destroy half then free 5 chunks : 1.23 ms
frame  1/60: [LIGHTS] flatEntries=42
zrhi_d3d12_window: [LIGHTS] frame_ms avg=1.500 min=1.000 max=2.000 n=59 (frame1 excluded)
BENCH_JSON {"program":"zkernel_bench","metrics":{"migrate_rows_per_sec":300.5,"compact_ms":1.25}}
BENCH_JSON {"program":"zrhi_d3d12_window","metrics":{"frame_ms_avg":1.5,"frames":59}}
BENCH_JSON {"program":"broken not json
BENCH_JSON {"program":"zrender_bench","metrics":{"sort_ms_a":2.0}}
BENCH_JSON {"program":"zrhi_d3d12_window_vk","metrics":{"frame_ms_avg_vk":1.75,"frames_vk":59}}
'@
    $all = Get-BenchJsonObjects -Text $noise
    Assert-True ($all.Count -eq 4) ("parse: 4 of 5 BENCH_JSON lines extracted (got {0})" -f $all.Count)
    $k = Get-BenchJsonObjects -Text $noise -Program 'zkernel_bench'
    Assert-True ($k.Count -eq 1) 'parse: program filter keeps only matching program'
    Assert-True ([double]$k[0].metrics.migrate_rows_per_sec -eq 300.5) 'parse: nested metric value readable as double'
    Assert-True ($k[0].metrics.compact_ms -eq 1.25) 'parse: second metric readable'
    # TASK-B-R9-11: the VK pseudo-program filters on its own JSON label and its
    # _vk-suffixed keys do not collide with the D3D12 frame_ms_avg.
    $vk = Get-BenchJsonObjects -Text $noise -Program 'zrhi_d3d12_window_vk'
    Assert-True ($vk.Count -eq 1) 'parse: VK pseudo-program label filtered (B-R9-11)'
    Assert-True ([double]$vk[0].metrics.frame_ms_avg_vk -eq 1.75) 'parse: _vk-suffixed metric readable'
    Assert-True ($null -eq $vk[0].metrics.frame_ms_avg) 'parse: VK line carries no D3D12 frame_ms_avg (no cross-backend collision)'

    # --- fallback frame_ms line parsing
    $fb = Get-FallbackFrameMs -Text $noise
    Assert-True ($null -ne $fb -and $fb.frame_ms_avg -eq 1.5 -and $fb.frames -eq 59) 'fallback: frame_ms human line parsed (avg/min/max/n)'
    Assert-True ($null -eq (Get-FallbackFrameMs -Text 'no such line here')) 'fallback: absent line yields null'

    # --- Compare-Metrics verdict matrix
    $kinds = @{
        'migrate_rows_per_sec' = 'throughput'; 'compact_ms'  = 'time'
        'sort_ms_a'            = 'time';       'sort_ms_b'   = 'time'
        'saved_pct_b'          = 'throughput'; 'frame_ms_avg' = 'time'
        'frame_ms_avg_vk'      = 'time'
    }
    $cur  = @{ 'migrate_rows_per_sec' = 100.0; 'compact_ms' = 10.0; 'sort_ms_a' = 99.0 }
    $base = @{ 'migrate_rows_per_sec' = 100.0; 'compact_ms' = 7.0;  'sort_ms_b' = 2.0 }
    $rows = Compare-Metrics -Current $cur -Baseline $base -KindByName $kinds
    $byName = @{}
    foreach ($r in $rows) { $byName[$r.Name] = $r }
    Assert-True ($rows.Count -eq 7) 'compare: all 7 gated metrics produce a row (6+1 B-R9-11)'
    Assert-True ($byName['migrate_rows_per_sec'].Verdict -eq 'OK') 'compare: within band -> OK'
    Assert-True ($byName['compact_ms'].Verdict -eq 'RED') 'compare: +42.9% time beyond +30% -> RED'
    Assert-True ($byName['sort_ms_a'].Verdict -eq 'NEW') 'compare: current without baseline -> NEW (warn only)'
    Assert-True ($byName['sort_ms_b'].Verdict -eq 'MISS') 'compare: baseline metric not collected -> MISS (gate red)'
    Assert-True ($byName['saved_pct_b'].Verdict -eq 'MISS') 'compare: second missing metric also MISS'
    Assert-True ($byName['frame_ms_avg'].Verdict -eq 'MISS') 'compare: all gated metrics produce a row'
    Assert-True ($byName['frame_ms_avg_vk'].Verdict -eq 'MISS') 'compare: VK gate metric #7 row produced (B-R9-11)'
    $rowsOk = Compare-Metrics -Current $base -Baseline $base -KindByName $kinds
    $reds = @($rowsOk | Where-Object { $_.Verdict -eq 'RED' })
    Assert-True ($reds.Count -eq 0) 'compare: identical current/baseline -> zero RED'
    # --- per-metric tolerance override (MetricDefs): migrate_rows_per_sec -35%
    $curOv  = @{ 'migrate_rows_per_sec' = 70.0; 'sort_ms_a' = 100.0 }
    $baseOv = @{ 'migrate_rows_per_sec' = 100.0; 'sort_ms_a' = 70.0 }
    $rowsOv = Compare-Metrics -Current $curOv -Baseline $baseOv -KindByName $kinds
    $byOv = @{}; foreach ($r in $rowsOv) { $byOv[$r.Name] = $r }
    Assert-True ($byOv['migrate_rows_per_sec'].Verdict -eq 'OK') 'override: migrate -30% passes (measured -35% band, class would RED)'
    Assert-True ($byOv['sort_ms_a'].Verdict -eq 'RED') 'override: sort_ms_a +42.9% still RED (class band, no override)'

    Write-Host ''
    if ($failures -gt 0) {
        Write-Host ("SELFTEST FAIL ({0} failed)" -f $failures) -ForegroundColor Red
        exit 1
    }
    Write-Host 'SELFTEST PASS' -ForegroundColor Green
    exit 0
}

# ------------------------------------------------------------------ main -----
function Main {
    if (-not $Gate -and $env:BENCH_GATE -eq '1') { $Gate = $true }

    New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

    # ---- machine quiet guard (evidence quality; not a performance test) ----
    if (-not $SkipQuietCheck) {
        if (-not (Test-Path $QuietScript)) {
            Write-Host 'BENCH_VERDICT: SKIP (check_machine_quiet.ps1 not found - cannot guarantee evidence quality)' -ForegroundColor Yellow
            return 0
        }
        $verdictLine = ''
        try {
            $quietOut = & $QuietScript -SampleSeconds 3 2>&1 | Out-String
            $v = ($quietOut -split "`n" | Where-Object { $_ -match 'VERDICT:' } | Select-Object -Last 1)
            if ($v) { $verdictLine = $v.Trim() }
        } catch {
            $verdictLine = 'VERDICT: check failed (' + $_.Exception.Message + ')'
        }
        if (-not $verdictLine) { $verdictLine = 'VERDICT: unknown' }
        Write-Host ("machine quiet check: {0}" -f $verdictLine)
        if ($verdictLine -notmatch 'QUIET') {
            Write-Host 'BENCH_VERDICT: SKIP (machine busy - performance numbers untrustworthy; nothing collected, nothing judged)' -ForegroundColor Yellow
            if ($UpdateBaseline) {
                Write-Host '  -UpdateBaseline refused: a baseline written under load poisons every future comparison.' -ForegroundColor Yellow
            }
            return 0
        }
    }

    # ---- collect --------------------------------------------------------------
    # Each program runs -RunsPerProgram times (default 3); the per-metric value
    # is the MEDIAN across runs - absorbs single-run outliers (e.g. a compact
    # run landing on a background spike). Run-to-run CPU frequency regimes are
    # NOT fully absorbed (see MetricDefs note on migrate_rows_per_sec).
    $progs = @('zkernel_bench', 'zrender_bench', 'zrhi_d3d12_window', 'zrhi_d3d12_window_vk')
    $outRuns   = @{}   # program -> list of stdout texts (one per run)
    $progOk    = @{}
    $progNote  = @{}
    $machine   = 'unknown'
    if ($RunsPerProgram -lt 1) { $RunsPerProgram = 1 }
    foreach ($p in $progs) {
        $exeName = $p
        if ($ProgramExe.ContainsKey($p)) { $exeName = $ProgramExe[$p] }
        $exe = Join-Path $ExeDir ("{0}.exe" -f $exeName)
        $log = Join-Path $LogDir ("bench_{0}.log" -f $p)
        if (-not (Test-Path $exe)) {
            $progOk[$p] = $false
            $progNote[$p] = ("exe not found: {0}" -f $exe)
            $outRuns[$p] = @()
            Write-Host ("  [warn] {0}" -f $progNote[$p]) -ForegroundColor Yellow
            continue
        }
        $texts = @()
        $allOk = $true
        $note  = 'ok'
        for ($i = 0; $i -lt $RunsPerProgram; ++$i) {
            Write-Host ("running {0} {1} (run {2}/{3}) ..." -f $p, ($ProgramArgs[$p] -join ' '), ($i + 1), $RunsPerProgram)
            try {
                # PS 5.1 trap (documented in build_native.ps1): with EAP=Stop, native
                # stderr lines wrapped in 2>&1 become terminating errors. Relax EAP
                # around the native invocation only; a non-zero exit is handled below.
                $prevEap = $ErrorActionPreference
                $ErrorActionPreference = 'Continue'
                $text = (& $exe @($ProgramArgs[$p]) 2>&1 | Out-String)
                $code = $LASTEXITCODE
                $ErrorActionPreference = $prevEap
                $texts += $text
                if ($code -ne 0) {
                    $allOk = $false
                    $note = ("exit={0} on run {1} (log: TempLogs/bench_{2}.log)" -f $code, ($i + 1), $p)
                    Write-Host ("  [warn] {0} {1}" -f $p, $note) -ForegroundColor Yellow
                }
            } catch {
                $ErrorActionPreference = $prevEap
                $allOk = $false
                $note = $_.Exception.Message
                Write-Host ("  [warn] {0} failed to run: {1}" -f $p, $_.Exception.Message) -ForegroundColor Yellow
            }
        }
        [System.IO.File]::WriteAllText($log, ($texts -join "`n"), (New-Object System.Text.UTF8Encoding($false)))
        $progOk[$p] = $allOk
        $progNote[$p] = $note
        $outRuns[$p] = $texts
    }

    # machine marker: CPU brand line from zkernel_bench human output
    foreach ($runText in $outRuns['zkernel_bench']) {
        foreach ($line in ($runText -split "`r?`n")) {
            if ($line -match '^CPU:\s+(.+)$') { $machine = $Matches[1].Trim(); break }
        }
        if ($machine -ne 'unknown') { break }
    }

    # ---- parse: programmatic --json first, human frame_ms line as fallback ----
    # Per program: collect metric -> list of values across runs, then median.
    $current = @{}
    $parseNotes = @()
    foreach ($p in $progs) {
        if (-not $progOk[$p]) { $parseNotes += ("{0}: not collected ({1})" -f $p, $progNote[$p]); continue }
        $metricRuns = @{}   # metric name -> List[double] across runs
        $parsedAny = $false
        foreach ($runText in $outRuns[$p]) {
            $objs = Get-BenchJsonObjects -Text $runText -Program $p
            # Documented fallback (stale binary without --json): parse the human
            # frame_ms line. The [LIGHTS][vulkan] variant matches the same regex;
            # the VK pseudo-program maps its values onto the _vk-suffixed keys.
            $isVkProg = ($p -eq 'zrhi_d3d12_window_vk')
            $fallbackProg = 'zrhi_d3d12_window'
            if ($isVkProg) { $fallbackProg = 'zrhi_d3d12_window_vk' }
            if ($objs.Count -eq 0 -and ($p -eq 'zrhi_d3d12_window' -or $isVkProg)) {
                $fb = Get-FallbackFrameMs -Text $runText
                if ($null -ne $fb) {
                    $parsedAny = $true
                    $suffix = ''
                    if ($isVkProg) { $suffix = '_vk' }
                    foreach ($m in @('frame_ms_avg', 'frame_ms_min', 'frame_ms_max')) {
                        $key = ($m + $suffix)
                        if (-not $metricRuns.ContainsKey($key)) { $metricRuns[$key] = New-Object System.Collections.Generic.List[double] }
                        $metricRuns[$key].Add([double]$fb.$m)
                    }
                    $note = ($fallbackProg + ': parsed human frame_ms line (--json line absent)')
                    if ($parseNotes -notcontains $note) {
                        $parseNotes += $note
                    }
                    continue
                }
            }
            if ($objs.Count -eq 0) { continue }
            $parsedAny = $true
            foreach ($prop in $objs[0].metrics.PSObject.Properties) {
                if (-not $metricRuns.ContainsKey($prop.Name)) {
                    $metricRuns[$prop.Name] = New-Object System.Collections.Generic.List[double]
                }
                $metricRuns[$prop.Name].Add([double]$prop.Value)
            }
        }
        if (-not $parsedAny) {
            $parseNotes += ("{0}: no BENCH_JSON line in any run (stale binary? rebuild)" -f $p)
            continue
        }
        foreach ($name in $metricRuns.Keys) {
            $current[$name] = Get-Median -Values ([double[]]$metricRuns[$name].ToArray())
        }
    }

    # ---- -UpdateBaseline: write clean numbers and leave -----------------------
    if ($UpdateBaseline) {
        $missing = @($Script:MetricDefs.Keys | Where-Object { -not $current.ContainsKey($_) })
        if ($missing.Count -gt 0) {
            Write-Host ("BASELINE UPDATE FAILED: gated metrics not collectible: {0}" -f ($missing -join ', ')) -ForegroundColor Red
            return 1
        }
        # Quiet provenance: a baseline written via -SkipQuietCheck carries that
        # fact; every later comparison warns about it until a quiet re-baseline.
        $quietState = 'QUIET'
        if ($SkipQuietCheck) { $quietState = 'SKIPPED (SkipQuietCheck - ambient load not verified)' }
        # Canonical 2-space JSON by hand (file-conventions: JSON 2-space indent;
        # ConvertTo-Json on PS 5.1 emits 4-space + noisy member alignment).
        function Format-JsString([string]$s) {
            return '"' + ($s -replace '\\', '\\\\' -replace '"', '\"') + '"'
        }
        $lines = @('{', '  "schema": 1,')
        $lines += ('  "created": ' + (Format-JsString (Get-Date -Format 'yyyy-MM-dd')) + ',')
        $lines += ('  "updated": ' + (Format-JsString (Get-Date -Format 'yyyy-MM-dd')) + ',')
        $lines += ('  "machine": ' + (Format-JsString $machine) + ',')
        $lines += ('  "machine_quiet": ' + (Format-JsString $quietState) + ',')
        $lines += '  "metrics": {'
        $kv = foreach ($name in $Script:MetricDefs.Keys) {
            ('    ' + (Format-JsString $name) + ': ' + ([string]([math]::Round([double]$current[$name], 4))))
        }
        $lines += ($kv -join ",`n")
        $lines += '  }'
        $lines += '}'
        $json = ($lines -join "`n")
        [System.IO.File]::WriteAllText($BaselinePath, $json + "`n", (New-Object System.Text.UTF8Encoding($false)))
        Write-Host ''
        Write-Host ("baseline written: {0}" -f $BaselinePath) -ForegroundColor Green
        Write-Host ("  machine: {0}" -f $machine)
        Write-Host ("  metrics: {0}" -f (($Script:MetricDefs.Keys | ForEach-Object { "{0}={1}" -f $_, $current[$_] }) -join ', '))
        Write-Host 'BENCH_VERDICT: BASELINE UPDATED'
        return 0
    }

    # ---- compare vs baseline ---------------------------------------------------
    if (-not (Test-Path $BaselinePath)) {
        Write-Host ("no baseline: {0}" -f $BaselinePath) -ForegroundColor Yellow
        Write-Host '  run -UpdateBaseline once (on a quiet machine) to create the committed baseline.'
        if ($Gate) {
            Write-Host 'BENCH_VERDICT: FAIL (gate on, but no baseline to judge against - cannot prove no regression)' -ForegroundColor Red
            return 1
        }
        Write-Host 'BENCH_VERDICT: SKIP (no baseline - report-only)'
        return 0
    }
    try {
        $baselineDoc = Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json
    } catch {
        Write-Host ("baseline unreadable: {0} ({1})" -f $BaselinePath, $_.Exception.Message) -ForegroundColor Red
        if ($Gate) { Write-Host 'BENCH_VERDICT: FAIL (gate on, baseline broken)' -ForegroundColor Red; return 1 }
        Write-Host 'BENCH_VERDICT: SKIP (baseline broken - report-only)'
        return 0
    }
    $baseline = @{}
    if ($baselineDoc.metrics) {
        foreach ($prop in $baselineDoc.metrics.PSObject.Properties) {
            $baseline[$prop.Name] = [double]$prop.Value
        }
    }
    if ($baselineDoc.machine -and $baselineDoc.machine -ne $machine) {
        Write-Host ("  [warn] baseline machine '{0}' != this machine '{1}' - cross-machine numbers are indicative only" -f $baselineDoc.machine, $machine) -ForegroundColor Yellow
    }
    if ($baselineDoc.machine_quiet -and ("{0}" -f $baselineDoc.machine_quiet) -notmatch '^QUIET') {
        Write-Host ("  [warn] baseline was created WITHOUT a machine-quiet verification ({0}) - consider re-running -UpdateBaseline on a quiet machine" -f $baselineDoc.machine_quiet) -ForegroundColor Yellow
    }

    $kinds = @{}
    foreach ($name in $Script:MetricDefs.Keys) { $kinds[$name] = $Script:MetricDefs[$name].Kind }
    $rows = Compare-Metrics -Current $current -Baseline $baseline -KindByName $kinds

    # ---- table ------------------------------------------------------------------
    Write-Host ''
    Write-Host '==== benchmark regression (baseline vs current) ===='
    $tableRows = foreach ($r in $rows) {
        [pscustomobject]@{
            metric   = $r.Name
            kind     = $r.Kind
            baseline = if ($baseline.ContainsKey($r.Name)) { $baseline[$r.Name] } else { 'n/a' }
            current  = if ($current.ContainsKey($r.Name)) { $current[$r.Name] } else { 'n/a' }
            delta    = if ($null -ne $r.DeltaPct) { '{0:+0.0;-0.0}%' -f $r.DeltaPct } else { 'n/a' }
            verdict  = $r.Verdict
        }
    }
    $tableRows | Format-Table -AutoSize | Out-String -Width 160 | Write-Host
    foreach ($n in $parseNotes) { Write-Host ("  note: {0}" -f $n) -ForegroundColor DarkGray }

    $reds  = @($rows | Where-Object { $_.Verdict -eq 'RED' })
    $miss  = @($rows | Where-Object { $_.Verdict -eq 'MISS' })
    $neu   = @($rows | Where-Object { $_.Verdict -eq 'NEW' })
    $crash = @($progs | Where-Object { -not $progOk[$_] })
    $redNames = @(($reds + $miss) | ForEach-Object { $_.Name })

    if ($reds.Count -gt 0) {
        foreach ($r in $reds) {
            Write-Host ("  RED: {0}: baseline={1} current={2} delta={3}% (kind={4})" -f `
                $r.Name, $r.Baseline, $r.Current, ('{0:+0.0;-0.0}' -f $r.DeltaPct), $r.Kind) -ForegroundColor Red
        }
    }
    if ($neu.Count -gt 0) {
        Write-Host ("  [warn] metrics without baseline (update baseline to gate them): {0}" -f (($neu | ForEach-Object Name) -join ', ')) -ForegroundColor Yellow
    }

    # ---- verdict ---------------------------------------------------------------
    if ($crash.Count -gt 0) {
        Write-Host ("  ERROR: benchmark programs failed to run: {0}" -f (($crash | ForEach-Object { "{0} ({1})" -f $_, $progNote[$_] }) -join '; ')) -ForegroundColor Red
    }

    if ($Gate) {
        if ($crash.Count -gt 0) {
            Write-Host 'BENCH_VERDICT: FAIL (gate on; benchmark program crashed - cannot prove no regression)' -ForegroundColor Red
            return 1
        }
        if ($redNames.Count -gt 0) {
            Write-Host ("BENCH_VERDICT: FAIL (gate on; {0} beyond tolerance: {1})" -f $redNames.Count, ($redNames -join ', ')) -ForegroundColor Red
            return 1
        }
        Write-Host ("BENCH_VERDICT: PASS (gate on; {0}/{1} within tolerance)" -f ($rows.Count - $neu.Count), $rows.Count) -ForegroundColor Green
        return 0
    }
    # report-only: never fail on tolerance; surface everything in the table
    if ($crash.Count -gt 0) {
        Write-Host 'BENCH_VERDICT: ERROR (report-only; benchmark program crashed - see notes above)' -ForegroundColor Red
        return 0
    }
    if ($redNames.Count -gt 0) {
        Write-Host ("BENCH_VERDICT: REPORT-ONLY ({0} red, not gating: {1}; set BENCH_GATE=1 to enforce)" -f $redNames.Count, ($redNames -join ', ')) -ForegroundColor Yellow
        return 0
    }
    Write-Host ("BENCH_VERDICT: PASS (report-only; {0}/{1} within tolerance; set BENCH_GATE=1 to enforce)" -f ($rows.Count - $neu.Count), $rows.Count) -ForegroundColor Green
    return 0
}

exit (Main)
