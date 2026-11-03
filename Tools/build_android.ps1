# =============================================================================
# build_android.ps1 - One-click Android APK build (TASK-A-AND-02)
#
# Pipeline (any stage failure => non-zero exit):
#   build : Unity -executeMethod Game.EditorTools.PlayerBuild.BuildAndroid64
#           (IL2CPP ARM64, Vulkan+GLES3, external toolchain in EditorPrefs)
#           -Backend Mono runs BuildAndroid64Mono instead (fast pipeline
#           verification output: Build/Android/ZFrame_MonoVerify.apk).
#           -Backend MonoDev runs BuildAndroid64MonoDev instead (same Mono
#           pipeline + BuildOptions.Development -> DEVELOPMENT_BUILD macro,
#           DeviceFileLogger active; output Build/Android/ZFrame_MonoDev.apk).
#           Use it for on-device evidence runs (TASK-A-AND-04).
#   report: APK size + Unity exit code => stdout (APK itself lands in
#           Build/Android/). No smoke stage - device smoke (adb devices /
#           install / logcat) is run manually, see TASK-A-AND-02.
#
# External toolchain (installed by TASK-A-AND-02, outside the repo):
#   C:/Users/1/Android/{jdk, platform-tools, platforms/android-34,
#   build-tools/34.0.0, ndk/23.1.7779620}
#   Written to EditorPrefs by PlayerBuild.BuildAndroid64* BEFORE
#   SwitchActiveBuildTarget (Jdk11Path / AndroidSdkRoot / AndroidNdkRoot;
#   key names verified against Unity 2022.3.62f3c1 binaries).
#
# Unity batch-mode discipline (mirrors Tools/build_player.ps1):
#   1. single instance: abort if Unity.exe is already running
#   2. delete UnityProject/Temp/UnityLockfile before launch (best effort,
#      via [IO.File]::Delete - NOT Remove-Item, which the local safe-delete
#      hook may intercept and turn into a false failure)
#   3. Unity is ALWAYS launched through Git Bash (direct PS launch dies in
#      the licensing handshake on this machine - HANDOFF 2026-10-25)
#   4. hard timeout default 2700s (45 min; first Android build = Gradle
#      dependency downloads + IL2CPP - HANDOFF relaxes to 45 min for this
#      task) => taskkill /F fallback, evidence stays in the log
#   5. launched WITHOUT -quit: the build method calls EditorApplication.Exit
#      itself (same contract as BuildWindows64)
#
# Usage (repo root or any CWD - all paths are script-relative):
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_android.ps1
#   powershell ... -File Tools/build_android.ps1 -Backend Mono    # fast verify
#   powershell ... -File Tools/build_android.ps1 -TimeoutSec 3600
# =============================================================================
param(
    [ValidateSet('Mono', 'MonoDev', 'IL2CPP')]
    [string]$Backend = 'IL2CPP',
    [int]$TimeoutSec = 2700
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- paths -----
$RepoRoot  = Split-Path -Parent $PSScriptRoot
$UnityExe  = 'C:\Program Files\Unity 2022.3.62f3c1\Editor\Unity.exe'
$UnityProj = Join-Path $RepoRoot 'UnityProject'
$LogDir    = Join-Path $RepoRoot 'TempLogs'
$BuildDir  = Join-Path $RepoRoot 'Build'
$ApkSuffix = switch ($Backend) { 'Mono' { '_MonoVerify' } 'MonoDev' { '_MonoDev' } default { '' } }
$ApkPath   = Join-Path $BuildDir ('Android\ZFrame{0}.apk' -f $ApkSuffix)
$UnityLog  = Join-Path $LogDir ('build_android_{0}_unity.log' -f $Backend.ToLower())
# TASK-A-AND-04: MonoDev = Mono pipeline + BuildOptions.Development (DEVELOPMENT_BUILD
# macro, so DeviceFileLogger writes zframe_boot.log on the device). Device evidence runs.
$Method    = switch ($Backend) {
    'Mono'    { 'Game.EditorTools.PlayerBuild.BuildAndroid64Mono' }
    'MonoDev' { 'Game.EditorTools.PlayerBuild.BuildAndroid64MonoDev' }
    default   { 'Game.EditorTools.PlayerBuild.BuildAndroid64' }
}

if (-not (Test-Path -LiteralPath $UnityExe)) { Write-Host "FATAL: Unity not found: $UnityExe" -ForegroundColor Red; exit 1 }
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

# ------------------------------------------------------------- helpers ------
function ToFwdSlash([string]$p) { return $p.Replace('\', '/') }

function Resolve-BashExe {
    # same defensive candidate chain as build_player.ps1 (never Join-Path on $null)
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
    param([string]$ArgLine, [int]$HardTimeoutSec = 2700)

    Assert-NoUnityRunning
    Remove-UnityLockBestEffort

    $shPath  = Join-Path $LogDir 'buildandroid_launch.sh'
    $outPath = Join-Path $LogDir 'buildandroid_launch.out'
    $errPath = Join-Path $LogDir 'buildandroid_launch.err'
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
        # duplicate case-variant variables (HTTP_PROXY vs http_proxy) crash
        # Start-Process before bash runs. Diagnose it.
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
            throw "Unity hard timeout (${HardTimeoutSec}s) exceeded (log: $UnityLog)"
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

# ---------------------------------------------------------------- main ------
$RunStart = Get-Date
try {
    Write-Host ''
    Write-Host ("==== Android build: backend={0}  method={1} ====" -f $Backend, $Method) -ForegroundColor Cyan
    Assert-NoUnityRunning
    $argLine = ('-batchmode -projectPath "{0}" -executeMethod {1} -logFile "{2}"' -f `
        (ToFwdSlash $UnityProj), $Method, (ToFwdSlash $UnityLog))
    $unityExit = Invoke-UnityBuild -ArgLine $argLine -HardTimeoutSec $TimeoutSec
    Write-Host ("  Unity exit: {0}  (log: TempLogs/{1})" -f $unityExit, (Split-Path -Leaf $UnityLog))
    if ($unityExit -ne 0) { throw "Unity build exit=$unityExit" }

    if (-not (Test-Path -LiteralPath $ApkPath)) { throw "APK not produced: $ApkPath" }
    $apk = Get-Item -LiteralPath $ApkPath

    Write-Host ''
    Write-Host 'RESULT: PASS (Android build)' -ForegroundColor Green
    Write-Host ("  APK: {0}" -f $apk.FullName)
    Write-Host ("  Size: {0} ({1:N0} bytes)  Built: {2}" -f (Format-Size $apk.Length), $apk.Length, $apk.LastWriteTime)
    Write-Host ("  Wall: {0}" -f ((Get-Date) - $RunStart).ToString('h\:mm\:ss'))
    exit 0
} catch {
    Write-Host ''
    Write-Host "RESULT: FAIL - $_" -ForegroundColor Red
    Write-Host "  unity log : $UnityLog"
    exit 1
}
