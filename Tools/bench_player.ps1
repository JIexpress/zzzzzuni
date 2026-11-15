# =============================================================================
# bench_player.ps1 - player frame-time regression gate (TASK A-PERF-02)
#
# Why this file exists: bench_regression.ps1 gates the three native benchmark
# programs only (zkernel_bench / zrender_bench / zrhi_d3d12_window). The player
# frame baseline - the numbers a player actually feels - had no gate at all
# (grep 'frame-baseline' in bench_regression.ps1: 0 hits), so a frame-time
# regression could only be caught by someone remembering to compare by hand.
# bench_regression.ps1 is deliberately NOT touched by this task; the native
# gate keeps its own numbers, its own SelfTest and its own exit semantics.
#
# Pipeline (cost profile only - asShipped p50 is a vsync beat, not a cost; see
# Docs/Planning/ (frame-time baseline doc, 2026-11-15) section 4.1):
#   round i in 1..N (default N=5, ~10s each):
#     Build/Windows64/ZFrame.exe -framebaseline-vsync0
#       -screen-width 1920 -screen-height 1080 -screen-fullscreen 0
#     -> exactly one new Tools/Probes/out/frame-baseline-<UTC>.json
#   Round 1 is DISCARDED (cold-start PSO bias, registered in the same doc's
#   regression-criterion section as "cold first round GPU reads low, cause not
#   yet proven").
#   Per metric: p50 = MEDIAN across the kept rounds of statistics.<m>.p50.
#   Compared against Tools/Probes/baseline/player-frame-baseline.json:
#     RED when current > baseline * (1 + envelope)   (all six metrics are
#     "lower is better", so only the upper bound matters)
#
# Exit codes (stable contract - docs quote these):
#   0  every metric within its envelope (report-only mode also returns 0 on
#      RED: it reports, it does not gate)
#   1  at least one RED; only reachable with -Gate
#   2  MISS: no data / probe JSON missing a field / environment guard tripped.
#      "Cannot prove no regression" counts as a regression, so MISS never
#      degrades to 0 and never fills a default value in.
#
# Modes:
#   default          report-only: measure, compare, table, exit 0 unless MISS
#   -Gate            enforcement: exit 1 on any RED, exit 2 on any MISS
#   -UpdateBaseline  measure and rewrite the committed baseline JSON
#                    (machine + date + per-metric p50/envelope + measured
#                    spread); refused while a competing process is running
#   -SelfTest        pure-function tests only (median / envelope judgment /
#                    JSON parse / drop-first-round / round-file selection /
#                    exit-code mapping). No player is launched. exit 0/1.
#
# Environment guard (measurement hygiene, reused, not reimplemented):
#   * a running ZFrame.exe / Unity.exe steals GPU+CPU -> exit 2 before anything
#     is measured (checked before the run and before every round)
#   * check_machine_quiet.ps1 (CPU share > threshold) -> exit 2 unless -Force
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools/bench_player.ps1
#   powershell ... -File Tools/bench_player.ps1 -Gate
#   powershell ... -File Tools/bench_player.ps1 -UpdateBaseline
#   powershell ... -File Tools/bench_player.ps1 -SelfTest
#   powershell ... -File Tools/bench_player.ps1 -Gate -Force      # busy box
# =============================================================================
param(
    [switch]$Gate,
    [switch]$UpdateBaseline,
    [switch]$SelfTest,
    [switch]$SkipQuietCheck,
    [switch]$Force,
    [int]$Rounds = 5,
    [int]$RoundTimeoutSec = 240
)

$ErrorActionPreference = 'Stop'

$RepoRoot     = Split-Path -Parent $PSScriptRoot
$PlayerExe    = Join-Path $RepoRoot 'Build/Windows64/ZFrame.exe'
$OutDir       = Join-Path $PSScriptRoot 'Probes/out'
$BaselinePath = Join-Path $PSScriptRoot 'Probes/baseline/player-frame-baseline.json'
$QuietScript  = Join-Path $PSScriptRoot 'check_machine_quiet.ps1'
$LogDir       = Join-Path $RepoRoot 'TempLogs'
$ExpectedMode = 'cost'

# The gated metric set. Envelope default = the regression criterion registered
# in the frame-time baseline doc (2026-11-15): "effective threshold = median
# and measured cross-round envelope over >=5 rounds across >=2 sessions:
# GPU ~10% / main thread ~12% / render thread ~20%". The three count metrics
# have no registered envelope; they are deterministic per run
# (3 cost rounds: SetPass 28 constant, Batches 84 constant, Triangles +/-2), so
# a 10% band absorbs scene-entry variance while still flagging a whole
# draw-call class or a pass appearing - not noise.
$Script:MetricDefs = [ordered]@{
    'gpuFrameTime'             = @{ Unit = 'ms';    Envelope = 0.10 }
    'cpuMainThreadFrameTime'   = @{ Unit = 'ms';    Envelope = 0.12 }
    'cpuRenderThreadFrameTime' = @{ Unit = 'ms';    Envelope = 0.20 }
    'setPassCalls'             = @{ Unit = 'count'; Envelope = 0.10 }
    'batches'                  = @{ Unit = 'count'; Envelope = 0.10 }
    'triangles'                = @{ Unit = 'count'; Envelope = 0.10 }
}

# ---------------------------------------------------------------- pure fns ---
# Median of a numeric array. Even counts: lower middle element (same rule as
# bench_regression.ps1 Get-Median - one median definition in this repo).
function Get-Median {
    param([double[]]$Values)
    if ($null -eq $Values -or $Values.Count -eq 0) { throw 'Get-Median: empty input' }
    $sorted = $Values | Sort-Object
    return [double]$sorted[[int][math]::Floor(($sorted.Count - 1) / 2)]
}

# Envelope judgment (pure). $true = within band (PASS). All player metrics are
# "lower is better", so only the upper bound is gated; improvement always
# passes. Exactly +envelope still passes (band boundary), beyond it is RED.
function Test-WithinEnvelope {
    param(
        [double]$Baseline,
        [double]$Current,
        [double]$Envelope
    )
    if ($Baseline -le 0.0) { throw ("Test-WithinEnvelope: baseline must be > 0 (got {0})" -f $Baseline) }
    if ($Envelope -lt 0.0) { throw ("Test-WithinEnvelope: envelope must be >= 0 (got {0})" -f $Envelope) }
    return $Current -le ($Baseline * (1.0 + $Envelope))
}

# Drop the first round (cold start). Pure; never mutates the input array.
function Get-KeptRoundSets {
    param(
        [object[]]$RoundSets,
        [bool]$DropFirst = $true
    )
    $all = @($RoundSets)
    if ($DropFirst -and $all.Count -gt 1) {
        return ,@($all[1..($all.Count - 1)])
    }
    return ,$all
}

# Per-metric median / min / max / spread over the kept rounds (pure).
# Returns [pscustomobject]@{ KeptCount; Metrics = ordered name -> object }.
# A metric absent from some round is skipped there; Count exposes that so the
# caller can refuse a partial aggregate instead of averaging 3 of 4 rounds.
function Get-Aggregate {
    param(
        [object[]]$RoundSets,
        [string[]]$MetricNames
    )
    $sets = @($RoundSets)
    $metrics = [ordered]@{}
    foreach ($m in $MetricNames) {
        $vals = @()
        foreach ($s in $sets) {
            if ($null -eq $s) { continue }
            if (-not $s.ContainsKey($m)) { continue }
            $vals += [double]$s[$m]
        }
        if ($vals.Count -eq 0) { $metrics[$m] = $null; continue }
        $med = Get-Median -Values ([double[]]$vals)
        $mn = [double](($vals | Measure-Object -Minimum).Minimum)
        $mx = [double](($vals | Measure-Object -Maximum).Maximum)
        $spread = 0.0
        if ($med -gt 0.0) { $spread = 100.0 * ($mx - $mn) / $med }
        $metrics[$m] = [pscustomobject]@{
            P50       = $med
            Min       = $mn
            Max       = $mx
            SpreadPct = $spread
            Count     = $vals.Count
        }
    }
    return [pscustomobject]@{ KeptCount = $sets.Count; Metrics = $metrics }
}

# Pick the round's own probe file: the earliest-mtime file that did not exist
# before the round started (pure). "Earliest" because a stale file from a
# previous session is excluded by $Known, and if two appeared the one written
# first is this round's (a later one would mean a concurrent player, which the
# env guard already refuses).
function Select-RoundFile {
    param(
        [object[]]$Candidates,   # each: @{ Name = ...; LastWriteUtc = [datetime] }
        [string[]]$Known
    )
    $fresh = @($Candidates | Where-Object { $Known -notcontains $_.Name })
    if ($fresh.Count -eq 0) { return $null }
    $sorted = @($fresh | Sort-Object LastWriteUtc)
    return $sorted[0]
}

# Parse one frame-baseline probe JSON (pure - text in, no file IO).
# Ok=$false carries a human Reason; the caller turns that into MISS (exit 2).
function Parse-FrameBaseline {
    param(
        [string]$Text,
        [string[]]$MetricNames,
        [string]$ExpectMode = 'cost'
    )
    $res = [pscustomobject]@{
        Ok             = $false
        Reason         = ''
        Mode           = ''
        Status         = ''
        Machine        = ''
        GeneratedAtUtc = ''
        Values         = @{}
    }
    if (-not $Text) { $res.Reason = 'empty probe file'; return $res }
    try {
        $o = $Text | ConvertFrom-Json
    } catch {
        $res.Reason = ('invalid JSON: {0}' -f $_.Exception.Message)
        return $res
    }
    if ($null -eq $o) { $res.Reason = 'JSON parsed to null'; return $res }
    if ([string]$o.probe -ne 'frame-baseline') {
        $res.Reason = ("probe != 'frame-baseline' (got '{0}')" -f [string]$o.probe)
        return $res
    }
    $res.Mode   = [string]$o.mode
    $res.Status = [string]$o.status
    $res.GeneratedAtUtc = [string]$o.generatedAtUtc
    if ($o.gpu) {
        $res.Machine = (([string]$o.gpu.processor).Trim() + ' / ' + [string]$o.gpu.name)
    }
    if ($ExpectMode -and $res.Mode -ne $ExpectMode) {
        $res.Reason = ("mode '{0}' != expected '{1}' (asShipped numbers are vsync beats, not cost)" -f $res.Mode, $ExpectMode)
        return $res
    }
    if ($res.Status -ne 'ok') {
        $res.Reason = ("status '{0}' != 'ok' (aborted sampling carries incomplete data)" -f $res.Status)
        return $res
    }
    if ($null -eq $o.statistics) {
        $res.Reason = 'statistics object absent'
        return $res
    }
    $vals = @{}
    foreach ($m in $MetricNames) {
        $node = $o.statistics.($m)
        if ($null -eq $node) {
            $res.Reason = ("statistics.{0} absent" -f $m)
            return $res
        }
        if ($null -eq $node.p50) {
            $res.Reason = ("statistics.{0}.p50 is null (probe reported MISSING)" -f $m)
            return $res
        }
        $vals[$m] = [double]$node.p50
    }
    $res.Values = $vals
    $res.Ok = $true
    return $res
}

# Parse the committed baseline (pure). Returns Ok/Reason/Machine/Date and
# Metrics = name -> @{ P50; Envelope }. Envelope falls back to the metric's
# default and is flagged in Warnings (an old baseline without envelopes is
# still usable, but the reader deserves to know which band was applied).
function Read-BaselineDoc {
    param(
        [string]$Text,
        [string[]]$MetricNames
    )
    $res = [pscustomobject]@{
        Ok       = $false
        Reason   = ''
        Machine  = ''
        Date     = ''
        Metrics  = @{}
        Warnings = @()
    }
    if (-not $Text) { $res.Reason = 'empty baseline file'; return $res }
    try {
        $o = $Text | ConvertFrom-Json
    } catch {
        $res.Reason = ('invalid JSON: {0}' -f $_.Exception.Message)
        return $res
    }
    if ($null -eq $o) { $res.Reason = 'JSON parsed to null'; return $res }
    $res.Machine = [string]$o.machine
    $res.Date    = [string]$o.date
    if ($null -eq $o.metrics) { $res.Reason = 'metrics object absent'; return $res }
    $warn = @()
    $metrics = @{}
    foreach ($m in $MetricNames) {
        $node = $o.metrics.($m)
        if ($null -eq $node) {
            $res.Reason = ("baseline metric '{0}' absent (cannot prove no regression)" -f $m)
            return $res
        }
        if ($null -eq $node.p50) {
            $res.Reason = ("baseline metric '{0}' has no p50" -f $m)
            return $res
        }
        $p50 = [double]$node.p50
        if ($p50 -le 0.0) {
            $res.Reason = ("baseline metric '{0}' p50={1} is not > 0" -f $m, $p50)
            return $res
        }
        $env = $null
        if ($null -ne $node.envelope) { $env = [double]$node.envelope }
        else {
            $env = [double]$Script:MetricDefs[$m].Envelope
            $warn += ("{0}: no envelope in baseline, using default {1:P0}" -f $m, $env)
        }
        $metrics[$m] = @{ P50 = $p50; Envelope = $env }
    }
    $res.Metrics  = $metrics
    $res.Warnings = $warn
    $res.Ok = $true
    return $res
}

# Compare current vs baseline (pure). Returns one row per gated metric:
#   OK / RED / MISS. MISS covers "no current value" and "no baseline value" -
# both mean the gate cannot prove anything, and this gate treats that as
# failure rather than as a pass.
function Compare-FrameMetrics {
    param(
        [System.Collections.IDictionary]$Current,   # name -> double
        [hashtable]$Baseline,                       # name -> @{ P50; Envelope }
        [string[]]$MetricNames
    )
    $rows = @()
    foreach ($m in $MetricNames) {
        $hasCur = $Current.Contains($m)
        $hasBase = $Baseline.ContainsKey($m)
        $cur = 0.0
        if ($hasCur) { $cur = [double]$Current[$m] }
        $base = 0.0
        $env = 0.0
        $note = ''
        if ($hasBase) {
            $base = [double]$Baseline[$m].P50
            $env  = [double]$Baseline[$m].Envelope
        } else {
            $note = 'no baseline value'
        }
        if (-not $hasCur) { $note = 'no current value' }
        $verdict = 'OK'
        if (-not $hasCur -or -not $hasBase) { $verdict = 'MISS' }
        elseif (-not (Test-WithinEnvelope -Baseline $base -Current $cur -Envelope $env)) { $verdict = 'RED' }
        $delta = $null
        if ($hasCur -and $hasBase -and $base -gt 0.0) {
            $delta = 100.0 * ($cur - $base) / $base
        }
        $rows += [pscustomobject]@{
            Name     = $m
            Baseline = $base
            Envelope = $env
            Current  = $cur
            DeltaPct = $delta
            Verdict  = $verdict
            Note     = $note
        }
    }
    return ,$rows
}

# Exit-code mapping (pure, the contract docs quote):
#   MISS (either mode) -> 2 ; RED -> 1 only in gate mode ; else 0
function Get-CheckExitCode {
    param(
        [int]$RedCount,
        [int]$MissCount,
        [bool]$GateMode
    )
    if ($MissCount -gt 0) { return 2 }
    if ($RedCount -gt 0) {
        if ($GateMode) { return 1 }
        return 0
    }
    return 0
}

# ------------------------------------------------------------ self test -----
if ($SelfTest) {
    Write-Host '== bench_player self-test (pure functions, no player launched) =='
    $failures = 0
    function Assert-True([bool]$Cond, [string]$Name) {
        if ($Cond) { Write-Host ("  ok    {0}" -f $Name) -ForegroundColor Gray }
        else {
            Write-Host ("  FAIL  {0}" -f $Name) -ForegroundColor Red
            $script:failures++
        }
    }
    $names = @($Script:MetricDefs.Keys)

    # --- median (same definition as bench_regression.ps1)
    Assert-True ((Get-Median -Values @(3.0, 1.0, 2.0)) -eq 2.0) 'median: odd count picks middle'
    Assert-True ((Get-Median -Values @(4.0, 1.0, 2.0, 3.0)) -eq 2.0) 'median: even count lower-middle (4 rounds)'
    Assert-True ((Get-Median -Values @(5.0)) -eq 5.0) 'median: single value'
    $threw = $false
    try { Get-Median -Values @() | Out-Null } catch { $threw = $true }
    Assert-True $threw 'median: empty input throws (no silent 0)'

    # --- envelope judgment (upper bound only, boundary inclusive)
    Assert-True (Test-WithinEnvelope -Baseline 3.0 -Current 3.3 -Envelope 0.10) 'envelope: exactly +10% passes (boundary)'
    Assert-True (-not (Test-WithinEnvelope -Baseline 3.0 -Current 3.3001 -Envelope 0.10)) 'envelope: just beyond +10% is RED'
    Assert-True (Test-WithinEnvelope -Baseline 3.0 -Current 2.0 -Envelope 0.10) 'envelope: improvement always passes'
    Assert-True (Test-WithinEnvelope -Baseline 3.0 -Current 3.0 -Envelope 0.0) 'envelope: zero band, equal value passes'
    Assert-True (-not (Test-WithinEnvelope -Baseline 3.0 -Current 3.01 -Envelope 0.0)) 'envelope: zero band, +0.3% is RED'
    Assert-True (-not (Test-WithinEnvelope -Baseline 3.0 -Current 3.6 -Envelope 0.12)) 'envelope: +20% vs 12% band is RED'
    $threw = $false
    try { Test-WithinEnvelope -Baseline 0.0 -Current 1.0 -Envelope 0.1 | Out-Null } catch { $threw = $true }
    Assert-True $threw 'envelope: non-positive baseline throws (never silently passes)'

    # --- drop first round + per-metric median of the remaining four
    $r1 = @{ 'gpuFrameTime' = 99.0;  'cpuMainThreadFrameTime' = 3.0 }
    $r2 = @{ 'gpuFrameTime' = 3.0;   'cpuMainThreadFrameTime' = 3.0 }
    $r3 = @{ 'gpuFrameTime' = 3.1;   'cpuMainThreadFrameTime' = 3.1 }
    $r4 = @{ 'gpuFrameTime' = 3.2;   'cpuMainThreadFrameTime' = 3.2 }
    $r5 = @{ 'gpuFrameTime' = 3.3;   'cpuMainThreadFrameTime' = 3.3 }
    $sets = @($r1, $r2, $r3, $r4, $r5)
    $kept = Get-KeptRoundSets -RoundSets $sets -DropFirst $true
    Assert-True ($kept.Count -eq 4) ("dropFirst: 5 rounds -> 4 kept (got {0})" -f $kept.Count)
    Assert-True ([object]::ReferenceEquals($kept[0], $r2)) 'dropFirst: round 1 (cold) is the one discarded'
    $agg = Get-Aggregate -RoundSets $kept -MetricNames $names
    Assert-True ($agg.Metrics['gpuFrameTime'].P50 -eq 3.1) ('aggregate: gpu p50 = lower-middle of 3.0/3.1/3.2/3.3 = 3.1 (got {0})' -f $agg.Metrics['gpuFrameTime'].P50)
    Assert-True ($agg.Metrics['gpuFrameTime'].P50 -ne 99.0) 'aggregate: cold-start outlier never reaches the median'
    Assert-True ($agg.Metrics['gpuFrameTime'].Count -eq 4) 'aggregate: value count = kept rounds'
    Assert-True ($agg.Metrics['gpuFrameTime'].Min -eq 3.0 -and $agg.Metrics['gpuFrameTime'].Max -eq 3.3) 'aggregate: min/max from kept rounds'
    Assert-True ([math]::Abs($agg.Metrics['gpuFrameTime'].SpreadPct - (100.0 * 0.3 / 3.1)) -lt 1e-9) 'aggregate: spread = (max-min)/median'
    Assert-True ($null -eq $agg.Metrics['batches']) 'aggregate: metric absent everywhere -> null (caller must MISS)'
    $aggNoDrop = Get-Aggregate -RoundSets $sets -MetricNames @('gpuFrameTime')
    Assert-True ($aggNoDrop.Metrics['gpuFrameTime'].Count -eq 5) 'aggregate: without dropFirst all 5 rounds count (control)'
    $partial = Get-Aggregate -RoundSets @(@{ 'gpuFrameTime' = 3.0 }, @{ 'gpuFrameTime' = 3.2 }, @{ 'batches' = 84 }) -MetricNames @('gpuFrameTime')
    Assert-True ($partial.Metrics['gpuFrameTime'].Count -eq 2) 'aggregate: partial data surfaces as Count < kept (caller refuses it)'

    # --- probe JSON parsing
    $good = @'
{
  "probe": "frame-baseline",
  "mode": "cost",
  "status": "ok",
  "generatedAtUtc": "2026-11-15T04:17:12Z",
  "gpu": { "name": "AMD Radeon 780M Graphics", "processor": "AMD Ryzen 7 7840HS w/ Radeon 780M Graphics " },
  "statistics": {
    "gpuFrameTime":             { "p50": 3.1088 },
    "cpuMainThreadFrameTime":   { "p50": 3.4956 },
    "cpuRenderThreadFrameTime": { "p50": 0.742 },
    "setPassCalls":             { "p50": 28 },
    "batches":                  { "p50": 84 },
    "triangles":                { "p50": 106738 },
    "vertices":                 { "p50": 96959 }
  }
}
'@
    $p = Parse-FrameBaseline -Text $good -MetricNames $names
    Assert-True $p.Ok 'parse: complete cost JSON accepted'
    Assert-True ($p.Values['gpuFrameTime'] -eq 3.1088) 'parse: gpu p50 read as double'
    Assert-True ($p.Values['triangles'] -eq 106738) 'parse: count metric read as double'
    Assert-True ($p.Values.Count -eq 6) 'parse: exactly the six gated metrics collected'
    Assert-True ($p.Machine -match 'Ryzen 7 7840HS') 'parse: machine marker from gpu.processor'

    $missGpu = $good.Replace('"p50": 3.1088', '"p50": null')
    $pm = Parse-FrameBaseline -Text $missGpu -MetricNames $names
    Assert-True (-not $pm.Ok -and $pm.Reason -match 'gpuFrameTime') 'parse: null p50 -> not Ok, reason names the metric (MISS not 0)'

    $missingNode = $good -replace '(?m)^.*"setPassCalls".*\r?\n', ''
    $pn = Parse-FrameBaseline -Text $missingNode -MetricNames $names
    Assert-True (-not $pn.Ok -and $pn.Reason -match 'setPassCalls') 'parse: absent statistics node -> not Ok, reason names the metric'

    $shipped = $good.Replace('"mode": "cost"', '"mode": "asShipped"')
    $ps = Parse-FrameBaseline -Text $shipped -MetricNames $names
    Assert-True (-not $ps.Ok -and $ps.Reason -match 'asShipped') 'parse: asShipped file rejected (wrong cost profile)'

    $aborted = $good.Replace('"status": "ok"', '"status": "aborted"')
    $pa = Parse-FrameBaseline -Text $aborted -MetricNames $names
    Assert-True (-not $pa.Ok -and $pa.Reason -match 'aborted') 'parse: aborted sampling rejected (incomplete data)'

    $bad = Parse-FrameBaseline -Text 'BENCH_JSON {"probe":"frame-baseline"}' -MetricNames $names
    Assert-True (-not $bad.Ok) 'parse: garbage text -> not Ok (never throws into the gate)'
    $empty = Parse-FrameBaseline -Text '' -MetricNames $names
    Assert-True (-not $empty.Ok) 'parse: empty file -> not Ok'

    # --- per-round file selection (mtime ascending, known files excluded)
    $t0 = [datetime]::new(2026, 11, 15, 4, 10, 0, [System.DateTimeKind]::Utc)
    $known = @('frame-baseline-old1.json', 'frame-baseline-old2.json')
    $cands = @(
        [pscustomobject]@{ Name = 'frame-baseline-old1.json'; LastWriteUtc = $t0.AddMinutes(-5) },
        [pscustomobject]@{ Name = 'frame-baseline-newB.json'; LastWriteUtc = $t0.AddSeconds(20) },
        [pscustomobject]@{ Name = 'frame-baseline-newA.json'; LastWriteUtc = $t0.AddSeconds(10) },
        [pscustomobject]@{ Name = 'frame-baseline-old2.json'; LastWriteUtc = $t0.AddSeconds(1) }
    )
    $pick = Select-RoundFile -Candidates $cands -Known $known
    Assert-True ($null -ne $pick -and $pick.Name -eq 'frame-baseline-newA.json') 'roundFile: earliest new file wins (no cross-round mixing)'
    $pick2 = Select-RoundFile -Candidates @($cands[0], $cands[3]) -Known $known
    Assert-True ($null -eq $pick2) 'roundFile: only known files -> null (MISS, not a stale read)'
    $pick3 = Select-RoundFile -Candidates @() -Known $known
    Assert-True ($null -eq $pick3) 'roundFile: empty directory -> null'

    # --- verdict table
    $baseDoc = @{
        'gpuFrameTime'             = @{ P50 = 3.0; Envelope = 0.10 }
        'cpuMainThreadFrameTime'   = @{ P50 = 3.5; Envelope = 0.12 }
        'cpuRenderThreadFrameTime' = @{ P50 = 0.75; Envelope = 0.20 }
        'setPassCalls'             = @{ P50 = 28.0; Envelope = 0.10 }
        'batches'                  = @{ P50 = 84.0; Envelope = 0.10 }
        'triangles'                = @{ P50 = 106738.0; Envelope = 0.10 }
    }
    $curOk = @{
        'gpuFrameTime'             = 3.05
        'cpuMainThreadFrameTime'   = 3.4
        'cpuRenderThreadFrameTime' = 0.74
        'setPassCalls'             = 28.0
        'batches'                  = 84.0
        'triangles'                = 106740.0
    }
    $rowsOk = Compare-FrameMetrics -Current $curOk -Baseline $baseDoc -MetricNames $names
    Assert-True ($rowsOk.Count -eq 6) 'compare: one row per gated metric'
    Assert-True (@($rowsOk | Where-Object { $_.Verdict -ne 'OK' }).Count -eq 0) 'compare: within envelope -> all OK'
    $curRed = @{}
    foreach ($k in $curOk.Keys) { $curRed[$k] = $curOk[$k] }
    $curRed['gpuFrameTime'] = 3.5
    $rowsRed = Compare-FrameMetrics -Current $curRed -Baseline $baseDoc -MetricNames $names
    $gpuRow = @($rowsRed | Where-Object { $_.Name -eq 'gpuFrameTime' })[0]
    Assert-True ($gpuRow.Verdict -eq 'RED') 'compare: +16.7% gpu vs 10% band -> RED'
    Assert-True ([math]::Abs($gpuRow.DeltaPct - (100.0 * 0.5 / 3.0)) -lt 1e-9) 'compare: delta percent computed vs baseline'
    $curMiss = @{}
    foreach ($k in $curOk.Keys) { $curMiss[$k] = $curOk[$k] }
    $curMiss.Remove('batches')
    $rowsMiss = Compare-FrameMetrics -Current $curMiss -Baseline $baseDoc -MetricNames $names
    Assert-True (@($rowsMiss | Where-Object { $_.Verdict -eq 'MISS' }).Count -eq 1) 'compare: missing current value -> one MISS'
    $thinBase = @{}
    foreach ($k in $baseDoc.Keys) { $thinBase[$k] = $baseDoc[$k] }
    $thinBase.Remove('triangles')
    $rowsNB = Compare-FrameMetrics -Current $curOk -Baseline $thinBase -MetricNames $names
    $triRow = @($rowsNB | Where-Object { $_.Name -eq 'triangles' })[0]
    Assert-True ($triRow.Verdict -eq 'MISS') 'compare: baseline missing a metric -> MISS (not NEW/OK)'

    # --- exit-code mapping
    Assert-True ((Get-CheckExitCode -RedCount 0 -MissCount 0 -GateMode $true) -eq 0) 'exit: clean gate run -> 0'
    Assert-True ((Get-CheckExitCode -RedCount 1 -MissCount 0 -GateMode $true) -eq 1) 'exit: RED in gate mode -> 1'
    Assert-True ((Get-CheckExitCode -RedCount 1 -MissCount 0 -GateMode $false) -eq 0) 'exit: RED in report-only -> 0 (reports, does not gate)'
    Assert-True ((Get-CheckExitCode -RedCount 0 -MissCount 1 -GateMode $true) -eq 2) 'exit: MISS in gate mode -> 2'
    Assert-True ((Get-CheckExitCode -RedCount 0 -MissCount 1 -GateMode $false) -eq 2) 'exit: MISS in report-only -> 2 (cannot prove != pass)'
    Assert-True ((Get-CheckExitCode -RedCount 3 -MissCount 2 -GateMode $true) -eq 2) 'exit: MISS outranks RED -> 2'

    # --- baseline reader
    $bText = @'
{
  "machine": "AMD Ryzen 7 7840HS / AMD Radeon 780M Graphics",
  "date": "2026-11-15",
  "metrics": {
    "gpuFrameTime":             { "p50": 3.12, "envelope": 0.1 },
    "cpuMainThreadFrameTime":   { "p50": 3.6,  "envelope": 0.12 },
    "cpuRenderThreadFrameTime": { "p50": 0.75, "envelope": 0.2 },
    "setPassCalls":             { "p50": 28,   "envelope": 0.1 },
    "batches":                  { "p50": 84,   "envelope": 0.1 },
    "triangles":                { "p50": 106738, "envelope": 0.1 }
  }
}
'@
    $bd = Read-BaselineDoc -Text $bText -MetricNames $names
    Assert-True ($bd.Ok -and $bd.Metrics['gpuFrameTime'].Envelope -eq 0.1) 'baseline: p50 + envelope read'
    Assert-True ($bd.Date -eq '2026-11-15') 'baseline: date read'
    $bNoEnvText = @'
{
  "machine": "AMD Ryzen 7 7840HS / AMD Radeon 780M Graphics",
  "date": "2026-11-15",
  "metrics": {
    "gpuFrameTime":             { "p50": 3.12 },
    "cpuMainThreadFrameTime":   { "p50": 3.6 },
    "cpuRenderThreadFrameTime": { "p50": 0.75 },
    "setPassCalls":             { "p50": 28 },
    "batches":                  { "p50": 84 },
    "triangles":                { "p50": 106738 }
  }
}
'@
    $bd2 = Read-BaselineDoc -Text $bNoEnvText -MetricNames $names
    Assert-True ($bd2.Ok -and $bd2.Metrics['gpuFrameTime'].Envelope -gt 0.0) 'baseline: missing envelope -> metric default (warned)'
    Assert-True ($bd2.Warnings.Count -ge 1) 'baseline: missing envelope is reported, not silent'
    $bZero = $bText.Replace('"p50": 3.12', '"p50": 0')
    $bd3 = Read-BaselineDoc -Text $bZero -MetricNames $names
    Assert-True (-not $bd3.Ok) 'baseline: p50=0 rejected (a zero baseline would pass anything)'

    Write-Host ''
    if ($failures -gt 0) {
        Write-Host ("SELFTEST FAIL ({0} failed)" -f $failures) -ForegroundColor Red
        exit 1
    }
    Write-Host 'SELFTEST PASS' -ForegroundColor Green
    exit 0
}

# ------------------------------------------------------------------ helpers --
# Competing processes that would poison the measurement. ZFrame = another
# player (including a leftover from a previous run), Unity = editor compiling
# or playing. Both steal GPU/CPU from the run we are about to measure.
function Get-CompetingProcesses {
    $found = @()
    foreach ($n in @('ZFrame', 'Unity')) {
        $procs = @(Get-Process -Name $n -ErrorAction SilentlyContinue)
        foreach ($p in $procs) {
            $found += [pscustomobject]@{ Name = $p.ProcessName; Id = $p.Id }
        }
    }
    return ,$found
}

# Snapshot of existing probe files (name + mtime) so a round can identify the
# file it produced instead of reading a neighbour's.
function Get-OutSnapshot {
    $items = @(Get-ChildItem -LiteralPath $OutDir -Filter 'frame-baseline-*.json' -File -ErrorAction SilentlyContinue |
        Select-Object Name, LastWriteTimeUtc)
    return ,$items
}

function Format-JsString([string]$s) {
    return '"' + ($s -replace '\\', '\\\\' -replace '"', '\"') + '"'
}

# JSON numbers must use a '.' decimal separator regardless of the host locale
# (a de-DE box would otherwise write "3,12" and produce an unparseable baseline).
function Format-JsNumber([double]$v, [int]$Decimals = 4) {
    $pattern = '0.' + ('#' * $Decimals)
    return $v.ToString($pattern, [System.Globalization.CultureInfo]::InvariantCulture)
}

# ------------------------------------------------------------------- main ----
function Main {
    New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
    $metricNames = @($Script:MetricDefs.Keys)

    if ($Rounds -lt 2) {
        Write-Host ("MISS: -Rounds must be >= 2 (first round is always discarded; got {0})" -f $Rounds) -ForegroundColor Red
        Write-Host 'BENCH_VERDICT: MISS (invalid round count)'
        return 2
    }
    if (-not (Test-Path -LiteralPath $PlayerExe)) {
        Write-Host ("MISS: player build not found: {0}" -f $PlayerExe) -ForegroundColor Red
        Write-Host '  build it first: powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_player.ps1' -ForegroundColor Yellow
        Write-Host 'BENCH_VERDICT: MISS (no player build - cannot prove no regression)'
        return 2
    }
    if (-not (Test-Path -LiteralPath $OutDir)) {
        Write-Host ("MISS: probe output dir not found: {0}" -f $OutDir) -ForegroundColor Red
        Write-Host 'BENCH_VERDICT: MISS (no probe output dir - cannot prove no regression)'
        return 2
    }

    # ---- environment guard: competing player/editor --------------------------
    $competing = Get-CompetingProcesses
    if ($competing.Count -gt 0) {
        Write-Host ("MISS: competing process already running: {0}" -f (($competing | ForEach-Object { '{0}(pid {1})' -f $_.Name, $_.Id }) -join ', ')) -ForegroundColor Red
        Write-Host '  a second player/editor shares the GPU and CPU and poisons every frame-time number - close it and re-run.' -ForegroundColor Yellow
        Write-Host 'BENCH_VERDICT: MISS (environment guard: competing process)'
        return 2
    }

    # ---- environment guard: machine quiet (reuses check_machine_quiet.ps1) ---
    $quietState = 'SKIPPED (SkipQuietCheck - ambient load not verified)'
    if (-not $SkipQuietCheck) {
        if (-not (Test-Path -LiteralPath $QuietScript)) {
            Write-Host ("MISS: {0} not found - cannot guarantee evidence quality" -f $QuietScript) -ForegroundColor Red
            Write-Host 'BENCH_VERDICT: MISS (quiet check unavailable - cannot prove no regression)'
            return 2
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
            if (-not $Force) {
                Write-Host '  the box is busy: frame-time numbers taken now are not comparable to any baseline.' -ForegroundColor Yellow
                Write-Host '  re-run when idle, or pass -Force to measure anyway (the baseline records that it was forced).' -ForegroundColor Yellow
                Write-Host 'BENCH_VERDICT: MISS (machine busy - refusing to measure; -Force to override)' -ForegroundColor Red
                return 2
            }
            Write-Host '  -Force given: measuring on a busy box; numbers are indicative only.' -ForegroundColor Yellow
            $quietState = 'FORCED (machine was busy)'
        } else {
            $quietState = 'QUIET'
        }
    }

    # ---- collection: N rounds, one new probe file each -----------------------
    $roundSets  = @()
    $roundFiles = @()
    $machine    = ''
    for ($i = 1; $i -le $Rounds; ++$i) {
        $competing = Get-CompetingProcesses
        if ($competing.Count -gt 0) {
            Write-Host ("MISS: competing process appeared before round {0}: {1}" -f $i, (($competing | ForEach-Object { '{0}(pid {1})' -f $_.Name, $_.Id }) -join ', ')) -ForegroundColor Red
            Write-Host 'BENCH_VERDICT: MISS (environment guard: competing process)'
            return 2
        }
        $known = @(Get-OutSnapshot | ForEach-Object { $_.Name })
        $playerLog = Join-Path $LogDir ("framebaseline_gate{0}.log" -f $i)
        $argv = @('-framebaseline-vsync0',
                  '-screen-width', '1920',
                  '-screen-height', '1080',
                  '-screen-fullscreen', '0',
                  '-logFile', $playerLog)
        Write-Host ("round {0}/{1}: ZFrame.exe {2}" -f $i, $Rounds, ($argv -join ' '))
        $code = -1
        try {
            $proc = Start-Process -FilePath $PlayerExe -ArgumentList $argv -WorkingDirectory $RepoRoot -PassThru
            if (-not $proc.WaitForExit($RoundTimeoutSec * 1000)) {
                try { $proc.Kill() } catch { }
                try { $proc.WaitForExit() } catch { }
                Write-Host ("MISS: round {0} exceeded {1}s and was killed (log: TempLogs/framebaseline_gate{0}.log)" -f $i, $RoundTimeoutSec) -ForegroundColor Red
                Write-Host 'BENCH_VERDICT: MISS (probe did not finish - cannot prove no regression)'
                return 2
            }
            $code = $proc.ExitCode
        } catch {
            Write-Host ("MISS: round {0} failed to start: {1}" -f $i, $_.Exception.Message) -ForegroundColor Red
            Write-Host 'BENCH_VERDICT: MISS (player would not launch)'
            return 2
        }
        if ($code -ne 0) {
            Write-Host ("MISS: round {0} exit={1} (0=JSON written, 4=aborted, 3=JSON not written; log: TempLogs/framebaseline_gate{0}.log)" -f $i, $code) -ForegroundColor Red
            Write-Host 'BENCH_VERDICT: MISS (probe run did not complete cleanly)'
            return 2
        }
        $pick = Select-RoundFile -Candidates (Get-OutSnapshot) -Known $known
        if ($null -eq $pick) {
            Write-Host ("MISS: round {0} wrote no new frame-baseline-*.json under {1}" -f $i, $OutDir) -ForegroundColor Red
            Write-Host 'BENCH_VERDICT: MISS (no probe JSON - cannot prove no regression)'
            return 2
        }
        $roundPath = Join-Path $OutDir $pick.Name
        try {
            $roundText = [System.IO.File]::ReadAllText($roundPath)
        } catch {
            Write-Host ("MISS: round {0} probe file unreadable: {1} ({2})" -f $i, $pick.Name, $_.Exception.Message) -ForegroundColor Red
            Write-Host 'BENCH_VERDICT: MISS (probe JSON unreadable)'
            return 2
        }
        $parsed = Parse-FrameBaseline -Text $roundText -MetricNames $metricNames -ExpectMode $ExpectedMode
        if (-not $parsed.Ok) {
            Write-Host ("MISS: round {0} probe JSON unusable ({1}): {2}" -f $i, $pick.Name, $parsed.Reason) -ForegroundColor Red
            Write-Host 'BENCH_VERDICT: MISS (probe JSON missing fields - cannot prove no regression)'
            return 2
        }
        $roundSets  += $parsed.Values
        $roundFiles += $pick.Name
        if (-not $machine) { $machine = $parsed.Machine }
        Write-Host ("  -> {0} (gpu p50={1}ms)" -f $pick.Name, $parsed.Values['gpuFrameTime']) -ForegroundColor DarkGray
        if ($i -lt $Rounds) { Start-Sleep -Seconds 1 }
    }

    # ---- aggregate: discard round 1, median of the rest ----------------------
    $keptSets = Get-KeptRoundSets -RoundSets $roundSets -DropFirst $true
    $agg = Get-Aggregate -RoundSets $keptSets -MetricNames $metricNames
    $incomplete = @($metricNames | Where-Object {
        $null -eq $agg.Metrics[$_] -or $agg.Metrics[$_].Count -ne $keptSets.Count
    })
    if ($incomplete.Count -gt 0) {
        Write-Host ("MISS: no usable p50 for: {0}" -f ($incomplete -join ', ')) -ForegroundColor Red
        Write-Host 'BENCH_VERDICT: MISS (partial metric set - cannot prove no regression)'
        return 2
    }
    $current = @{}
    foreach ($m in $metricNames) { $current[$m] = [double]$agg.Metrics[$m].P50 }

    # ---- -UpdateBaseline -----------------------------------------------------
    if ($UpdateBaseline) {
        $baselineDir = Split-Path -Parent $BaselinePath
        New-Item -ItemType Directory -Force -Path $baselineDir | Out-Null
        $today = Get-Date -Format 'yyyy-MM-dd'
        $lines = @('{', '  "schema": 1,')
        $lines += ('  "probe": ' + (Format-JsString 'frame-baseline') + ',')
        $lines += ('  "mode": ' + (Format-JsString $ExpectedMode) + ',')
        $lines += ('  "date": ' + (Format-JsString $today) + ',')
        $lines += ('  "machine": ' + (Format-JsString $machine) + ',')
        $lines += ('  "machine_quiet": ' + (Format-JsString $quietState) + ',')
        $lines += ('  "rounds": {0},' -f $Rounds)
        $lines += ('  "roundsKept": {0},' -f $keptSets.Count)
        $lines += '  "dropFirstRound": true,'
        $lines += ('  "criterion": ' + (Format-JsString 'per-metric p50 = median over the kept rounds (round 1 discarded); RED when current > baseline * (1 + envelope); exit 0 pass / 1 RED / 2 MISS') + ',')
        $lines += ('  "envelopeSource": ' + (Format-JsString 'frame-time baseline doc (Docs/Planning, 2026-11-15) regression criterion: GPU 10% / main 12% / render 20% (>=5 rounds, cross-session); count metrics 10% (deterministic per run, no registered envelope)') + ',')
        $lines += '  "metrics": {'
        $blocks = @()
        foreach ($m in $metricNames) {
            $def = $Script:MetricDefs[$m]
            $a = $agg.Metrics[$m]
            $b = @()
            $b += ('    ' + (Format-JsString $m) + ': {')
            $b += ('      "unit": ' + (Format-JsString ([string]$def.Unit)) + ',')
            $b += ('      "p50": {0},' -f (Format-JsNumber $a.P50))
            $b += ('      "envelope": {0},' -f (Format-JsNumber ([double]$def.Envelope)))
            $b += ('      "measuredMin": {0},' -f (Format-JsNumber $a.Min))
            $b += ('      "measuredMax": {0},' -f (Format-JsNumber $a.Max))
            $b += ('      "measuredSpreadPct": {0}' -f (Format-JsNumber $a.SpreadPct 2))
            $b += '    }'
            $blocks += ($b -join "`n")
        }
        $lines += ($blocks -join ",`n")
        $lines += '  },'
        $lines += '  "notes": ['
        $notes = @(
            ('rounds: {0} x -framebaseline-vsync0 (1920x1080 windowed, ~10s each), round 1 discarded (cold-start PSO bias)' -f $Rounds),
            ('probe files used: ' + ($roundFiles -join ', ')),
            'metric p50 = median of statistics.<metric>.p50 across the kept rounds (even count: lower middle)',
            'envelope = registered regression criterion; a fresh box/session invalidates it and needs a re-baseline',
            ('machine_quiet: ' + $quietState)
        )
        $qnotes = foreach ($n in $notes) { '    ' + (Format-JsString $n) }
        $lines += ($qnotes -join ",`n")
        $lines += '  ]'
        $lines += '}'
        $json = (($lines -join "`n") + "`n")
        [System.IO.File]::WriteAllText($BaselinePath, $json, (New-Object System.Text.UTF8Encoding($false)))
        Write-Host ''
        Write-Host ("baseline written: {0}" -f $BaselinePath) -ForegroundColor Green
        Write-Host ("  machine: {0}" -f $machine)
        Write-Host ("  kept rounds: {0} of {1} (first discarded)" -f $keptSets.Count, $Rounds)
        foreach ($m in $metricNames) {
            $a = $agg.Metrics[$m]
            Write-Host ("  {0,-26} p50={1}  spread={2}%" -f $m, $a.P50, [math]::Round($a.SpreadPct, 2))
        }
        Write-Host 'BENCH_VERDICT: BASELINE UPDATED'
        return 0
    }

    # ---- compare vs committed baseline --------------------------------------
    if (-not (Test-Path -LiteralPath $BaselinePath)) {
        Write-Host ("MISS: no baseline at {0}" -f $BaselinePath) -ForegroundColor Red
        Write-Host '  create it once on a quiet machine: powershell -NoProfile -ExecutionPolicy Bypass -File Tools/bench_player.ps1 -UpdateBaseline' -ForegroundColor Yellow
        Write-Host 'BENCH_VERDICT: MISS (no baseline - cannot prove no regression)'
        return 2
    }
    $baselineText = ''
    try {
        $baselineText = [System.IO.File]::ReadAllText($BaselinePath)
    } catch {
        Write-Host ("MISS: baseline unreadable: {0} ({1})" -f $BaselinePath, $_.Exception.Message) -ForegroundColor Red
        Write-Host 'BENCH_VERDICT: MISS (baseline unreadable - cannot prove no regression)'
        return 2
    }
    $bdoc = Read-BaselineDoc -Text $baselineText -MetricNames $metricNames
    if (-not $bdoc.Ok) {
        Write-Host ("MISS: baseline unusable ({0}): {1}" -f $BaselinePath, $bdoc.Reason) -ForegroundColor Red
        Write-Host 'BENCH_VERDICT: MISS (baseline broken - cannot prove no regression)'
        return 2
    }
    if ($bdoc.Machine -and $machine -and $bdoc.Machine -ne $machine) {
        Write-Host ("  [warn] baseline machine '{0}' != this machine '{1}' - cross-machine numbers are indicative only" -f $bdoc.Machine, $machine) -ForegroundColor Yellow
    }
    if ($bdoc.Date -and $bdoc.Date -ne (Get-Date -Format 'yyyy-MM-dd')) {
        Write-Host ("  [note] baseline date {0}" -f $bdoc.Date) -ForegroundColor DarkGray
    }
    foreach ($w in $bdoc.Warnings) { Write-Host ("  [warn] {0}" -f $w) -ForegroundColor Yellow }

    $rows = Compare-FrameMetrics -Current $current -Baseline $bdoc.Metrics -MetricNames $metricNames

    # ---- table (kept-round values in collection order, then the median) ------
    Write-Host ''
    Write-Host ("==== player frame regression ({0} cost rounds, first discarded, {1} kept) ====" -f $Rounds, $keptSets.Count)
    $tableRows = foreach ($r in $rows) {
        $vals = @()
        foreach ($s in $keptSets) { $vals += ('{0:0.###}' -f [double]$s[$r.Name]) }
        [pscustomobject]@{
            metric   = $r.Name
            rounds   = ($vals -join ' / ')
            median   = ('{0:0.####}' -f $r.Current)
            min      = ('{0:0.####}' -f $agg.Metrics[$r.Name].Min)
            max      = ('{0:0.####}' -f $agg.Metrics[$r.Name].Max)
            spread   = ('{0:0.00}%' -f $agg.Metrics[$r.Name].SpreadPct)
            baseline = ('{0:0.####}' -f $r.Baseline)
            env      = ('{0:0}%' -f (100.0 * $r.Envelope))
            delta    = if ($null -ne $r.DeltaPct) { '{0:+0.0;-0.0}%' -f $r.DeltaPct } else { 'n/a' }
            verdict  = $r.Verdict
        }
    }
    $tableRows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
    Write-Host ("  probe files: {0}" -f ($roundFiles -join ', ')) -ForegroundColor DarkGray

    $reds = @($rows | Where-Object { $_.Verdict -eq 'RED' })
    $miss = @($rows | Where-Object { $_.Verdict -eq 'MISS' })
    foreach ($r in $reds) {
        Write-Host ("  RED: {0}: baseline={1} current={2} delta={3}% (envelope {4}%)" -f `
            $r.Name, ('{0:0.####}' -f $r.Baseline), ('{0:0.####}' -f $r.Current), ('{0:+0.0;-0.0}' -f $r.DeltaPct), (100.0 * $r.Envelope)) -ForegroundColor Red
    }
    foreach ($r in $miss) {
        Write-Host ("  MISS: {0}: {1}" -f $r.Name, $r.Note) -ForegroundColor Red
    }

    $exitCode = Get-CheckExitCode -RedCount $reds.Count -MissCount $miss.Count -GateMode ([bool]$Gate)
    Write-Host ''
    if ($miss.Count -gt 0) {
        Write-Host ("BENCH_VERDICT: MISS ({0} metric(s) unprovable: {1}) - cannot prove no regression" -f $miss.Count, (($miss | ForEach-Object Name) -join ', ')) -ForegroundColor Red
    } elseif ($reds.Count -gt 0) {
        if ($Gate) {
            Write-Host ("BENCH_VERDICT: FAIL (gate on; {0} beyond envelope: {1})" -f $reds.Count, (($reds | ForEach-Object Name) -join ', ')) -ForegroundColor Red
        } else {
            Write-Host ("BENCH_VERDICT: REPORT-ONLY ({0} red, not gating: {1}; pass -Gate to enforce)" -f $reds.Count, (($reds | ForEach-Object Name) -join ', ')) -ForegroundColor Yellow
        }
    } else {
        Write-Host ("BENCH_VERDICT: PASS ({0}/{1} within envelope{2})" -f $rows.Count, $rows.Count, $(if ($Gate) { '; gate on' } else { '; report-only' })) -ForegroundColor Green
    }
    return $exitCode
}

exit (Main)
