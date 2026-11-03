# =============================================================================
# run_tests.ps1 - ZFrame/ZRender one-click full test suite (TASK-FINAL-01)
#
# Segments (any failure => exit 1; per-segment summary => TestReport.txt at
# the repository root):
#   native   : Tools/build_native.ps1 (cmake configure + build + doctest run
#              [= the single registered ctest entry `zrender_test`] + dll
#              deploy), followed by a supplementary `ctest -C Release` pass.
#   editmode : Unity -runTests -testPlatform EditMode (NEVER with -quit)
#   playmode : Unity -runTests -testPlatform PlayMode (auto-skipped if the
#              ZFrame.PlayMode.Tests assembly does not exist)
#   arch     : -executeMethod ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine
#   config   : -executeMethod ZFrame.Data.ConfigEditor.ConfigValidator.ValidateFromCommandLine
#   naming   : -executeMethod ZFrame.Editor.AssetNameValidator.ValidateFromCommandLine
#   bench    : Tools/bench_regression.ps1 - benchmark regression vs
#              Tools/bench_baseline.json (TASK-B-R9-10). Report-only by
#              default (a tolerance breach shows in the table/summary but
#              never fails the suite); env BENCH_GATE=1 turns on enforcement
#              (segment FAIL on any RED/MISS). Machine-quiet-guarded: the
#              script SKIPs (exit 0) on a busy machine.
#   player   : Tools/build_player.ps1 - one-click Windows64 Player build
#              pipeline (TASK-B-R9-15): version.json -> bundleVersion
#              injection + build auto-increment, Build/Windows64/ZFrame.exe,
#              Top-20 size report (Build/build_report.txt), then a 20s smoke
#              of the real exe (startup-chain markers in Player.log + kill).
#              DEFAULT SKIP - the build takes minutes; env PLAYER_BUILD=1
#              opts in.
#
# Unity batch-mode discipline (HANDOFF.md, built in):
#   1. Single instance: abort if Unity.exe is already running.
#   2. Delete UnityProject/Temp/UnityLockfile before every launch.
#   3. Test runs (-runTests) are launched WITHOUT -quit.
#   4. Unity is ALWAYS launched through Git Bash - launching it directly from
#      the PowerShell toolchain dies in the licensing handshake on this
#      machine (8s fake exit, no output, HANDOFF 2026-10-25 note).
#   5. Poll the results.xml: if it exists but Unity has not exited within
#      5 minutes -> taskkill /PID <pid> /F (evidence = results.xml).
#   6. Hard timeout per launch -> taskkill + segment FAIL.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools/run_tests.ps1
#   powershell ... -File Tools/run_tests.ps1 -Segments native,editmode   # partial
# =============================================================================
param(
    [ValidateSet('native', 'editmode', 'playmode', 'arch', 'config', 'naming', 'bench', 'player')]
    [string[]]$Segments = @('native', 'editmode', 'playmode', 'arch', 'config', 'naming', 'bench', 'player')
)

$ErrorActionPreference = 'Continue'

# ---------------------------------------------------------------- paths -----
$RepoRoot   = Split-Path -Parent $PSScriptRoot
$UnityExe   = 'C:\Program Files\Unity 2022.3.62f3c1\Editor\Unity.exe'
$UnityProj  = Join-Path $RepoRoot 'UnityProject'
$NativeBuild= Join-Path $RepoRoot 'Native\build'
$LogDir     = Join-Path $RepoRoot 'TempLogs'
$ReportPath = Join-Path $RepoRoot 'TestReport.txt'
$BuildScript= Join-Path $RepoRoot 'Tools\build_native.ps1'

if (-not (Test-Path $UnityExe))  { Write-Host "FATAL: Unity not found: $UnityExe" -ForegroundColor Red; exit 1 }
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

function Resolve-BashExe {
    # $env:ProgramFiles may be unset in restricted/sandboxed shells - never Join-Path
    # on a $null (that throws ParameterArgumentValidationError before the fallbacks
    # get a chance). Build candidates defensively, hardcoded paths last.
    $candidates = @()
    if ($env:ProgramFiles) {
        $candidates += (Join-Path $env:ProgramFiles 'Git\bin\bash.exe')
        $candidates += (Join-Path $env:ProgramFiles 'Git\usr\bin\bash.exe')
    }
    if (${env:ProgramFiles(x86)}) {
        $candidates += (Join-Path ${env:ProgramFiles(x86)} 'Git\bin\bash.exe')
    }
    $candidates += 'C:\Program Files\Git\bin\bash.exe'
    $candidates += 'C:\Program Files\Git\usr\bin\bash.exe'
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    $cmd = Get-Command bash -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw 'Git Bash not found (required for Unity launches, see script header)'
}
$BashExe = Resolve-BashExe

# ------------------------------------------------------------- helpers ------
function ToFwdSlash([string]$p) { return $p.Replace('\', '/') }

# TASK-G-66: every "clean up last run's leftovers" delete is best-effort. The local
# safe-delete hook (genie-trash) fails closed by THROWING; when that happened inside a
# bare Remove-Item the exception landed in the segment try/catch and turned a clean run
# into "editmode FAIL / playmode FAIL" (a false red). Returns $true when the path is gone.
function Remove-PathBestEffort([string]$Path, [string]$What = 'path') {
    if (-not (Test-Path -LiteralPath $Path)) { return $true }
    if (Test-Path -LiteralPath $Path -PathType Container) {
        # A directory where a file belongs: never recurse and never prompt - Remove-Item
        # would ask "the item has children, continue?" and a non-interactive run hangs.
        Write-Host ("  [warn] {0} is a directory, left untouched (no recursion): {1}" -f $What, $Path) -ForegroundColor Yellow
        return $false
    }
    try {
        Remove-Item -LiteralPath $Path -Force -Confirm:$false -ErrorAction Stop
    } catch {
        Write-Host ("  [warn] cannot delete {0} {1}: {2}" -f $What, $Path, $_.Exception.Message) -ForegroundColor Yellow
    }
    if (-not (Test-Path -LiteralPath $Path)) { return $true }
    try {
        [System.IO.File]::Delete($Path)
    } catch {
        Write-Host ("  [warn] fallback delete failed for {0}: {1}" -f $Path, $_.Exception.Message) -ForegroundColor Yellow
    }
    if (-not (Test-Path -LiteralPath $Path)) { return $true }
    Write-Host ("  [warn] {0} survived deletion: {1}" -f $What, $Path) -ForegroundColor Yellow
    return $false
}

# Fingerprint of a *file* (directories yield $null): last-write UTC + size.
function Get-FileFingerprint([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $i = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if (-not $i) { return $null }
    return [pscustomobject]@{ LastWriteUtc = $i.LastWriteTimeUtc; Length = [long]$i.Length }
}

# True only when $Path is a file that did not exist before the launch ($Baseline $null)
# or whose fingerprint changed since. A leftover that was never rewritten is not
# evidence for this run - reading it would be a false green (TASK-G-66 H2).
function Test-FileIsFresh($Baseline, [string]$Path) {
    $now = Get-FileFingerprint $Path
    if ($null -eq $now) { return $false }
    if ($null -eq $Baseline) { return $true }
    return (($now.LastWriteUtc -ne $Baseline.LastWriteUtc) -or ($now.Length -ne $Baseline.Length))
}

function Remove-UnityLock {
    $lock = Join-Path $UnityProj 'Temp\UnityLockfile'
    if (-not (Test-Path -LiteralPath $lock)) { return }
    if (Remove-PathBestEffort $lock 'UnityLockfile') {
        Write-Host '  [discipline] deleted UnityProject/Temp/UnityLockfile'
    } else {
        # TASK-G-66 H1: a lock we cannot remove may stop Unity from opening the project.
        # That is a real risk for this segment, but it is NOT a reason to fail the
        # segment or abort the run on a housekeeping step - say so loudly and let
        # Unity's own verdict decide.
        Write-Host '  [warn] UnityLockfile could not be deleted - a stale lock remains and Unity may refuse to start. Continuing; the segment verdict comes from Unity itself.' -ForegroundColor Yellow
    }
}

# TASK-G-65: clearing LAST run's validator report must never abort the run. A failed
# delete is a warning; the "never trust stale evidence" meaning is preserved by the
# caller, which flags the report as STALE when this returns $false.
# Returns $true when the path is gone (or never existed), $false when stale.
function Clear-StaleReport([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $true }
    if (Remove-PathBestEffort $Path 'stale report') { return $true }
    Write-Host ("  [warn] stale report still in place: {0} (evidence will be flagged STALE)" -f $Path) -ForegroundColor Yellow
    return $false
}

function Assert-NoUnityRunning {
    $p = Get-Process -Name Unity -ErrorAction SilentlyContinue
    if ($p) {
        throw ("Unity.exe already running (PID {0}) - single-instance discipline; close it first" -f (($p | ForEach-Object Id) -join ','))
    }
}

function Read-UnityExitCode([string]$StdoutFile) {
    if (Test-Path $StdoutFile) {
        $m = Select-String -Path $StdoutFile -Pattern 'UNITY_EXIT_CODE=(-?\d+)' | Select-Object -Last 1
        if ($m) { return [int]$m.Matches[0].Groups[1].Value }
    }
    return $null
}

# Launch Unity through Git Bash and enforce the batch-mode discipline.
#   $ArgLine     : Unity arguments (Windows paths, forward slashes, pre-quoted)
#   $MarkerFile  : results.xml to poll (test runs). $null for -executeMethod.
#   $HardTimeoutSec / $XmlGraceSec : discipline rule 6 / rule 5.
# Returns an object: ExitCode / EvidenceFresh / EvidenceNote. EvidenceFresh is the
# answer to "may the caller trust $MarkerFile as THIS run's evidence?".
function Invoke-UnityBatch {
    param(
        [string]$ArgLine,
        [AllowNull()][string]$MarkerFile,
        [int]$HardTimeoutSec = 1800,
        [int]$XmlGraceSec = 300
    )
    Assert-NoUnityRunning
    Remove-UnityLock

    # TASK-G-66 H2: fingerprint last run's results.xml BEFORE launching. If the old file
    # cannot be deleted it stays on disk, and the poll below would otherwise see it at
    # once and hand the previous run's numbers to the caller as if they were fresh
    # (false green). Only a new or rewritten file may count as evidence.
    $markerBaseline = $null
    if ($MarkerFile -and (Test-Path -LiteralPath $MarkerFile)) {
        $markerBaseline = Get-FileFingerprint $MarkerFile
        if (Remove-PathBestEffort $MarkerFile 'previous results.xml') {
            $markerBaseline = $null   # cleared: whatever shows up next belongs to this run
        } elseif ($null -eq $markerBaseline) {
            Write-Host ("  [warn] results.xml path exists but is not a readable file: {0} - it can never count as evidence" -f $MarkerFile) -ForegroundColor Yellow
        } else {
            Write-Host '  [warn] previous results.xml survived deletion - only a rewritten file will be accepted as this run''s evidence' -ForegroundColor Yellow
        }
    }

    # Launch wrapper: a tiny LF shell script, so no nested-quoting hazards.
    $shPath  = Join-Path $LogDir 'runtests_launch.sh'
    $outPath = Join-Path $LogDir 'runtests_launch.out'
    $errPath = Join-Path $LogDir 'runtests_launch.err'
    $sh = "`"$(ToFwdSlash $UnityExe)`" $ArgLine`n"
    $sh += 'rc=$?' + "`n"
    $sh += 'echo UNITY_EXIT_CODE=$rc' + "`n"
    [System.IO.File]::WriteAllText($shPath, $sh, (New-Object System.Text.UTF8Encoding($false)))

    $bashProc = $null
    try {
        $bashProc = Start-Process -FilePath $BashExe -ArgumentList (ToFwdSlash $shPath) `
            -RedirectStandardOutput $outPath -RedirectStandardError $errPath `
            -PassThru -WindowStyle Hidden
    } catch {
        # PS 5.1 builds an environment block with a case-sensitive dictionary; if the
        # process environment carries both e.g. HTTP_PROXY and http_proxy (common when a
        # proxy/sandbox injects lowercase variants) Start-Process throws a duplicate-key
        # error before bash ever runs. Diagnose it instead of surfacing a raw dict error.
        $proxyDups = @()
        $names = @([Environment]::GetEnvironmentVariables('Process').Keys)
        $names | Group-Object { $_.ToLower() } | Where-Object { $_.Count -gt 1 } |
            ForEach-Object { $proxyDups += ($_.Group -join '/') }
        if ($proxyDups.Count -gt 0) {
            throw ("cannot launch Unity via Git Bash: Start-Process failed building the environment block. " +
                "Duplicate case-variant variables present: {0}. Normalise them in the calling shell " +
                "(e.g. remove the lowercase variants) and re-run. Underlying: {1}" -f ($proxyDups -join ', '), $_.Exception.Message)
        }
        throw ("cannot launch Unity via Git Bash: {0}" -f $_.Exception.Message)
    }

    $start    = Get-Date
    $xmlSeen  = $null
    $killed   = $false
    $unityPid = $null
    while (-not $bashProc.HasExited) {
        if ($null -eq $unityPid) {
            $up = Get-Process -Name Unity -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($up) { $unityPid = $up.Id; Write-Host "  [discipline] Unity started (PID $unityPid)" }
        }
        if ($MarkerFile -and ($null -eq $xmlSeen) -and (Test-FileIsFresh $markerBaseline $MarkerFile)) {
            $xmlSeen = Get-Date
            Write-Host "  [discipline] results.xml appeared; grace window ${XmlGraceSec}s for Unity to exit"
        }
        if ($xmlSeen -and (-not $killed) -and (((Get-Date) - $xmlSeen).TotalSeconds -gt $XmlGraceSec)) {
            $up = Get-Process -Name Unity -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($up) {
                Write-Host "  [discipline] xml present but Unity alive >${XmlGraceSec}s -> taskkill /PID $($up.Id) /F" -ForegroundColor Yellow
                taskkill /PID $up.Id /F | Out-Null
                $killed = $true
            } else { break }
        }
        if (((Get-Date) - $start).TotalSeconds -gt $HardTimeoutSec) {
            $up = Get-Process -Name Unity -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($up) {
                Write-Host "  [discipline] hard timeout ${HardTimeoutSec}s -> taskkill /PID $($up.Id) /F" -ForegroundColor Yellow
                taskkill /PID $up.Id /F | Out-Null
            }
            if (-not $bashProc.WaitForExit(30000)) { Stop-Process -Id $bashProc.Id -Force -ErrorAction SilentlyContinue }
            throw "Unity hard timeout (${HardTimeoutSec}s) exceeded"
        }
        Start-Sleep -Seconds 5
    }
    # bash has exited; give the marker a moment to flush, then read it.
    $deadline = (Get-Date).AddSeconds(15)
    $code = $null
    while ((Get-Date) -lt $deadline) {
        $code = Read-UnityExitCode $outPath
        if ($null -ne $code) { break }
        Start-Sleep -Seconds 1
    }
    # Never leave a zombie Unity behind.
    $leftover = Get-Process -Name Unity -ErrorAction SilentlyContinue
    if ($leftover) { $leftover | Stop-Process -Force -ErrorAction SilentlyContinue; Write-Host '  [discipline] leftover Unity process force-stopped' -ForegroundColor Yellow }
    if ($null -eq $code) { throw 'Unity exit code not captured (no UNITY_EXIT_CODE marker from bash)' }

    # TASK-G-66 H2: decide once, after Unity is gone, whether the marker on disk is
    # really this run's output. A pre-run leftover that was never rewritten -> $false,
    # and the caller must FAIL the segment instead of parsing it.
    $evidenceFresh = $false
    $evidenceNote  = 'no marker file expected'
    if ($MarkerFile) {
        $evidenceFresh = Test-FileIsFresh $markerBaseline $MarkerFile
        if ($evidenceFresh) { $evidenceNote = 'results.xml written by this run' }
        elseif (Test-Path -LiteralPath $MarkerFile) { $evidenceNote = 'results.xml is STALE/BLOCKED (pre-run file was never rewritten)' }
        else { $evidenceNote = 'results.xml missing' }
    }
    return [pscustomobject]@{
        ExitCode      = $code
        EvidenceFresh = $evidenceFresh
        EvidenceNote  = $evidenceNote
    }
}

# Parse a Unity Test Framework results.xml (NUnit3 format).
function Read-TestResultsXml([string]$Path) {
    if (-not (Test-Path $Path)) { return $null }
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

# --------------------------------------------------------------- results ----
$AllRows = New-Object System.Collections.Generic.List[object]
function Add-Row([string]$Name, [string]$Status, [string]$Detail, [string]$Duration, [string]$Log) {
    $script:AllRows.Add([pscustomobject]@{
        Segment = $Name; Status = $Status; Detail = $Detail; Duration = $Duration; Log = $Log })
}

$RunStart = Get-Date

# ------------------------------------------------ 空载护栏（证据质量，不阻塞）----
# 2026-10-29 起：性能/帧率类结论只在空载时可信（B-R9-08 验收：CompatTelRunner 把 430fps 压到 310fps，
# 并制造出"关态比原管线快 14%"的荒谬值）。这里只记录+告警，**不改变六段判定**。
$QuietVerdict = 'unknown'
try {
    $quietScript = Join-Path $PSScriptRoot 'check_machine_quiet.ps1'
    if (Test-Path $quietScript) {
        $quietOut = & $quietScript -WarnOnly -SampleSeconds 3 2>&1 | Out-String
        $line = ($quietOut -split "`n" | Where-Object { $_ -match 'VERDICT:' } | Select-Object -Last 1)
        if ($line) { $QuietVerdict = $line.Trim() }
        Write-Host ''
        Write-Host ("machine quiet check: {0}" -f $QuietVerdict) -ForegroundColor DarkGray
        if ($QuietVerdict -notmatch 'QUIET') {
            Write-Host '  注意：本机负载偏高，本轮耗时/帧率数字不宜做跨跑比较。' -ForegroundColor Yellow
        }
    }
} catch {
    $QuietVerdict = 'check failed: ' + $_.Exception.Message
}

# ------------------------------------------------- segment 1: native --------
if ($Segments -contains 'native') {
    Write-Host ''
    Write-Host '==== [1] native: build_native.ps1 (configure + build + doctest + ctest) ====' -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $log = Join-Path $LogDir 'runtests_native.log'
    try {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $BuildScript 2>&1 |
            Tee-Object -FilePath $log
        $buildExit = $LASTEXITCODE
        $lines = Get-Content -LiteralPath $log -ErrorAction SilentlyContinue
        $tail  = @($lines | Select-Object -Last 4) -join ' | '
        Write-Host "  build_native exit: $buildExit ($tail)"

        $cases = 0; $asserts = 0; $tFailed = 0
        $mCases = Select-String -Path $log -Pattern 'test cases:\s*(\d+)\s*\|\s*(\d+)\s*passed\s*\|\s*(\d+)\s*failed' | Select-Object -Last 1
        if ($mCases) { $cases = [int]$mCases.Matches[0].Groups[1].Value; $tFailed = [int]$mCases.Matches[0].Groups[3].Value }
        $mAsserts = Select-String -Path $log -Pattern 'assertions:\s*(\d+)\s*\|\s*(\d+)\s*passed' | Select-Object -Last 1
        if ($mAsserts) { $asserts = [int]$mAsserts.Matches[0].Groups[1].Value }

        # Supplementary ctest pass (the single registered entry `zrender_test`).
        # Verdict is authoritative from build_native; ctest adds CI-style
        # evidence, but an unusable ctest binary is only a warning.
        # TASK-G-63: --test-dir is $RepoRoot-derived (absolute), and the test's
        # own working directory comes from WORKING_DIRECTORY in
        # Native/CMakeLists.txt (= repo root), so this segment is independent of
        # the CWD the caller happened to invoke this script from.
        # TASK-B-R9-10: candidate list like build_native's cmake resolver - on
        # this box the PATH ctest (MSYS2/mingw64) gets write-denied in the build
        # tree ("Cannot create log file"), so the VS BuildTools ctest is retried
        # before degrading to the note. Pass/fail semantics unchanged: still
        # only a supplementary note, failures still throw.
        $ctestNote = ''
        $ctestCandidates = @()
        $pathCtest = Get-Command ctest -ErrorAction SilentlyContinue
        if ($pathCtest) { $ctestCandidates += $pathCtest.Source }
        $vsCtest = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\ctest.exe"
        if (Test-Path $vsCtest) { $ctestCandidates += $vsCtest }
        if ($ctestCandidates.Count -gt 0 -and (Test-Path (Join-Path $NativeBuild 'CTestTestfile.cmake'))) {
            $ctestNote = 'ctest: binary not usable here (verdict from build_native doctest run)'
            foreach ($ctestExe in ($ctestCandidates | Select-Object -Unique)) {
                $ctestOut = & $ctestExe --test-dir (ToFwdSlash $NativeBuild) -C Release --output-on-failure 2>&1
                $ctestText = ($ctestOut | Out-String)
                if ($ctestText -match '100% tests passed') {
                    $ctestNote = 'ctest: 100% tests passed (1 entry: zrender_test)'
                    break
                }
                elseif ($ctestText -match '% tests passed') {
                    $flat = $ctestText.Replace([string][char]13, ' ').Replace([string][char]10, ' ')
                    throw "ctest reported failures: $flat"
                }
                # else: this candidate could not run (e.g. write-denied log file)
                # - try the next candidate; if none works the note stands.
            }
        } else { $ctestNote = 'ctest: unavailable (verdict from build_native doctest run)' }

        if ($buildExit -eq 0 -and $tFailed -eq 0 -and $cases -gt 0) {
            Write-Host "  PASS: $cases cases / $asserts assertions / 0 failed; $ctestNote" -ForegroundColor Green
            Add-Row 'native' 'PASS' ("cases={0} asserts={1} failed=0; {2}" -f $cases, $asserts, $ctestNote) $sw.Elapsed.ToString('mm\:ss') 'TempLogs/runtests_native.log'
        } elseif ($buildExit -eq 0 -and $cases -le 0) {
            # 0 cases + exit 0 = 测试二进制没跑起来/用例被裁掉，绝不能当通过。
            throw "build_native exit=0 but parsed 0 test cases from log (empty/filtered test binary - refusing to report PASS)"
        } else {
            throw "build_native exit=$buildExit, doctest failed=$tFailed"
        }
    } catch {
        Write-Host "  FAIL: $_" -ForegroundColor Red
        Add-Row 'native' 'FAIL' $_.ToString() $sw.Elapsed.ToString('mm\:ss') 'TempLogs/runtests_native.log'
    }
}

# ------------------------------------------- segments 2/3: Unity EditMode ---
function Invoke-UnityTestSegment([string]$Name, [string]$Platform, [string]$AssemblyHint) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $xml  = Join-Path $LogDir ("runtests_{0}.xml" -f $Platform.ToLower())
    $ulog = Join-Path $LogDir ("runtests_{0}.log" -f $Platform.ToLower())
    try {
        if (-not (Test-Path $AssemblyHint)) {
            Write-Host "  SKIP: $Platform test assembly not found ($AssemblyHint)"
            Add-Row $Name 'SKIP' 'test assembly not present' '-' '-'
            return
        }
        $argLine = ("-batchmode -projectPath `"{0}`" -runTests -testPlatform {1} -testResults `"{2}`" -logFile `"{3}`"" -f `
            (ToFwdSlash $UnityProj), $Platform, (ToFwdSlash $xml), (ToFwdSlash $ulog))
        $run  = Invoke-UnityBatch -ArgLine $argLine -MarkerFile $xml -HardTimeoutSec 2400
        $code = $run.ExitCode
        Write-Host ("  Unity exit: {0}  (evidence: {1})" -f $code, $run.EvidenceNote)
        # TASK-G-66 H2: never parse a results.xml this run did not write. A leftover
        # would report the previous run's counts - that is exactly the false green the
        # task forbids. Stale/blocked evidence is a FAIL, not a PASS.
        if (-not $run.EvidenceFresh) {
            throw ("results.xml cannot be trusted as this run's evidence ({0}) - refusing to read it: {1}" -f $run.EvidenceNote, $xml)
        }
        $r = Read-TestResultsXml $xml
        if (-not $r) { throw "results.xml missing/unreadable: $xml (Unity exit=$code)" }
        $dur = if ($r.Duration) { "{0}s(xml)" -f $r.Duration } else { '' }
        if ($r.Failed -eq 0 -and $r.Total -gt 0 -and $code -eq 0) {
            Write-Host ("  PASS: total={0} passed={1} failed=0 {2}" -f $r.Total, $r.Passed, $dur) -ForegroundColor Green
            Add-Row $Name 'PASS' ("total={0} passed={1} failed=0 skipped={2} {3}" -f $r.Total, $r.Passed, $r.Skipped, $dur) $sw.Elapsed.ToString('mm\:ss') ("TempLogs/{0}" -f (Split-Path -Leaf $xml))
        } elseif ($r.Failed -eq 0 -and $r.Total -gt 0) {
            Write-Host ("  PASS (taskkilled zombie): total={0} failed=0, Unity exit={1} - evidence=results.xml" -f $r.Total, $code) -ForegroundColor Green
            Add-Row $Name 'PASS' ("total={0} passed={1} failed=0 [zombie killed, evidence=xml, unity exit={2}]" -f $r.Total, $r.Passed, $code) $sw.Elapsed.ToString('mm\:ss') ("TempLogs/{0}" -f (Split-Path -Leaf $xml))
        } else {
            throw ("tests failed: total={0} passed={1} failed={2} (Unity exit={3})" -f $r.Total, $r.Passed, $r.Failed, $code)
        }
    } catch {
        Write-Host "  FAIL: $_" -ForegroundColor Red
        Add-Row $Name 'FAIL' $_.ToString() $sw.Elapsed.ToString('mm\:ss') ("TempLogs/{0}" -f (Split-Path -Leaf $xml))
    }
}

if ($Segments -contains 'editmode') {
    Write-Host ''
    Write-Host '==== [2] Unity EditMode tests (-runTests, no -quit) ====' -ForegroundColor Cyan
    Invoke-UnityTestSegment 'editmode' 'EditMode' (Join-Path $UnityProj 'Assets\ZFrame\Tests\ZFrame.Core.Tests.asmdef')
}

if ($Segments -contains 'playmode') {
    Write-Host ''
    Write-Host '==== [3] Unity PlayMode tests (-runTests, no -quit) ====' -ForegroundColor Cyan
    Invoke-UnityTestSegment 'playmode' 'PlayMode' (Join-Path $UnityProj 'Assets\ZFrame\Tests\PlayMode\ZFrame.PlayMode.Tests.asmdef')
}

# --------------------------------------- segments 4/5/6: batch validators ---
function Invoke-ValidatorSegment([string]$Name, [string]$Method, [string]$ReportRel) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $ulog = Join-Path $LogDir ("runtests_{0}.log" -f $Name)
    $rpt  = Join-Path $LogDir (Split-Path -Leaf $ReportRel)
    # TASK-G-65: best-effort clear of last run's report (a delete failure must not
    # abort the whole suite - see Clear-StaleReport). $rptFresh=$false means the
    # old file survived, so its presence is NOT this run's evidence.
    $rptFresh = Clear-StaleReport $rpt
    try {
        $argLine = ("-batchmode -quit -projectPath `"{0}`" -executeMethod {1} -logFile `"{2}`"" -f `
            (ToFwdSlash $UnityProj), $Method, (ToFwdSlash $ulog))
        $code = (Invoke-UnityBatch -ArgLine $argLine -MarkerFile $null -HardTimeoutSec 1200).ExitCode
        $evidence = if (Test-Path $rpt) { ('report: ' + $ReportRel) } else { 'report file absent' }
        if (-not $rptFresh -and (Test-Path $rpt)) {
            $evidence = $evidence + ' [STALE: delete of previous run failed]'
        }
        if ($code -eq 0) {
            Write-Host "  PASS: exit 0 ($evidence)" -ForegroundColor Green
            Add-Row $Name 'PASS' ("exit=0; {0}" -f $evidence) $sw.Elapsed.ToString('mm\:ss') ("TempLogs/{0}" -f (Split-Path -Leaf $ulog))
        } else {
            throw "validator exit=$code ($evidence)"
        }
    } catch {
        Write-Host "  FAIL: $_" -ForegroundColor Red
        Add-Row $Name 'FAIL' $_.ToString() $sw.Elapsed.ToString('mm\:ss') ("TempLogs/{0}" -f (Split-Path -Leaf $ulog))
    }
}

if ($Segments -contains 'arch') {
    Write-Host ''
    Write-Host '==== [4] architecture validator ====' -ForegroundColor Cyan
    Invoke-ValidatorSegment 'arch' 'ZFrame.Editor.ArchitectureValidator.ValidateFromCommandLine' 'TempLogs/arch_report.txt'
}
if ($Segments -contains 'config') {
    Write-Host ''
    Write-Host '==== [5] config validator ====' -ForegroundColor Cyan
    Invoke-ValidatorSegment 'config' 'ZFrame.Data.ConfigEditor.ConfigValidator.ValidateFromCommandLine' 'TempLogs/config_report.txt'
}
if ($Segments -contains 'naming') {
    Write-Host ''
    Write-Host '==== [6] asset naming check ====' -ForegroundColor Cyan
    Invoke-ValidatorSegment 'naming' 'ZFrame.Editor.AssetNameValidator.ValidateFromCommandLine' 'TempLogs/name_report.txt'
}

# ------------------------------------------- segment 7: bench regression ----
# TASK-B-R9-10: benchmark regression vs Tools/bench_baseline.json.
# Default report-only: a tolerance breach is surfaced in the table and this
# row's detail but NEVER fails the suite. BENCH_GATE=1 in the environment
# switches the child script to gate mode (exit 1 on RED/MISS -> FAIL row).
# The child script machine-quiet-checks first and verdicts SKIP (exit 0) on a
# busy machine - load-contaminated numbers are worthless evidence either way.
if ($Segments -contains 'bench') {
    Write-Host ''
    Write-Host '==== [7] bench: benchmark regression vs baseline (report-only unless BENCH_GATE=1) ====' -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $log = Join-Path $LogDir 'runtests_bench.log'
    try {
        $benchArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
            (Join-Path $PSScriptRoot 'bench_regression.ps1'))
        if ($env:BENCH_GATE -eq '1') { $benchArgs += '-Gate' }
        # BENCH_SKIP_QUIET=1: pass -SkipQuietCheck through (debug/CI escape hatch -
        # e.g. reproducing the gate verdict chain on a momentarily noisy dev box;
        # without it the child SKIPs on a busy machine by design).
        if ($env:BENCH_SKIP_QUIET -eq '1') { $benchArgs += '-SkipQuietCheck' }
        $out = & powershell.exe @benchArgs 2>&1 | Tee-Object -FilePath $log
        $out | ForEach-Object { Write-Host ("  {0}" -f $_) }
        $exit = $LASTEXITCODE
        $verdict = ($out | Where-Object { $_ -match 'BENCH_VERDICT:' } | Select-Object -Last 1)
        if ($null -ne $verdict) { $verdict = ([string]$verdict).Trim() }
        else { $verdict = 'BENCH_VERDICT: none (no verdict line from bench_regression.ps1)' }
        if ($exit -eq 0 -and $verdict -match 'PASS|BASELINE UPDATED') {
            Write-Host ("  PASS: {0}" -f $verdict) -ForegroundColor Green
            Add-Row 'bench' 'PASS' $verdict $sw.Elapsed.ToString('mm\:ss') 'TempLogs/runtests_bench.log'
        } elseif ($exit -eq 0 -and $verdict -match 'SKIP|REPORT-ONLY') {
            Write-Host ("  SKIP/REPORT-ONLY: {0}" -f $verdict) -ForegroundColor Yellow
            Add-Row 'bench' 'SKIP' $verdict $sw.Elapsed.ToString('mm\:ss') 'TempLogs/runtests_bench.log'
        } elseif ($exit -eq 0 -and $verdict -match 'ERROR') {
            # report-only surface for a crashed benchmark program: the suite's
            # bench segment still shows it as FAIL (a benchmark that cannot run
            # proves nothing), even though the child script exited 0.
            throw $verdict
        } else {
            throw ("bench_regression exit={0}; {1}" -f $exit, $verdict)
        }
    } catch {
        Write-Host "  FAIL: $_" -ForegroundColor Red
        Add-Row 'bench' 'FAIL' $_.ToString() $sw.Elapsed.ToString('mm\:ss') 'TempLogs/runtests_bench.log'
    }
}

# ------------------------------------------- segment 8: player build+smoke ---
# TASK-B-R9-15: one-click Player build pipeline. DEFAULT SKIP - the Unity
# player build takes minutes, so it must not inflate the everyday six-segment
# regression; env PLAYER_BUILD=1 opts in. The child script owns the whole
# chain (version injection -> Build/Windows64/ZFrame.exe -> Top-20 size
# report -> 20s exe smoke with startup-chain markers) and its exit code is
# this segment's verdict. Console/table evidence: Build/build_report.txt.
if ($Segments -contains 'player') {
    Write-Host ''
    Write-Host '==== [8] player: build_player.ps1 (Windows64 build + size report + smoke) ====' -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    if ($env:PLAYER_BUILD -ne '1') {
        Write-Host '  SKIP: set PLAYER_BUILD=1 to enable (multi-minute Unity player build + smoke)'
        Add-Row 'player' 'SKIP' 'default off; PLAYER_BUILD=1 enables (build+report+smoke)' '-' '-'
    } else {
        $log = Join-Path $LogDir 'runtests_player.log'
        try {
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'build_player.ps1') 2>&1 |
                Tee-Object -FilePath $log
            $exit = $LASTEXITCODE
            if ($exit -eq 0) {
                Write-Host '  PASS: player built + smoked + reported (see Build/build_report.txt)' -ForegroundColor Green
                Add-Row 'player' 'PASS' 'build+smoke+report exit=0 (Build/Windows64/ZFrame.exe, Build/build_report.txt)' $sw.Elapsed.ToString('mm\:ss') 'TempLogs/runtests_player.log'
            } else {
                throw "build_player.ps1 exit=$exit (TempLogs/runtests_player.log)"
            }
        } catch {
            Write-Host "  FAIL: $_" -ForegroundColor Red
            Add-Row 'player' 'FAIL' $_.ToString() $sw.Elapsed.ToString('mm\:ss') 'TempLogs/runtests_player.log'
        }
    }
}

# ------------------------------------------------------------ summary -------
$totalSec  = ((Get-Date) - $RunStart).TotalSeconds
$failedRows = @($AllRows | Where-Object { $_.Status -eq 'FAIL' })
$passedRows = @($AllRows | Where-Object { $_.Status -eq 'PASS' })
$skippedRows= @($AllRows | Where-Object { $_.Status -eq 'SKIP' })

Write-Host ''
Write-Host '==================== SUMMARY ====================' -ForegroundColor Cyan
$AllRows | Format-Table Segment, Status, Detail, Duration, Log -AutoSize | Out-String -Width 220 | Write-Host

# TestReport.txt (UTF-8 without BOM, LF endings - file-conventions.md).
$rep = New-Object System.Collections.Generic.List[string]
$rep.Add('ZFrame / ZRender - full test report (Tools/run_tests.ps1)')
$rep.Add(('Generated: {0}  (wall {1})' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), ([TimeSpan]::FromSeconds($totalSec)).ToString('h\:mm\:ss')))
$rep.Add('Segments requested: ' + ($Segments -join ', '))
$rep.Add('Machine quiet check: ' + $QuietVerdict)
$rep.Add('')
$idx = 0
foreach ($row in $AllRows) {
    $idx++
    $rep.Add(('[{0}] {1} -> {2}' -f $idx, $row.Segment, $row.Status))
    $rep.Add(('     detail : {0}' -f $row.Detail))
    $rep.Add(('     duration: {0}   log: {1}' -f $row.Duration, $row.Log))
    $rep.Add('')
}
if ($failedRows.Count -eq 0) {
    $verdict = ('ALL PASS ({0} passed, {1} skipped, 0 failed)' -f $passedRows.Count, $skippedRows.Count)
} else {
    $verdict = ('FAILED ({0} failed: {1})' -f $failedRows.Count, (($failedRows | ForEach-Object Segment) -join ', '))
}
$rep.Add('Verdict: ' + $verdict)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($ReportPath, (($rep -join "`n") + "`n"), $utf8NoBom)
Write-Host "report written: $ReportPath"

if ($failedRows.Count -gt 0) {
    Write-Host "RESULT: $verdict" -ForegroundColor Red
    exit 1
}
Write-Host "RESULT: $verdict" -ForegroundColor Green
exit 0
