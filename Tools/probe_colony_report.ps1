# =============================================================================
# probe_colony_report.ps1 - colony report panel UI regression gate (TASK A-COL-08)
#
# Why this file exists: the monthly report panel (ReportPanelView / ColonyWorldHost,
# TASK-A-COL-05/07) renders data-driven content every month; nothing proved the panel
# actually SHOWS that content. A screenshot with a "non-black pixels" check alone is a
# vacuous assertion (B-PLANET-15 lesson), so this gate is built on CONTENT ANCHORS:
# panel GameObject under the UIRoot Panel-layer canvas, rendered Summary rows that
# exactly match the view-model, >=1 amber-highlighted anomaly row with a real Button,
# a Raw toggle switch that changes the rendered row count through the paid intel-gate,
# plus pixel forensics on two captured PNGs (summary vs raw must genuinely differ).
#
# Pipeline:
#   The probe itself is a PlayMode test case
#   (UnityProject/Assets/Game/Tests/PlayMode/ColonyReportProbeTests.cs) that is
#   LAZILY ACTIVATED: it only executes when the Unity process is launched with
#   -acol08probe (FrameBaselineProbe lazy-activation pattern); in every other run
#   (editor play, full PlayMode suite) it Assert.Ignore-skips at zero cost.
#   This runner launches Unity (-runTests, -testFilter narrowed to the probe class,
#   -acol08probe), then compares the anchor JSON the probe wrote
#   (Tools/Probes/out/colony_report-<UTC>.json) against the committed baseline
#   Tools/Probes/baseline/colony-report-baseline.json.
#
# Gate criterion (the colony world is fixed-seed + deterministic domain, so content
# counts are exact-match, not envelope-compared):
#   * every numeric anchor must equal the baseline value exactly (content drift ->
#     RED -> re-baseline consciously via -UpdateBaseline, never silently);
#   * every boolean anchor must be true;
#   * image floors (runner constants): PNGs must be non-black, contain bright text
#     pixels and amber highlight pixels, and summary vs raw must differ.
#   Exit codes (stable contract - docs quote these; bench_player.ps1 semantics):
#   0  every anchor within criterion (report-only mode also returns 0 on RED)
#   1  at least one RED; only reachable with -Gate
#   2  MISS: no anchor JSON / JSON unusable / screenshots missing / environment
#      guard tripped. "Cannot prove no regression" counts as a regression, so MISS
#      never degrades to 0 and never fills a default value in.
#   Divergence from bench_player (registered): Unity -runTests exits non-zero when a
#   test assertion fails. When that happens AND the probe still wrote a fresh anchor
#   JSON, this runner compares the JSON normally (an anchor regression is RED exit 1
#   with -Gate, not MISS) - the JSON is complete evidence by design (the C# probe
#   writes artifacts BEFORE asserting). MISS is reserved for "no usable data".
#
# Modes:
#   default          report-only: probe run, compare, table, exit 0 unless MISS
#   -Gate            enforcement: exit 1 on any RED, exit 2 on any MISS
#   -UpdateBaseline  run the probe and rewrite the committed baseline JSON
#                    (anchor values + date + unity version); refused when any
#                    boolean anchor is false or an image floor is violated
#   -SelfTest        pure-function tests only (anchor parse / verdict / exit-code
#                    mapping / fresh-file selection / PNG checks). No Unity launch.
#                    exit 0/1.
#
# Environment discipline (HANDOFF.md; the user's editor may be open):
#   * Unity.exe slot: wait 60s x 10 rounds. Processes are classified by command
#     line (zombie verification): a batch-mode process on THIS project is a leftover
#     batch run and may be taskkilled after the wait window; anything else is
#     treated as the user's editor - NEVER killed, NEVER lock-deleted -> MISS after
#     10 rounds.
#   * UnityLockfile is only deleted when no Unity.exe process is running.
#   * Unity editor launches always go through Git Bash (licensing handshake dies
#     otherwise on this machine - run_tests.ps1 discipline #4).
#   * Test runs (-runTests) are launched WITHOUT -quit.
#   * Evidence: results.xml freshness fingerprint + the probe's own anchor JSON.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools/probe_colony_report.ps1
#   powershell ... -File Tools/probe_colony_report.ps1 -Gate
#   powershell ... -File Tools/probe_colony_report.ps1 -UpdateBaseline
#   powershell ... -File Tools/probe_colony_report.ps1 -SelfTest
# =============================================================================
param(
    [switch]$Gate,
    [switch]$UpdateBaseline,
    [switch]$SelfTest,
    [int]$WaitRounds = 10,
    [int]$WaitSecondsPerRound = 60,
    [int]$HardTimeoutSec = 1800
)

$ErrorActionPreference = 'Stop'

$RepoRoot     = Split-Path -Parent $PSScriptRoot
$UnityExe     = 'C:\Program Files\Unity 2022.3.62f3c1\Editor\Unity.exe'
$UnityProj    = Join-Path $RepoRoot 'UnityProject'
$OutDir       = Join-Path $PSScriptRoot 'Probes\out'
$BaselinePath = Join-Path $PSScriptRoot 'Probes\baseline\colony-report-baseline.json'
$LogDir       = Join-Path $RepoRoot 'TempLogs'
$ResultsXml   = Join-Path $LogDir 'probe_colony_report.xml'
$UnityLog     = Join-Path $LogDir 'probe_colony_report.log'
$ProbeClass   = 'Game.View.PlayMode.Tests.ColonyReportProbeTests'
$ProbeFlag    = '-acol08probe'

# The gated anchor set. Dotted paths into the probe's anchor JSON. Counts are
# exact-match (fixed seed 20261122 + deterministic domain => same numbers every
# run on the same content; a change means content/panel drifted and needs a
# conscious -UpdateBaseline). Booleans must all be true.
$Script:NumericAnchors = @(
    'month',
    'summary.rowsVm', 'summary.rowsRendered', 'summary.exactMatchCount',
    'summary.anomalyLinesVm', 'summary.anomalyRowsRendered',
    'summary.anomalyHighlightedRows', 'summary.anomalyButtons',
    'raw.rowsVm', 'raw.rowsRendered', 'raw.delta'
)
$Script:BooleanAnchors = @(
    'panel.exists', 'panel.underPanelLayerCanvas', 'panel.onStack', 'panel.visible', 'panel.built',
    'summary.rowsRenderedEqualsVm', 'summary.renderedNonEmpty',
    'raw.rowsRenderedEqualsVm', 'raw.rowCountChanged', 'raw.toggleInteractable',
    'raw.paidGateConsumedIntel'
)

# Image floors (pixel forensics - "cannot be all black" is NOT the criterion; these
# prove text/highlight/difference content in the captures themselves). Calibrated
# 2026-11-23 on 780M/1280x720 panel captures; floors are far below measured values.
$Script:ImageFloors = [ordered]@{
    'images.summaryNonBlackRatio'  = 0.02
    'images.summaryBrightPixels'   = 50
    'images.summaryAmberPixels'    = 10
    'images.rawNonBlackRatio'      = 0.02
    'images.rawBrightPixels'       = 50
    'images.summaryVsRawDiffRatio' = 0.005
}

# ---------------------------------------------------------------- pure fns ---
# Resolve a dotted path ('summary.rowsVm') against a parsed JSON object. Two
# document shapes are supported: nested (the probe's own anchor JSON) and flat
# with literal dotted key names (the baseline's content node). A whole-path
# literal key wins first; dotted navigation is the fallback. Returns $null when
# any hop is missing (caller turns that into MISS).
function Get-AnchorValue {
    param([object]$Json, [string]$Path)
    if ($null -eq $Json -or -not $Path) { return $null }
    $literal = $Json.PSObject.Properties[$Path]
    if ($literal) { return $literal.Value }
    $node = $Json
    foreach ($seg in $Path.Split('.')) {
        if ($null -eq $node) { return $null }
        $prop = $node.PSObject.Properties[$seg]
        if ($null -eq $prop) { return $null }
        $node = $prop.Value
    }
    return $node
}

# Parse one probe anchor JSON (pure - text in, no file IO).
# Ok=$false carries a human Reason; the caller turns that into MISS (exit 2).
function Parse-AnchorJson {
    param([string]$Text)
    $res = [pscustomobject]@{
        Ok       = $false
        Reason   = ''
        Json     = $null
        Status   = ''
        Probe    = ''
    }
    if (-not $Text) { $res.Reason = 'empty anchor file'; return $res }
    try {
        $o = $Text | ConvertFrom-Json
    } catch {
        $res.Reason = ('invalid JSON: {0}' -f $_.Exception.Message)
        return $res
    }
    if ($null -eq $o) { $res.Reason = 'JSON parsed to null'; return $res }
    $res.Probe  = [string](Get-AnchorValue -Json $o -Path 'probe')
    $res.Status = [string](Get-AnchorValue -Json $o -Path 'status')
    if ($res.Probe -ne 'colony-report') {
        $res.Reason = ("probe != 'colony-report' (got '{0}')" -f $res.Probe)
        return $res
    }
    if ($res.Status -ne 'ok') {
        $res.Reason = ("status '{0}' != 'ok' (aborted probe carries incomplete anchors)" -f $res.Status)
        return $res
    }
    $res.Json = $o
    $res.Ok = $true
    return $res
}

# One numeric anchor comparison (pure). Exact match: any difference is RED.
function Test-NumericAnchor {
    param([double]$Baseline, [double]$Current)
    return $Baseline -eq $Current
}

# One image floor judgment (pure): current must be >= floor.
function Test-AboveFloor {
    param([double]$Floor, [double]$Current)
    return $Current -ge $Floor
}

# Compare current anchors vs baseline (pure). One row per checked anchor:
#   OK / RED / MISS. MISS covers "no current value" and "no baseline value" -
# both mean the gate cannot prove anything, and this gate treats that as
# failure rather than as a pass.
function Compare-AnchorSets {
    param(
        [object]$Current,          # parsed probe JSON
        [object]$Baseline,         # parsed baseline JSON (content node)
        [string[]]$NumericPaths,
        [string[]]$BooleanPaths,
        [System.Collections.IDictionary]$ImageFloors
    )
    $rows = @()
    # PS5.1 ConvertFrom-Json yields Int32/Int64 for integers and DECIMAL for
    # fractional numbers - all four are acceptable numeric anchor values.
    function Test-IsNumeric([object]$v) {
        return ($null -ne $v) -and ($v -is [double] -or $v -is [single] -or $v -is [decimal] -or $v -is [int] -or $v -is [long])
    }
    foreach ($path in $NumericPaths) {
        $cur = Get-AnchorValue -Json $Current -Path $path
        $base = Get-AnchorValue -Json $Baseline -Path $path
        $verdict = 'OK'
        $note = ''
        if (-not (Test-IsNumeric $cur)) {
            $verdict = 'MISS'; $note = 'no current value'
        } elseif (-not (Test-IsNumeric $base)) {
            $verdict = 'MISS'; $note = 'no baseline value'
        } elseif (-not (Test-NumericAnchor -Baseline ([double]$base) -Current ([double]$cur))) {
            $verdict = 'RED'; $note = ('baseline={0} current={1}' -f $base, $cur)
        }
        $rows += [pscustomobject]@{ Anchor = $path; Kind = 'numeric'; Baseline = $base; Current = $cur; Verdict = $verdict; Note = $note }
    }
    foreach ($path in $BooleanPaths) {
        $cur = Get-AnchorValue -Json $Current -Path $path
        $verdict = 'OK'
        $note = ''
        if ($null -eq $cur -or -not ($cur -is [bool])) {
            $verdict = 'MISS'; $note = 'no current value'
        } elseif ([bool]$cur -ne $true) {
            $verdict = 'RED'; $note = 'anchor false (panel content regression)'
        }
        $rows += [pscustomobject]@{ Anchor = $path; Kind = 'boolean'; Baseline = $true; Current = $cur; Verdict = $verdict; Note = $note }
    }
    foreach ($path in $ImageFloors.Keys) {
        $floor = [double]$ImageFloors[$path]
        $cur = Get-AnchorValue -Json $Current -Path $path
        $verdict = 'OK'
        $note = ('floor={0}' -f $floor)
        if (-not (Test-IsNumeric $cur)) {
            $verdict = 'MISS'; $note = 'no current value'
        } elseif (-not (Test-AboveFloor -Floor $floor -Current ([double]$cur))) {
            $verdict = 'RED'; $note = ('current={0} below floor={1}' -f $cur, $floor)
        }
        $rows += [pscustomobject]@{ Anchor = $path; Kind = 'imageFloor'; Baseline = $floor; Current = $cur; Verdict = $verdict; Note = $note }
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

# Pick this round's anchor file: the newest-mtime colony_report-*.json that did
# not exist before the launch (pure over candidate objects).
function Select-RoundFile {
    param(
        [object[]]$Candidates,   # each: @{ Name = ...; LastWriteTimeUtc = [datetime] }
        [string[]]$Known
    )
    $fresh = @($Candidates | Where-Object { $Known -notcontains $_.Name })
    if ($fresh.Count -eq 0) { return $null }
    $sorted = @($fresh | Sort-Object LastWriteTimeUtc -Descending)
    return $sorted[0]
}

# PNG evidence checks (pure over probe-record paths + a dir). The JSON must
# reference two PNGs that exist and are non-empty, else this run proves nothing.
function Test-PngEvidence {
    param([object]$Json, [string]$OutDir)
    $res = [pscustomobject]@{ Ok = $false; Reason = '' }
    $names = @('images.summaryPng', 'images.rawPng')
    foreach ($p in $names) {
        $name = Get-AnchorValue -Json $Json -Path $p
        if (-not $name) { $res.Reason = ("{0} absent in anchor JSON" -f $p); return $res }
        if (($name -split '[\\/]').Count -gt 1 -or $name -match '\.\.') {
            $res.Reason = ("{0} is not a bare file name: {1}" -f $p, $name); return $res
        }
        $full = Join-Path $OutDir $name
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            $res.Reason = ("screenshot missing: {0}" -f $full); return $res
        }
        $len = (Get-Item -LiteralPath $full).Length
        if ($len -le 0) { $res.Reason = ("screenshot empty: {0}" -f $full); return $res }
    }
    $res.Ok = $true
    return $res
}

# ------------------------------------------------------------ self test -----
if ($SelfTest) {
    Write-Host '== probe_colony_report self-test (pure functions, no Unity launched) =='
    $failures = 0
    function Assert-True([bool]$Cond, [string]$Name) {
        if ($Cond) { Write-Host ("  ok    {0}" -f $Name) -ForegroundColor Gray }
        else {
            Write-Host ("  FAIL  {0}" -f $Name) -ForegroundColor Red
            $script:failures++
        }
    }

    # --- anchor path resolution
    $j = @{ month = 1 } | ConvertTo-Json | ConvertFrom-Json
    Assert-True ((Get-AnchorValue -Json $j -Path 'month') -eq 1) 'path: top-level numeric resolved'
    Assert-True ($null -eq (Get-AnchorValue -Json $j -Path 'summary.rowsVm')) 'path: missing hop -> null (caller MISS)'
    Assert-True ($null -eq (Get-AnchorValue -Json $null -Path 'month')) 'path: null json -> null'
    # flat document with literal dotted key names (baseline content node shape)
    $flat = '{ "content": { "summary.rowsVm": 7, "raw.delta": 16 } }' | ConvertFrom-Json
    Assert-True ((Get-AnchorValue -Json $flat.content -Path 'summary.rowsVm') -eq 7) 'path: literal dotted key (flat baseline) resolved'
    Assert-True ($null -eq (Get-AnchorValue -Json $flat.content -Path 'summary.rowsNope')) 'path: literal miss -> null'

    # --- probe JSON parsing
    $goodText = @'
{
  "probe": "colony-report",
  "status": "ok",
  "generatedAtUtc": "2026-11-23T08-00-00Z",
  "month": 1,
  "panel": { "exists": true, "underPanelLayerCanvas": true, "onStack": true, "visible": true, "built": true },
  "summary": { "rowsVm": 12, "rowsRendered": 12, "rowsRenderedEqualsVm": true, "renderedNonEmpty": true,
               "exactMatchCount": 12, "anomalyLinesVm": 3, "anomalyRowsRendered": 3,
               "anomalyHighlightedRows": 3, "anomalyButtons": 3 },
  "raw": { "rowsVm": 104, "rowsRendered": 104, "rowsRenderedEqualsVm": true, "rowCountChanged": true,
           "delta": 92, "toggleInteractable": true, "intelBefore": 3, "intelAfter": 2,
           "paidGateConsumedIntel": true },
  "images": { "summaryPng": "colony_report-x-summary.png", "rawPng": "colony_report-x-raw.png",
              "summaryNonBlackRatio": 0.97, "summaryBrightPixels": 4100, "summaryAmberPixels": 640,
              "rawNonBlackRatio": 0.98, "rawBrightPixels": 9800, "rawAmberPixels": 0,
              "summaryVsRawDiffRatio": 0.33 }
}
'@
    $parsed = Parse-AnchorJson -Text $goodText
    Assert-True $parsed.Ok 'parse: complete anchor JSON accepted'
    Assert-True ((Get-AnchorValue -Json $parsed.Json -Path 'summary.rowsVm') -eq 12) 'parse: nested value readable'
    Assert-True ((Get-AnchorValue -Json $parsed.Json -Path 'images.summaryVsRawDiffRatio') -eq 0.33) 'parse: image metric readable'

    $aborted = $goodText.Replace('"status": "ok"', '"status": "aborted"')
    $pa = Parse-AnchorJson -Text $aborted
    Assert-True (-not $pa.Ok -and $pa.Reason -match 'aborted') 'parse: aborted status rejected (incomplete anchors -> MISS)'
    $badProbe = $goodText.Replace('"probe": "colony-report"', '"probe": "other"')
    $pp = Parse-AnchorJson -Text $badProbe
    Assert-True (-not $pp.Ok -and $pp.Reason -match 'colony-report') 'parse: wrong probe id rejected'
    $bad = Parse-AnchorJson -Text 'PROBE_JSON {not valid'
    Assert-True (-not $bad.Ok) 'parse: garbage text -> not Ok (never throws into the gate)'
    $empty = Parse-AnchorJson -Text ''
    Assert-True (-not $empty.Ok) 'parse: empty file -> not Ok'

    # --- baseline node for comparisons
    $baseText = @'
{
  "schema": 1, "probe": "colony-report", "date": "2026-11-23",
  "content": {
    "month": 1,
    "panel": { "exists": true },
    "summary": { "rowsVm": 12, "rowsRendered": 12, "exactMatchCount": 12, "anomalyLinesVm": 3,
                 "anomalyRowsRendered": 3, "anomalyHighlightedRows": 3, "anomalyButtons": 3 },
    "raw": { "rowsVm": 104, "rowsRendered": 104, "delta": 92 }
  }
}
'@
    $baseline = (ConvertFrom-Json $baseText).content
    Assert-True ($null -ne $baseline) 'baseline: content node resolved for comparisons'

    # --- verdicts: exact-match counts
    $rowsOk = Compare-AnchorSets -Current $parsed.Json -Baseline $baseline `
        -NumericPaths @('month', 'summary.rowsVm', 'raw.delta') -BooleanPaths @() -ImageFloors @{}
    Assert-True (@($rowsOk | Where-Object { $_.Verdict -ne 'OK' }).Count -eq 0) 'compare: equal counts -> OK'

    $rowsRed = Compare-AnchorSets -Current $parsed.Json -Baseline $baseline `
        -NumericPaths @('summary.rowsVm') -BooleanPaths @() -ImageFloors @{}
    Assert-True ($rowsRed[0].Verdict -eq 'OK') 'compare: same count -> OK (exact match)'
    $mutant = $goodText.Replace('"rowsVm": 12', '"rowsVm": 9') | ConvertFrom-Json
    $rowsRed2 = Compare-AnchorSets -Current $mutant -Baseline $baseline `
        -NumericPaths @('summary.rowsVm') -BooleanPaths @() -ImageFloors @{}
    Assert-True ($rowsRed2[0].Verdict -eq 'RED') 'compare: count drift -> RED (exact-match criterion)'
    Assert-True ($rowsRed2[0].Note -match 'baseline=12 current=9') 'compare: RED note names both values'

    # --- verdicts: booleans
    $rowsBool = Compare-AnchorSets -Current $parsed.Json -Baseline $baseline `
        -NumericPaths @() -BooleanPaths @('panel.exists', 'summary.renderedNonEmpty') -ImageFloors @{}
    Assert-True (@($rowsBool | Where-Object { $_.Verdict -ne 'OK' }).Count -eq 0) 'compare: true booleans -> OK'
    $boolMutant = $goodText.Replace('"renderedNonEmpty": true', '"renderedNonEmpty": false') | ConvertFrom-Json
    $rowsBoolRed = Compare-AnchorSets -Current $boolMutant -Baseline $baseline `
        -NumericPaths @() -BooleanPaths @('summary.renderedNonEmpty') -ImageFloors @{}
    Assert-True ($rowsBoolRed[0].Verdict -eq 'RED') 'compare: false boolean anchor -> RED'
    $rowsBoolMiss = Compare-AnchorSets -Current $parsed.Json -Baseline $baseline `
        -NumericPaths @() -BooleanPaths @('panel.nonexistent') -ImageFloors @{}
    Assert-True ($rowsBoolMiss[0].Verdict -eq 'MISS') 'compare: missing boolean -> MISS (not OK)'

    # --- verdicts: image floors
    $rowsFloor = Compare-AnchorSets -Current $parsed.Json -Baseline $baseline `
        -NumericPaths @() -BooleanPaths @() `
        -ImageFloors ([ordered]@{ 'images.summaryBrightPixels' = 50; 'images.summaryAmberPixels' = 10 })
    Assert-True (@($rowsFloor | Where-Object { $_.Verdict -ne 'OK' }).Count -eq 0) 'compare: measured above floors -> OK'
    $floorMutant = $goodText.Replace('"summaryAmberPixels": 640', '"summaryAmberPixels": 3') | ConvertFrom-Json
    $rowsFloorRed = Compare-AnchorSets -Current $floorMutant -Baseline $baseline `
        -NumericPaths @() -BooleanPaths @() `
        -ImageFloors ([ordered]@{ 'images.summaryAmberPixels' = 10 })
    Assert-True ($rowsFloorRed[0].Verdict -eq 'RED') 'compare: highlight pixels below floor -> RED (non-vacuous screenshot)'
    # PS5.1 ConvertFrom-Json yields [decimal] for fractional JSON numbers - the
    # floor/compare paths must accept them (this was a real false-MISS bug class).
    $rowsFloorDec = Compare-AnchorSets -Current $parsed.Json -Baseline $baseline `
        -NumericPaths @() -BooleanPaths @() `
        -ImageFloors ([ordered]@{ 'images.summaryVsRawDiffRatio' = 0.005 })
    Assert-True (@($rowsFloorDec | Where-Object { $_.Verdict -ne 'OK' }).Count -eq 0) 'compare: decimal-typed metric judged OK (PS5.1 decimal, not false-MISS)'
    $rowsFloorMiss = Compare-AnchorSets -Current $parsed.Json -Baseline $baseline `
        -NumericPaths @() -BooleanPaths @() `
        -ImageFloors ([ordered]@{ 'images.nope' = 1 })
    Assert-True ($rowsFloorMiss[0].Verdict -eq 'MISS') 'compare: missing image metric -> MISS'

    # --- numeric anchor judgment boundary
    Assert-True (Test-NumericAnchor -Baseline 12 -Current 12) 'numeric: equal passes'
    Assert-True (-not (Test-NumericAnchor -Baseline 12 -Current 12.0001)) 'numeric: 0.0001 drift is RED (exact criterion)'

    # --- fresh-file selection (newest mtime wins, known files excluded)
    $t0 = [datetime]::new(2026, 11, 23, 8, 0, 0, [System.DateTimeKind]::Utc)
    $known = @('colony_report-old.json')
    $cands = @(
        [pscustomobject]@{ Name = 'colony_report-old.json'; LastWriteTimeUtc = $t0.AddMinutes(-5) },
        [pscustomobject]@{ Name = 'colony_report-newB.json'; LastWriteTimeUtc = $t0.AddSeconds(20) },
        [pscustomobject]@{ Name = 'colony_report-newA.json'; LastWriteTimeUtc = $t0.AddSeconds(10) },
        [pscustomobject]@{ Name = 'colony_report-old2.json'; LastWriteTimeUtc = $t0.AddSeconds(1) }
    )
    $pick = Select-RoundFile -Candidates $cands -Known $known
    Assert-True ($null -ne $pick -and $pick.Name -eq 'colony_report-newB.json') 'roundFile: newest fresh file wins'
    $pick2 = Select-RoundFile -Candidates @($cands[0]) -Known $known
    Assert-True ($null -eq $pick2) 'roundFile: only known files -> null (MISS, not a stale read)'
    $pick3 = Select-RoundFile -Candidates @() -Known $known
    Assert-True ($null -eq $pick3) 'roundFile: empty directory -> null'

    # --- PNG evidence checks (temp dir)
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("probe-selftest-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    try {
        [System.IO.File]::WriteAllText((Join-Path $tmp 'a.png'), 'png-bytes')
        $pngOk = Test-PngEvidence -Json $parsed.Json -OutDir $tmp
        # parsed.Json references summaryPng colony_report-x-summary.png (missing in tmp) -> must fail
        Assert-True (-not $pngOk.Ok -and $pngOk.Reason -match 'missing') 'png: referenced screenshot absent -> not Ok (cannot prove)'
        $fixed = $goodText.Replace('colony_report-x-summary.png', 'a.png').Replace('colony_report-x-raw.png', 'b.png') | ConvertFrom-Json
        $pngMiss = Test-PngEvidence -Json $fixed -OutDir $tmp
        Assert-True (-not $pngMiss.Ok) 'png: second screenshot absent -> not Ok'
        [System.IO.File]::WriteAllText((Join-Path $tmp 'b.png'), 'png-bytes')
        $pngGood = Test-PngEvidence -Json $fixed -OutDir $tmp
        Assert-True $pngGood.Ok 'png: both referenced screenshots present+nonempty -> Ok'
        $escape = $goodText.Replace('colony_report-x-summary.png', 'sub\\evil.png') | ConvertFrom-Json
        $pngEsc = Test-PngEvidence -Json $escape -OutDir $tmp
        Assert-True (-not $pngEsc.Ok -and $pngEsc.Reason -match 'bare file name') 'png: path escape rejected (out-dir boundary)'
    } finally {
        Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }

    # --- exit-code mapping
    Assert-True ((Get-CheckExitCode -RedCount 0 -MissCount 0 -GateMode $true) -eq 0) 'exit: clean gate run -> 0'
    Assert-True ((Get-CheckExitCode -RedCount 1 -MissCount 0 -GateMode $true) -eq 1) 'exit: RED in gate mode -> 1'
    Assert-True ((Get-CheckExitCode -RedCount 1 -MissCount 0 -GateMode $false) -eq 0) 'exit: RED in report-only -> 0 (reports, does not gate)'
    Assert-True ((Get-CheckExitCode -RedCount 0 -MissCount 1 -GateMode $true) -eq 2) 'exit: MISS in gate mode -> 2'
    Assert-True ((Get-CheckExitCode -RedCount 0 -MissCount 1 -GateMode $false) -eq 2) 'exit: MISS in report-only -> 2 (cannot prove != pass)'
    Assert-True ((Get-CheckExitCode -RedCount 3 -MissCount 2 -GateMode $true) -eq 2) 'exit: MISS outranks RED -> 2'

    Write-Host ''
    if ($failures -gt 0) {
        Write-Host ("SELFTEST FAIL ({0} failed)" -f $failures) -ForegroundColor Red
        exit 1
    }
    Write-Host 'SELFTEST PASS' -ForegroundColor Green
    exit 0
}

# ------------------------------------------------------------------ helpers --
# Unity.exe slot check with zombie verification by command line. The user's editor
# may be open: wait (never kill, never delete its lock). A batch-mode process on
# THIS project is a leftover batch run: after the wait window it may be taskkilled
# (HANDOFF discipline: zombie verified via command line first).
function Get-UnityProcesses {
    return @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction SilentlyContinue)
}

function Get-UnityClass {
    param([object]$Proc)
    # our own launches carry forward-slash paths (Git Bash) while $RepoRoot is
    # backslashed - normalize before matching (2026-11-23: this exact mismatch made
    # our own zombie batches classify as user-or-unknown and never get killed)
    $cmdline = ([string]$Proc.CommandLine).Replace('/', '\')
    if ($cmdline -match '-batchmode' -and $cmdline -match [regex]::Escape($RepoRoot)) {
        # batch on this project - but it may be a PARALLEL AGENT'S ACTIVE RUN, not a
        # zombie. Zombie verdict needs the file-silence window (HANDOFF: poll the
        # other's output; xml present = finished; log stale >5min = stalled):
        #   active-batch  = its -logFile was written to within the last 5 minutes
        #   zombie-batch  = batch whose log has been silent >5 minutes (or no log arg)
        if (Test-BatchLogActive -Proc $Proc) { return 'active-batch' }
        return 'zombie-batch'
    }
    return 'user-or-unknown'    # user editor / foreign project - NEVER kill
}

# True when the batch's own -logFile (parsed from its command line) was touched
# within the last 5 minutes - i.e. it is an ACTIVE run that must never be killed.
function Test-BatchLogActive {
    param([object]$Proc)
    $cmdline = [string]$Proc.CommandLine
    # quoted form first, then unquoted (bash strips quotes; capture stops at whitespace)
    $m = [regex]::Match($cmdline, '(?i)-logFile\s+"([^"]+)"')
    if (-not $m.Success) {
        $m = [regex]::Match($cmdline, '(?i)-logFile\s+([^\s]+)')
    }
    if (-not $m.Success) { return $false }
    $logPath = $m.Groups[1].Value
    if (-not $logPath) { return $false }
    try {
        if (-not [System.IO.Path]::IsPathRooted($logPath)) {
            # batch CWD is unknown here - try repo root, then project dir
            $cand = Join-Path $RepoRoot $logPath
            if (-not (Test-Path -LiteralPath $cand -PathType Leaf)) {
                $cand = Join-Path $UnityProj $logPath
            }
            $logPath = $cand
        }
        $full = [System.IO.Path]::GetFullPath($logPath)
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { return $false }
        $ageSec = ((Get-Date) - (Get-Item -LiteralPath $full).LastWriteTime).TotalSeconds
        return ($ageSec -lt 300)
    } catch { return $false }
}

function Wait-UnitySlot {
    # 60s x N rounds: an active batch (possibly a parallel agent's legitimate run)
    # gets the full wait window; only zombie batches (cmdline-verified batchmode on
    # this project) that SURVIVE the whole window are killed; anything else is the
    # user's editor - never killed, gate MISSes instead.
    for ($round = 1; $round -le $WaitRounds; ++$round) {
        $procs = Get-UnityProcesses
        if ($procs.Count -eq 0) { return $null }
        Write-Host ("  unity slot busy (round {0}/{1}): {2}" -f $round, $WaitRounds,
            (($procs | ForEach-Object { 'pid {0} [{1}]' -f $_.ProcessId, (Get-UnityClass -Proc $_) }) -join ', ')) -ForegroundColor Yellow
        Start-Sleep -Seconds $WaitSecondsPerRound
    }
    return (Get-UnityProcesses)
}

function Remove-UnityLock {
    # only called when no Unity.exe process is running (caller enforces)
    $lock = Join-Path $UnityProj 'Temp\UnityLockfile'
    if (-not (Test-Path -LiteralPath $lock)) { return $true }
    try {
        Remove-Item -LiteralPath $lock -Force -Confirm:$false -ErrorAction Stop
        Write-Host '  [discipline] deleted UnityProject/Temp/UnityLockfile (no Unity process running)'
        return $true
    } catch {
        Write-Host ("  [warn] UnityLockfile could not be deleted: {0}" -f $_.Exception.Message) -ForegroundColor Yellow
        return $false
    }
}

function Get-OutSnapshot {
    $items = @(Get-ChildItem -LiteralPath $OutDir -Filter 'colony_report-*.json' -File -ErrorAction SilentlyContinue |
        Select-Object Name, LastWriteTimeUtc)
    return ,$items
}

# Launch Unity through Git Bash (run_tests.ps1 discipline: PowerShell toolchain
# launch dies in the licensing handshake on this machine). Poll the UNITY_EXIT_CODE
# marker (NOT the bash wrapper's lifetime - the Git bin/bash wrapper was observed
# dying while usr/bin/bash + Unity carried on, 2026-11-23), and kill only a
# verified zombie batch (cmdline batchmode + this project + log silent >5 min)
# after the xml grace window. Per-run unique .sh/.out/.err names - shared names
# race with an orphaned previous launch's still-open redirects.
function Invoke-ProbeRun {
    $bashCandidates = @()
    if ($env:ProgramFiles) {
        $bashCandidates += (Join-Path $env:ProgramFiles 'Git\bin\bash.exe')
        $bashCandidates += (Join-Path $env:ProgramFiles 'Git\usr\bin\bash.exe')
    }
    $bashCandidates += 'C:\Program Files\Git\bin\bash.exe'
    $bashExe = $null
    foreach ($c in $bashCandidates) { if (Test-Path $c) { $bashExe = $c; break } }
    if (-not $bashExe) { throw 'Git Bash not found (Unity editor launches must go through Git Bash on this machine)' }

    # previous results.xml must not be mistaken for this run's evidence
    $markerBaseline = $null
    if (Test-Path -LiteralPath $ResultsXml -PathType Leaf) {
        $item = Get-Item -LiteralPath $ResultsXml
        $markerBaseline = [pscustomobject]@{ LastWriteUtc = $item.LastWriteTimeUtc; Length = $item.Length }
        try { Remove-Item -LiteralPath $ResultsXml -Force -Confirm:$false -ErrorAction Stop; $markerBaseline = $null } catch { }
    }

    $projFwd = $UnityProj.Replace('\', '/')
    $xmlFwd  = $ResultsXml.Replace('\', '/')
    $logFwd  = $UnityLog.Replace('\', '/')
    $stamp   = Get-Date -Format 'yyyyMMdd-HHmmss'
    $shPath  = Join-Path $LogDir ("probe_colony_launch-{0}.sh" -f $stamp)
    $outPath = Join-Path $LogDir ("probe_colony_launch-{0}.out" -f $stamp)
    $errPath = Join-Path $LogDir ("probe_colony_launch-{0}.err" -f $stamp)
    $sh = "`"$($UnityExe.Replace('\', '/'))`" -batchmode -projectPath `"$projFwd`" -runTests -testPlatform PlayMode " +
          "-testResults `"$xmlFwd`" -logFile `"$logFwd`" -testFilter `"$ProbeClass`" $ProbeFlag`n"
    $sh += 'rc=$?' + "`n"
    $sh += 'echo UNITY_EXIT_CODE=$rc' + "`n"
    [System.IO.File]::WriteAllText($shPath, $sh, (New-Object System.Text.UTF8Encoding($false)))

    Write-Host ("  launching Unity via Git Bash: -testFilter {0} {1}" -f $ProbeClass, $ProbeFlag)
    Start-Process -FilePath $bashExe -ArgumentList ($shPath.Replace('\', '/')) `
        -RedirectStandardOutput $outPath -RedirectStandardError $errPath `
        -WindowStyle Hidden | Out-Null

    # Poll the exit-code marker, not process handles: xml-grace zombie kill + hard
    # timeout both live inside the same loop, so a hung Unity shutdown (observed:
    # 3 of 4 batch runs on this box hang after the log's shutdown lines) is killed
    # by cmdline-verified zombie discipline and the bash chain then echoes the marker.
    $start = Get-Date
    $xmlSeen = $null
    $code = $null
    while ($true) {
        if (Test-Path -LiteralPath $outPath -PathType Leaf) {
            $m = Select-String -Path $outPath -Pattern 'UNITY_EXIT_CODE=(-?\d+)' | Select-Object -Last 1
            if ($m) { $code = [int]$m.Matches[0].Groups[1].Value; break }
        }
        if (($null -eq $xmlSeen) -and (Test-Path -LiteralPath $ResultsXml -PathType Leaf)) {
            $fresh = $true
            if ($markerBaseline) {
                $item = Get-Item -LiteralPath $ResultsXml
                $fresh = ($item.LastWriteTimeUtc -ne $markerBaseline.LastWriteUtc) -or ($item.Length -ne $markerBaseline.Length)
            }
            if ($fresh) {
                $xmlSeen = Get-Date
                Write-Host "  [discipline] results.xml appeared; grace window 300s for Unity to exit"
            }
        }
        if ($xmlSeen -and (((Get-Date) - $xmlSeen).TotalSeconds -gt 300)) {
            $zombies = @(Get-UnityProcesses | Where-Object { (Get-UnityClass -Proc $_) -eq 'zombie-batch' })
            if ($zombies.Count -gt 0) {
                Write-Host ("  [discipline] xml present but zombie batch alive >300s -> taskkill (cmdline verified: pid {0})" -f (($zombies | ForEach-Object ProcessId) -join ',')) -ForegroundColor Yellow
                foreach ($p in $zombies) { taskkill /PID $p.ProcessId /F 2>&1 | Out-Null }
            }
        }
        if (((Get-Date) - $start).TotalSeconds -gt $HardTimeoutSec) {
            $zombies = @(Get-UnityProcesses | Where-Object { (Get-UnityClass -Proc $_) -eq 'zombie-batch' })
            foreach ($p in $zombies) { taskkill /PID $p.ProcessId /F 2>&1 | Out-Null }
            throw ("Unity hard timeout ({0}s) exceeded (marker never appeared)" -f $HardTimeoutSec)
        }
        Start-Sleep -Seconds 5
    }
    # Never leave a zombie Unity behind - but only kill cmd-line-verified batches
    # (our own launch is one; a user editor is not and must survive).
    Start-Sleep -Seconds 2
    $leftover = @(Get-UnityProcesses | Where-Object { (Get-UnityClass -Proc $_) -eq 'zombie-batch' })
    if ($leftover.Count -gt 0) {
        foreach ($p in $leftover) { taskkill /PID $p.ProcessId /F 2>&1 | Out-Null }
        Write-Host '  [discipline] leftover zombie batch force-stopped (cmdline verified)' -ForegroundColor Yellow
    }
    if ($null -eq $code) { throw 'Unity exit code not captured (no UNITY_EXIT_CODE marker from bash)' }
    return $code
}

function Read-TestResultsXml([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try {
        [xml]$doc = Get-Content -LiteralPath $Path -Raw
        $run = $doc.DocumentElement
        if ($run -and $run.Name -ne 'test-run') { $run = $doc.SelectSingleNode('//test-run') }
        if (-not $run) { return $null }
        return @{
            Total   = [int]$run.total
            Passed  = [int]$run.passed
            Failed  = [int]$run.failed
            Skipped = [int]$run.skipped
            Result  = [string]$run.result
            Duration= [string]$run.duration
        }
    } catch { return $null }
}

# ------------------------------------------------------------------- main ----
function Main {
    if (-not (Test-Path -LiteralPath $UnityExe)) {
        Write-Host ("MISS: Unity not found: {0}" -f $UnityExe) -ForegroundColor Red
        Write-Host 'PROBE_VERDICT: MISS (unity missing)'
        return 2
    }
    if (-not (Test-Path -LiteralPath (Join-Path $UnityProj 'Assets\Game\Tests\PlayMode\ColonyReportProbeTests.cs'))) {
        Write-Host 'MISS: probe test case not found (Assets/Game/Tests/PlayMode/ColonyReportProbeTests.cs)' -ForegroundColor Red
        Write-Host 'PROBE_VERDICT: MISS (probe test case missing - cannot prove no regression)'
        return 2
    }
    if (-not (Test-Path -LiteralPath $OutDir)) {
        Write-Host ("MISS: probe output dir not found: {0}" -f $OutDir) -ForegroundColor Red
        Write-Host 'PROBE_VERDICT: MISS (no probe output dir - cannot prove no regression)'
        return 2
    }

    Write-Host ("== colony report panel probe ({0}) ==" -f ($(if ($UpdateBaseline) { 'UpdateBaseline' } elseif ($Gate) { 'Gate' } else { 'report-only' })))

    # ---- environment guard: Unity slot (user editor may be open - wait, never kill) ----
    $stillBusy = Wait-UnitySlot
    if ($stillBusy -and $stillBusy.Count -gt 0) {
        $zombies = @($stillBusy | Where-Object { (Get-UnityClass -Proc $_) -eq 'zombie-batch' })
        $users   = @($stillBusy | Where-Object { (Get-UnityClass -Proc $_) -eq 'user-or-unknown' })
        $actives = @($stillBusy | Where-Object { (Get-UnityClass -Proc $_) -eq 'active-batch' })
        if ($users.Count -gt 0) {
            Write-Host ("MISS: user editor still open after {0} x {1}s: pid {2}" -f $WaitRounds, $WaitSecondsPerRound,
                (($users | ForEach-Object ProcessId) -join ', ')) -ForegroundColor Red
            Write-Host '  the gate never kills the user editor - close it or re-run later.' -ForegroundColor Yellow
            Write-Host 'PROBE_VERDICT: MISS (environment guard: user editor open)'
            return 2
        }
        if ($actives.Count -gt 0) {
            Write-Host ("MISS: an ACTIVE batch (parallel agent's legitimate run, log written <5min ago) survived the wait window: pid {0}" -f `
                (($actives | ForEach-Object ProcessId) -join ', ')) -ForegroundColor Red
            Write-Host '  the gate never kills another agent'"'"'s active run - re-run later (60s x 10 discipline).' -ForegroundColor Yellow
            Write-Host 'PROBE_VERDICT: MISS (environment guard: active parallel batch)'
            return 2
        }
        foreach ($p in $zombies) {
            Write-Host ("  zombie batch survived {0} x {1}s window (cmdline verified + log silent >5min) -> taskkill pid {2}" -f `
                $WaitRounds, $WaitSecondsPerRound, $p.ProcessId) -ForegroundColor Yellow
            taskkill /PID $p.ProcessId /F | Out-Null
        }
        Start-Sleep -Seconds 5
    }
    # re-check before touching the lockfile (a user editor could have opened during the wait)
    $preLock = Get-UnityProcesses
    if ($preLock.Count -gt 0) {
        Write-Host ("MISS: Unity process present right before launch (pid {0}) - lockfile untouched" -f (($preLock | ForEach-Object ProcessId) -join ', ')) -ForegroundColor Red
        Write-Host 'PROBE_VERDICT: MISS (environment guard: unity slot busy)'
        return 2
    }
    if (-not (Remove-UnityLock)) {
        Write-Host 'PROBE_VERDICT: MISS (stale UnityLockfile undeletable - cannot prove no regression)'
        return 2
    }

    # ---- collection: one probe run, one fresh anchor JSON --------------------
    $known = @(Get-OutSnapshot | ForEach-Object { $_.Name })
    $unityExit = -1
    try {
        $unityExit = Invoke-ProbeRun
    } catch {
        Write-Host ("MISS: probe run failed to complete: {0}" -f $_.Exception.Message) -ForegroundColor Red
        Write-Host 'PROBE_VERDICT: MISS (probe run did not complete)'
        return 2
    }
    Write-Host ("  Unity exit: {0} (log: TempLogs/probe_colony_report.log)" -f $unityExit)

    $pick = Select-RoundFile -Candidates (Get-OutSnapshot) -Known $known
    if ($null -eq $pick) {
        Write-Host ("MISS: probe wrote no new colony_report-*.json under {0} (unity exit={1})" -f $OutDir, $unityExit) -ForegroundColor Red
        Write-Host 'PROBE_VERDICT: MISS (no anchor JSON - cannot prove no regression)'
        return 2
    }
    $roundPath = Join-Path $OutDir $pick.Name
    Write-Host ("  anchor json: {0}" -f $pick.Name)

    $roundText = ''
    try { $roundText = [System.IO.File]::ReadAllText($roundPath) } catch {
        Write-Host ("MISS: anchor JSON unreadable: {0} ({1})" -f $pick.Name, $_.Exception.Message) -ForegroundColor Red
        Write-Host 'PROBE_VERDICT: MISS (anchor JSON unreadable)'
        return 2
    }
    $parsed = Parse-AnchorJson -Text $roundText
    if (-not $parsed.Ok) {
        Write-Host ("MISS: anchor JSON unusable ({0}): {1}" -f $pick.Name, $parsed.Reason) -ForegroundColor Red
        Write-Host 'PROBE_VERDICT: MISS (anchor JSON missing fields - cannot prove no regression)'
        return 2
    }

    $png = Test-PngEvidence -Json $parsed.Json -OutDir $OutDir
    if (-not $png.Ok) {
        Write-Host ("MISS: screenshot evidence unusable: {0}" -f $png.Reason) -ForegroundColor Red
        Write-Host 'PROBE_VERDICT: MISS (screenshot evidence missing - cannot prove no regression)'
        return 2
    }

    $xml = Read-TestResultsXml $ResultsXml
    if ($xml) {
        Write-Host ("  test framework: total={0} passed={1} failed={2} skipped={3} (xml duration {4}s)" -f `
            $xml.Total, $xml.Passed, $xml.Failed, $xml.Skipped, $xml.Duration)
    }

    # ---- -UpdateBaseline -----------------------------------------------------
    if ($UpdateBaseline) {
        $preRows = Compare-AnchorSets -Current $parsed.Json -Baseline $parsed.Json `
            -NumericPaths $Script:NumericAnchors -BooleanPaths $Script:BooleanAnchors -ImageFloors $Script:ImageFloors
        $preRed = @($preRows | Where-Object { $_.Verdict -ne 'OK' })
        if ($preRed.Count -gt 0) {
            Write-Host ("MISS: refusing to baseline a probe run that fails its own anchors/criteria ({0} rows not OK)" -f $preRed.Count) -ForegroundColor Red
            foreach ($r in $preRed) { Write-Host ("  {0}: {1} ({2})" -f $r.Anchor, $r.Verdict, $r.Note) -ForegroundColor Red }
            Write-Host 'PROBE_VERDICT: MISS (broken probe run - baseline not updated)'
            return 2
        }
        $baselineDir = Split-Path -Parent $BaselinePath
        New-Item -ItemType Directory -Force -Path $baselineDir | Out-Null
        $lines = @('{')
        $lines += '  "schema": 1,'
        $lines += '  "probe": "colony-report",'
        $lines += ('  "date": "' + (Get-Date -Format 'yyyy-MM-dd') + '",')
        $lines += ('  "unity": "' + ([string](Get-AnchorValue -Json $parsed.Json -Path 'unityVersion')) + '",')
        $lines += ('  "seed": ' + (Get-AnchorValue -Json $parsed.Json -Path 'seed') + ',')
        $lines += '  "scene": "SCN_Game",'
        $lines += '  "criterion": "content anchors exact-match vs baseline (fixed seed + deterministic domain); booleans must be true; image floors are runner constants; RED exit 1 (with -Gate) / MISS exit 2 - bench_player.ps1 semantics",'
        $lines += '  "content": {'
        $blocks = @()
        foreach ($path in $Script:NumericAnchors) {
            $v = Get-AnchorValue -Json $parsed.Json -Path $path
            $blocks += ('    "' + $path + '": ' + $v)
        }
        foreach ($path in $Script:BooleanAnchors) {
            $v = Get-AnchorValue -Json $parsed.Json -Path $path
            $blocks += ('    "' + $path + '": ' + $(if ($v) { 'true' } else { 'false' }))
        }
        $lines += ($blocks -join ",`n")
        $lines += '  },'
        $lines += '  "imageFloors": {'
        $fblocks = @()
        foreach ($k in $Script:ImageFloors.Keys) {
            $fblocks += ('    "' + $k + '": ' + (Format-Invariant ([double]$Script:ImageFloors[$k])))
        }
        $lines += ($fblocks -join ",`n")
        $lines += '  },'
        $lines += ('  "anchorJson": "' + $pick.Name + '",')
        $lines += ('  "unityExit": ' + $unityExit + ',')
        $lines += '  "notes": ['
        $lines += '    "counts are exact-match: the colony world is fixed-seed (20261122) + deterministic domain, so rendered content counts must not drift at all",'
        $lines += '    "a content change (new report lines, new anomalies) is a conscious -UpdateBaseline, never a silent pass"'
        $lines += '  ]'
        $lines += '}'
        $json = (($lines -join "`n") + "`n")
        [System.IO.File]::WriteAllText($BaselinePath, $json, (New-Object System.Text.UTF8Encoding($false)))
        Write-Host ''
        Write-Host ("baseline written: {0}" -f $BaselinePath) -ForegroundColor Green
        Write-Host ("  month={0} summaryRows={1} (anomalies={2}) rawRows={3}" -f `
            (Get-AnchorValue -Json $parsed.Json -Path 'month'),
            (Get-AnchorValue -Json $parsed.Json -Path 'summary.rowsVm'),
            (Get-AnchorValue -Json $parsed.Json -Path 'summary.anomalyLinesVm'),
            (Get-AnchorValue -Json $parsed.Json -Path 'raw.rowsRendered'))
        Write-Host 'PROBE_VERDICT: BASELINE UPDATED'
        return 0
    }

    # ---- compare vs committed baseline --------------------------------------
    if (-not (Test-Path -LiteralPath $BaselinePath)) {
        Write-Host ("MISS: no baseline at {0}" -f $BaselinePath) -ForegroundColor Red
        Write-Host '  create it once: powershell -NoProfile -ExecutionPolicy Bypass -File Tools/probe_colony_report.ps1 -UpdateBaseline' -ForegroundColor Yellow
        Write-Host 'PROBE_VERDICT: MISS (no baseline - cannot prove no regression)'
        return 2
    }
    $baselineText = ''
    try { $baselineText = [System.IO.File]::ReadAllText($BaselinePath) } catch {
        Write-Host ("MISS: baseline unreadable: {0} ({1})" -f $BaselinePath, $_.Exception.Message) -ForegroundColor Red
        Write-Host 'PROBE_VERDICT: MISS (baseline unreadable - cannot prove no regression)'
        return 2
    }
    try { $baselineDoc = ConvertFrom-Json $baselineText } catch {
        Write-Host ("MISS: baseline invalid JSON: {0}" -f $_.Exception.Message) -ForegroundColor Red
        Write-Host 'PROBE_VERDICT: MISS (baseline broken - cannot prove no regression)'
        return 2
    }
    $baselineContent = $baselineDoc.content
    if ($null -eq $baselineContent) {
        Write-Host 'MISS: baseline has no content node' -ForegroundColor Red
        Write-Host 'PROBE_VERDICT: MISS (baseline broken - cannot prove no regression)'
        return 2
    }

    $rows = Compare-AnchorSets -Current $parsed.Json -Baseline $baselineContent `
        -NumericPaths $Script:NumericAnchors -BooleanPaths $Script:BooleanAnchors -ImageFloors $Script:ImageFloors

    Write-Host ''
    Write-Host '==== colony report panel anchors (exact-match counts / booleans / image floors) ===='
    $tableRows = foreach ($r in $rows) {
        [pscustomobject]@{
            anchor   = $r.Anchor
            kind     = $r.Kind
            baseline = $r.Baseline
            current  = $r.Current
            verdict  = $r.Verdict
            note     = $r.Note
        }
    }
    $tableRows | Format-Table -AutoSize | Out-String -Width 220 | Write-Host
    Write-Host ("  probe files: {0}" -f $pick.Name) -ForegroundColor DarkGray

    $reds = @($rows | Where-Object { $_.Verdict -eq 'RED' })
    $miss = @($rows | Where-Object { $_.Verdict -eq 'MISS' })
    foreach ($r in $reds) {
        Write-Host ("  RED: {0}: {1}" -f $r.Anchor, $r.Note) -ForegroundColor Red
    }
    foreach ($r in $miss) {
        Write-Host ("  MISS: {0}: {1}" -f $r.Anchor, $r.Note) -ForegroundColor Red
    }

    $exitCode = Get-CheckExitCode -RedCount $reds.Count -MissCount $miss.Count -GateMode ([bool]$Gate)
    Write-Host ''
    if ($miss.Count -gt 0) {
        Write-Host ("PROBE_VERDICT: MISS ({0} anchor(s) unprovable: {1}) - cannot prove no regression" -f $miss.Count, (($miss | ForEach-Object Anchor) -join ', ')) -ForegroundColor Red
    } elseif ($reds.Count -gt 0) {
        if ($Gate) {
            Write-Host ("PROBE_VERDICT: FAIL (gate on; {0} anchor(s) regressed: {1})" -f $reds.Count, (($reds | ForEach-Object Anchor) -join ', ')) -ForegroundColor Red
        } else {
            Write-Host ("PROBE_VERDICT: REPORT-ONLY ({0} red, not gating: {1}; pass -Gate to enforce)" -f $reds.Count, (($reds | ForEach-Object Anchor) -join ', ')) -ForegroundColor Yellow
        }
    } else {
        Write-Host ("PROBE_VERDICT: PASS ({0}/{1} anchors within criterion{2})" -f $rows.Count, $rows.Count, $(if ($Gate) { '; gate on' } else { '; report-only' })) -ForegroundColor Green
    }
    return $exitCode
}

# JSON numbers must use a '.' decimal separator regardless of the host locale.
function Format-Invariant([double]$v) {
    return $v.ToString('0.########', [System.Globalization.CultureInfo]::InvariantCulture)
}

exit (Main)
