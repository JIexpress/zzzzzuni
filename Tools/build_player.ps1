# =============================================================================
# build_player.ps1 - One-click Windows x64 Player build (TASK-B-R9-15)
#
# Pipeline (any stage failure => non-zero exit):
#   build : Unity -executeMethod Game.EditorTools.PlayerBuild.BuildWindows64
#           (version.json -> PlayerSettings.bundleVersion injection + build
#            number auto-increment write-back; scene list = EditorBuildSettings;
#            output <repo>/Build/Windows64/ZFrame.exe, directory form)
#   smoke : launch the built exe, wait 20s (alive all along), verify the
#           startup-chain markers in Player.log
#           (LocalLow/<companyName>/<productName>/Player.log, company/product
#            parsed from ProjectSettings.asset), then taskkill the player.
#           Crash within the window or missing markers => FAIL.
#   report: Top-20 largest files + totals + version/smoke lines =>
#           Build/build_report.txt (UTF-8 no BOM, LF - same style as
#           run_tests' TestReport.txt).
#
# Unity batch-mode discipline (mirrors Tools/run_tests.ps1):
#   1. single instance: abort if Unity.exe is already running
#   2. delete UnityProject/Temp/UnityLockfile before launch (best effort,
#      via [IO.File]::Delete - NOT Remove-Item, which the local safe-delete
#      hook may intercept and turn into a false failure)
#   3. Unity is ALWAYS launched through Git Bash (direct PS launch dies in
#      the licensing handshake on this machine - HANDOFF 2026-10-25)
#   4. hard timeout 600s by default (a player build runs longer than a batch
#      compile) => taskkill /F fallback, evidence stays in the log
#   5. launched WITHOUT -quit: the build method calls EditorApplication.Exit
#      itself (same contract as Game/Editor/ParityBench.cs)
#
# Usage (repo root or any CWD - all paths are script-relative):
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_player.ps1
#   powershell ... -File Tools/build_player.ps1 -SkipSmoke   # build + report
#   powershell ... -File Tools/build_player.ps1 -SmokeOnly   # smoke only
#   powershell ... -File Tools/build_player.ps1 -TimeoutSec 900
# =============================================================================
param(
    [switch]$SkipSmoke,
    [switch]$SmokeOnly,
    [int]$TimeoutSec = 600,
    [int]$SmokeWaitSec = 20,
    # Startup-chain keywords the smoke run must find in Player.log (exact
    # substrings, case-insensitive). Defaults = the ones verified against the
    # first real build's Player.log (see TASK-B-R9-15 construction report).
    [string[]]$SmokeKeywords = @(
        'Initialize engine version: 2022.3',
        '- Loaded All Assemblies',
        '[GameBootstrap] Awake: full boot starts.'
    )
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- paths -----
$RepoRoot     = Split-Path -Parent $PSScriptRoot
$UnityExe     = 'C:\Program Files\Unity 2022.3.62f3c1\Editor\Unity.exe'
$UnityProj    = Join-Path $RepoRoot 'UnityProject'
$LogDir       = Join-Path $RepoRoot 'TempLogs'
$ProjSettings = Join-Path $UnityProj 'ProjectSettings\ProjectSettings.asset'
$VersionJson  = Join-Path $RepoRoot 'Tools\version.json'
$BuildDir     = Join-Path $RepoRoot 'Build'
$ExePath      = Join-Path $BuildDir 'Windows64\ZFrame.exe'
$ReportPath   = Join-Path $BuildDir 'build_report.txt'
$UnityLog     = Join-Path $LogDir 'build_player_unity.log'

if (-not (Test-Path -LiteralPath $UnityExe)) { Write-Host "FATAL: Unity not found: $UnityExe" -ForegroundColor Red; exit 1 }
if (-not (Test-Path -LiteralPath $ProjSettings)) { Write-Host "FATAL: ProjectSettings.asset not found: $ProjSettings" -ForegroundColor Red; exit 1 }
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

# companyName/productName from ProjectSettings.asset (single source, no
# hardcoding): the Player.log path follows these, not the exe file name.
$settingsText = Get-Content -LiteralPath $ProjSettings -Raw
$CompanyName = if ($settingsText -match 'companyName:\s*(\S+)') { $Matches[1] } else { 'ZFrame' }
$ProductName = if ($settingsText -match 'productName:\s*(\S+)') { $Matches[1] } else { 'ZFrame' }
$PlayerLog   = Join-Path $env:USERPROFILE ("AppData\LocalLow\{0}\{1}\Player.log" -f $CompanyName, $ProductName)

# ------------------------------------------------------------- helpers ------
function ToFwdSlash([string]$p) { return $p.Replace('\', '/') }

function Resolve-BashExe {
    # same defensive candidate chain as run_tests.ps1 (never Join-Path on $null)
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

function Assert-NoUnityRunning {
    $p = Get-Process -Name Unity -ErrorAction SilentlyContinue
    if ($p) {
        throw ("Unity.exe already running (PID {0}) - single-instance discipline; close it first" -f (($p | ForEach-Object Id) -join ','))
    }
}

function Remove-UnityLockBestEffort {
    # [IO.File]::Delete only: the safe-delete hook intercepts Remove-Item and
    # throwing there would abort the run before Unity even starts.
    $lock = Join-Path $UnityProj 'Temp\UnityLockfile'
    if (-not (Test-Path -LiteralPath $lock)) { return }
    if (Test-Path -LiteralPath $lock -PathType Container) {
        Write-Host '  [warn] UnityLockfile is a directory - left untouched' -ForegroundColor Yellow
        return
    }
    try {
        [System.IO.File]::Delete($lock)
        Write-Host '  [discipline] deleted UnityProject/Temp/UnityLockfile'
    } catch {
        Write-Host ("  [warn] UnityLockfile delete failed ({0}) - Unity may refuse to start; its own verdict decides" -f $_.Exception.Message) -ForegroundColor Yellow
    }
}

function Read-UnityExitCode([string]$StdoutFile) {
    if (Test-Path -LiteralPath $StdoutFile) {
        $m = Select-String -Path $StdoutFile -Pattern 'UNITY_EXIT_CODE=(-?\d+)' | Select-Object -Last 1
        if ($m) { return [int]$m.Matches[0].Groups[1].Value }
    }
    return $null
}

# Launch Unity through Git Bash and enforce the batch-mode discipline.
# Returns the Unity process exit code (throws on timeout/no exit marker).
function Invoke-UnityBuild {
    param([string]$ArgLine, [int]$HardTimeoutSec = 600)

    Assert-NoUnityRunning
    Remove-UnityLockBestEffort

    $shPath  = Join-Path $LogDir 'buildplayer_launch.sh'
    $outPath = Join-Path $LogDir 'buildplayer_launch.out'
    $errPath = Join-Path $LogDir 'buildplayer_launch.err'
    $sh = "`"$(ToFwdSlash $UnityExe)`" $ArgLine`n"
    $sh += 'rc=$?' + "`n"
    $sh += 'echo UNITY_EXIT_CODE=$rc' + "`n"
    [System.IO.File]::WriteAllText($shPath, $sh, (New-Object System.Text.UTF8Encoding($false)))

    $bashProc = $null
    try {
        $bashProc = Start-Process -FilePath (Resolve-BashExe) -ArgumentList (ToFwdSlash $shPath) `
            -RedirectStandardOutput $outPath -RedirectStandardError $errPath `
            -PassThru -WindowStyle Hidden
    } catch {
        # PS 5.1 builds the child environment from a case-sensitive dictionary:
        # duplicate case-variant variables (HTTP_PROXY vs http_proxy, common with
        # proxies/sandboxes) crash Start-Process before bash runs. Diagnose it.
        $proxyDups = @()
        $names = @([Environment]::GetEnvironmentVariables('Process').Keys)
        $names | Group-Object { $_.ToLower() } | Where-Object { $_.Count -gt 1 } |
            ForEach-Object { $proxyDups += ($_.Group -join '/') }
        if ($proxyDups.Count -gt 0) {
            throw ("cannot launch Unity via Git Bash: Start-Process failed on duplicate case-variant env variables ({0}). Normalise them in the calling shell and re-run. Underlying: {1}" -f ($proxyDups -join ', '), $_.Exception.Message)
        }
        throw ("cannot launch Unity via Git Bash: {0}" -f $_.Exception.Message)
    }

    $start    = Get-Date
    $unityPid = $null
    while (-not $bashProc.HasExited) {
        if ($null -eq $unityPid) {
            $up = Get-Process -Name Unity -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($up) { $unityPid = $up.Id; Write-Host "  [discipline] Unity started (PID $unityPid)" }
        }
        if (((Get-Date) - $start).TotalSeconds -gt $HardTimeoutSec) {
            $up = Get-Process -Name Unity -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($up) {
                Write-Host "  [discipline] hard timeout ${HardTimeoutSec}s -> taskkill /PID $($up.Id) /F" -ForegroundColor Yellow
                taskkill /PID $up.Id /F | Out-Null
            }
            if (-not $bashProc.WaitForExit(30000)) { Stop-Process -Id $bashProc.Id -Force -ErrorAction SilentlyContinue }
            throw "Unity hard timeout (${HardTimeoutSec}s) exceeded (log: TempLogs/build_player_unity.log)"
        }
        Start-Sleep -Seconds 5
    }

    $deadline = (Get-Date).AddSeconds(15)
    $code = $null
    while ((Get-Date) -lt $deadline) {
        $code = Read-UnityExitCode $outPath
        if ($null -ne $code) { break }
        Start-Sleep -Seconds 1
    }
    # never leave a zombie Unity behind
    $leftover = Get-Process -Name Unity -ErrorAction SilentlyContinue
    if ($leftover) { $leftover | Stop-Process -Force -ErrorAction SilentlyContinue; Write-Host '  [discipline] leftover Unity process force-stopped' -ForegroundColor Yellow }
    if ($null -eq $code) { throw 'Unity exit code not captured (no UNITY_EXIT_CODE marker from bash)' }
    return $code
}

function Format-Size([long]$Bytes) {
    if ($Bytes -ge 1GB) { return ('{0:N2} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N2} MB' -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0:N2} KB' -f ($Bytes / 1KB)) }
    return ("$Bytes B")
}

# Read Player.log content with retries while the player may still hold it open.
# [IO.File]::ReadAllText fails here (its FileShare.Read conflicts with the
# writer's write handle) - open explicitly with FileShare.ReadWrite instead.
function Read-PlayerLogSafe([string]$Path) {
    for ($try = 0; $try -lt 5; $try++) {
        try {
            $fs = [System.IO.FileStream]::new($Path, [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
            $sr = New-Object System.IO.StreamReader($fs)
            try { return $sr.ReadToEnd() } finally { $sr.Close() }
        } catch { Start-Sleep -Seconds 1 }
    }
    return $null
}

# Smoke: launch the built exe, require it to stay alive for $WaitSec seconds,
# require the startup-chain markers in a Player.log rewritten by THIS run,
# then kill the player. Any deviation throws (caller turns it into FAIL).
function Invoke-Smoke {
    param([string]$PlayerLogPath, [int]$WaitSec)

    Write-Host ("  [smoke] launching {0} (window up ~{1}s)..." -f $ExePath, $WaitSec)
    $logBaseline = $null
    if (Test-Path -LiteralPath $PlayerLogPath) {
        $i = Get-Item -LiteralPath $PlayerLogPath
        $logBaseline = @{ Utc = $i.LastWriteTimeUtc; Len = $i.Length }
    }

    $proc = Start-Process -FilePath $ExePath -PassThru
    try {
        Start-Sleep -Seconds $WaitSec

        if ($proc.HasExited) {
            throw ("player exited within {0}s (exit code {1}) - startup chain crashed" -f $WaitSec, $proc.ExitCode)
        }
        if (-not (Test-Path -LiteralPath $PlayerLogPath)) {
            throw "Player.log not found after launch: $PlayerLogPath"
        }
        $i = Get-Item -LiteralPath $PlayerLogPath
        $fresh = ($null -eq $logBaseline) -or
                 ($i.LastWriteTimeUtc -ne $logBaseline.Utc) -or ($i.Length -ne $logBaseline.Len)
        if (-not $fresh) {
            throw ("Player.log unchanged since launch (stale log would be false evidence): {0}" -f $PlayerLogPath)
        }

        # marker polling with a short read-grace: the 20s alive window above is
        # the crash gate; Unity flushes Player.log lazily, so give the markers a
        # few retries (~10s) to land in the file before declaring them missing.
        $missing = @()
        $content = $null
        for ($attempt = 1; $attempt -le 5; $attempt++) {
            $content = Read-PlayerLogSafe $PlayerLogPath
            if ($null -ne $content) {
                # exact substring, case-insensitive (NOT -like: '[' in
                # '[GameBootstrap]' would be parsed as a wildcard char class)
                $missing = @($SmokeKeywords | Where-Object {
                    $content.IndexOf($_, [System.StringComparison]::OrdinalIgnoreCase) -lt 0 })
                if ($missing.Count -eq 0) { break }
            }
            if (-not $proc.HasExited) { Start-Sleep -Seconds 2 } # dead player adds no log lines
        }
        if ($missing.Count -gt 0) {
            throw ("startup-chain markers missing in Player.log after retries: {0}" -f ($missing -join ' | '))
        }
        $i = Get-Item -LiteralPath $PlayerLogPath
        Write-Host ("  [smoke] markers verified in Player.log ({0} KB): {1}" -f `
            [math]::Round($i.Length / 1KB, 1), ($SmokeKeywords -join ' ; ')) -ForegroundColor Green
    } finally {
        # never strand the player, whatever the verdict was above
        if (Get-Process -Id $proc.Id -ErrorAction SilentlyContinue) {
            taskkill /PID $proc.Id /F | Out-Null
            Start-Sleep -Seconds 1
            Write-Host ("  [smoke] player (PID {0}) killed" -f $proc.Id) -ForegroundColor DarkGray
        }
    }
    Write-Host ("  [smoke] PASS: alive {0}s, startup chain verified, player killed" -f $WaitSec) -ForegroundColor Green
    return $true
}

# ---------------------------------------------------------------- main ------
$RunStart = Get-Date
$unityExit = 'n/a (smoke-only)'
$exeVersion = '-'
$bundleVersion = '-'
$smokeVerdict = 'SKIPPED'
$sceneLines = @()

try {
    # version.json is the single source. Read it up front so SmokeOnly runs can
    # report it too; the build path RE-READS it after the Unity stage below,
    # because the C# entry increments `build` for THIS build during that stage.
    $vjson = Get-Content -LiteralPath $VersionJson -Raw | ConvertFrom-Json
    $bundleVersion = '{0}.{1}' -f $vjson.version, $vjson.build

    if ($SmokeOnly) {
        Write-Host ''
        Write-Host '==== [smoke-only] existing build (no Unity launch) ====' -ForegroundColor Cyan
        if (-not (Test-Path -LiteralPath $ExePath)) {
            throw "SmokeOnly: exe not found at $ExePath - run a build first"
        }
    } else {
        Write-Host ''
        Write-Host '==== [1] build: Unity batch BuildWindows64 (version inject + auto-increment) ====' -ForegroundColor Cyan
        Assert-NoUnityRunning
        $argLine = ('-batchmode -projectPath "{0}" -executeMethod Game.EditorTools.PlayerBuild.BuildWindows64 -logFile "{1}"' -f `
            (ToFwdSlash $UnityProj), (ToFwdSlash $UnityLog))
        $unityExit = Invoke-UnityBuild -ArgLine $argLine -HardTimeoutSec $TimeoutSec
        Write-Host ("  Unity exit: {0}  (log: TempLogs/build_player_unity.log)" -f $unityExit)
        if ($unityExit -ne 0) { throw "Unity build exit=$unityExit" }

        if (-not (Test-Path -LiteralPath $ExePath)) { throw "exe not produced: $ExePath" }

        # Re-read version.json AFTER the build: the C# entry incremented `build`
        # for THIS build and wrote it back - the file now names this artifact.
        $vjson = Get-Content -LiteralPath $VersionJson -Raw | ConvertFrom-Json
        $bundleVersion = '{0}.{1}' -f $vjson.version, $vjson.build

        # Channel 1 - ProjectSettings.asset persistence (the whitelisted write path)
        $psText = Get-Content -LiteralPath $ProjSettings -Raw
        if ($psText -notmatch [regex]::Escape('bundleVersion: ' + $bundleVersion)) {
            throw ("ProjectSettings.asset bundleVersion was not persisted (expected '{0}')" -f $bundleVersion)
        }
        # Channel 2 - the player data: bundleVersion is serialized into
        # globalgamemanagers (this is what Application.version reads at runtime).
        # NOTE (honest boundary): the exe's VS_VERSION_INFO stays engine-stamped
        # in Unity 2022.3 Windows (FileVersion/ProductVersion = engine build) -
        # it is NOT a bundleVersion channel; the two checks above are.
        $ggm = Join-Path $BuildDir 'Windows64\ZFrame_Data\globalgamemanagers'
        if (-not (Test-Path -LiteralPath $ggm)) { throw "player data not found: $ggm" }
        if (-not (Select-String -LiteralPath $ggm -Pattern ([regex]::Escape($bundleVersion)) -Quiet)) {
            throw ("bundleVersion '{0}' not found in player data (globalgamemanagers)" -f $bundleVersion)
        }
        $exeVersion = (Get-Item -LiteralPath $ExePath).VersionInfo.FileVersion # informational only
        Write-Host ("  PASS: exe={0}  bundleVersion={1} (version.json build={2})  ProjectSettings persisted  player-data version OK" -f `
            (Split-Path -Leaf $ExePath), $bundleVersion, $vjson.build) -ForegroundColor Green

        # scene list evidence from the Unity log (printed by PlayerBuild)
        $sceneLines = @(Select-String -Path $UnityLog -Pattern '\[PlayerBuild\] scene: (.+)' -ErrorAction SilentlyContinue |
            ForEach-Object { $_.Matches[0].Groups[1].Value })
        if ($sceneLines.Count -eq 0) { $sceneLines = @('(not captured from Unity log)') }
    }

    if ($SkipSmoke) {
        Write-Host ''
        Write-Host '==== [2] smoke: SKIPPED (-SkipSmoke) ====' -ForegroundColor Yellow
    } else {
        Write-Host ''
        Write-Host '==== [2] smoke: launch exe + 20s startup-chain check + taskkill ====' -ForegroundColor Cyan
        Invoke-Smoke -PlayerLogPath $PlayerLog -WaitSec $SmokeWaitSec | Out-Null
        $smokeVerdict = 'PASS'
    }

    # ---- report (after smoke so it can carry its verdict) ------------------
    Write-Host ''
    Write-Host '==== [3] report: Top-20 size table -> Build/build_report.txt ====' -ForegroundColor Cyan
    $files = @(Get-ChildItem -LiteralPath $BuildDir -Recurse -File | Sort-Object Length -Descending)
    $totalBytes = [long]($files | Measure-Object Length -Sum).Sum
    $top = @($files | Select-Object -First 20)

    $rep = New-Object System.Collections.Generic.List[string]
    $rep.Add('ZFrame / ZRender - player build report (Tools/build_player.ps1)')
    $rep.Add(('Generated: {0}  (wall {1})' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), ((Get-Date) - $RunStart).ToString('h\:mm\:ss')))
    $rep.Add(('Output:    {0}' -f ('Build/Windows64 (exe: ZFrame.exe, directory form)')))
    $rep.Add(('Version:   bundleVersion={0}  (Tools/version.json: version={1}, build={2}; single source)' -f $bundleVersion, $vjson.version, $vjson.build))
    $rep.Add(('Scenes:    {0}' -f (($sceneLines | ForEach-Object { (Split-Path -Leaf $_) }) -join ', ')))
    $rep.Add(('Unity exit: {0}   exe FileVersion: {1} (engine-stamped by Unity 2022.3 - not a bundleVersion channel; see Version line + globalgamemanagers check)' -f $unityExit, $exeVersion))
    $rep.Add(('Smoke:     {0} (wait {1}s; Player.log: LocalLow/{2}/{3}/Player.log)' -f $smokeVerdict, $SmokeWaitSec, $CompanyName, $ProductName))
    $rep.Add(('           markers: {0}' -f ($SmokeKeywords -join ' ; ')))
    $rep.Add('')
    $rep.Add(('Files: {0} files, total {1} ({2} bytes)' -f $files.Count, (Format-Size $totalBytes), $totalBytes))
    $rep.Add('Top-20 largest files:')
    $rank = 0
    foreach ($f in $top) {
        $rank++
        $rel = $f.FullName.Substring($BuildDir.Length + 1)
        $rep.Add(('  {0,2}. {1,12:N0} bytes  {2,9}  {3}' -f $rank, $f.Length, (Format-Size $f.Length), $rel))
    }
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($ReportPath, (($rep -join "`n") + "`n"), $utf8NoBom)
    Write-Host ("  report written: {0}  ({1} files, total {2})" -f $ReportPath, $files.Count, (Format-Size $totalBytes)) -ForegroundColor Green

    Write-Host ''
    Write-Host 'RESULT: PASS (build + smoke + report)' -ForegroundColor Green
    exit 0
} catch {
    Write-Host ''
    Write-Host "RESULT: FAIL - $_" -ForegroundColor Red
    Write-Host "  unity log : TempLogs/build_player_unity.log (Unity exit: $unityExit)"
    exit 1
}
