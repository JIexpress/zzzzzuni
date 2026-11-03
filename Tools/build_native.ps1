# =============================================================================
# build_native.ps1 - ZRender native one-click build (TASK-B-R0-01)
#
# Steps: cmake configure (VS 17 2022 x64) -> build (Release) -> run unit tests
#        -> copy zrender.dll into UnityProject/Assets/Plugins/ZRender/ (if the
#        Unity project exists; otherwise skipped, NOT an error).
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools/build_native.ps1
#   powershell ... -File Tools/build_native.ps1 -Configuration Debug
# =============================================================================
param(
    [ValidateSet('Debug', 'Release', 'RelWithDebInfo', 'MinSizeRel')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

# Repo root = parent of the Tools\ folder containing this script.
$RepoRoot = Split-Path -Parent $PSScriptRoot

# TASK-G-63: the doctest run below inherits the caller's CWD, and
# Native/tests/test_zrhi_vulkan_spv.cpp locates its SPIR-V products through
# relative candidates (Native/build/bin/shaders_spv/ ...). Calling this script
# from anywhere but the repo root therefore produced a false "SPIR-V missing".
# Pin the working directory to the repo root (= the documented invocation, and
# the same directory ctest now uses via WORKING_DIRECTORY in Native/CMakeLists).
# TASK-G-65: Push-Location (not Set-Location) - when this script is invoked with
# `& .\Tools\build_native.ps1` it shares the caller's session, so the caller's CWD
# must be restored on every exit path (success, throw, $ErrorActionPreference).
# The matching Pop-Location lives in the `finally` block of the try below.
Push-Location $RepoRoot

$NativeDir = Join-Path $RepoRoot 'Native'
$BuildDir = Join-Path $NativeDir 'build'
$BinDir = Join-Path $BuildDir "bin\$Configuration"
$TestExe = Join-Path $BinDir 'zrender_test.exe'
$DllFile = Join-Path $BinDir 'zrender.dll'

function Resolve-CMakeCommand {
    # Prefer the cmake on PATH. Some restricted environments deny write access
    # to the MSYS2 cmake.exe that often sits on PATH, so every candidate is
    # probed with a real write into the build tree; the first one that works
    # wins. Fallback candidate: the CMake bundled with VS 2022 BuildTools
    # (it pairs naturally with the "Visual Studio 17 2022" generator).
    $candidates = @()
    $pathCmake = Get-Command cmake -ErrorAction SilentlyContinue
    if ($pathCmake) { $candidates += $pathCmake.Source }
    # $env:ProgramFiles / ProgramFiles(x86) may be unset in restricted or sandboxed
    # shells - Join-Path on $null throws a binding error, so build the roots defensively
    # and keep hardcoded fallbacks last.
    $vsRoots = @()
    if (${env:ProgramFiles(x86)}) { $vsRoots += ${env:ProgramFiles(x86)} }
    if ($env:ProgramFiles) { $vsRoots += $env:ProgramFiles }
    $vsRoots += 'C:\Program Files (x86)'
    $vsRoots += 'C:\Program Files'
    foreach ($vsRoot in ($vsRoots | Select-Object -Unique)) {
        $vsCmake = Join-Path $vsRoot 'Microsoft Visual Studio\2022\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
        if (Test-Path $vsCmake) { $candidates += $vsCmake }
    }
    if ($candidates.Count -eq 0) { throw 'cmake not found on PATH or in VS BuildTools' }

    $probeDir = Join-Path $BuildDir 'cmake_probe_tmp'
    foreach ($candidate in $candidates) {
        # NOTE: do NOT redirect stderr here - under $ErrorActionPreference='Stop'
        # PowerShell 5.1 turns native stderr lines into terminating errors.
        & $candidate -E make_directory $probeDir
        if (($LASTEXITCODE -eq 0) -and (Test-Path $probeDir)) {
            # TASK-G-66 H3: cleaning the probe directory is housekeeping, not a build
            # step. A failing delete (the local safe-delete hook fails closed by throwing,
            # and -ErrorAction SilentlyContinue does NOT stop a thrown exception) used to
            # kill the whole build with "FAILED: [safe-delete]...". Degrade to a warning.
            try {
                Remove-Item -Recurse -Force $probeDir -Confirm:$false -ErrorAction Stop
            } catch {
                Write-Host ("  [warn] cannot remove cmake probe dir {0}: {1} (continuing)" -f $probeDir, $_.Exception.Message) -ForegroundColor Yellow
            }
            $version = (& $candidate --version | Select-Object -First 1)
            Write-Host "cmake: $candidate ($version)"
            return $candidate
        }
    }
    throw 'no usable cmake found: every candidate failed the build-tree write probe'
}

function Invoke-Step {
    param([string]$Name, [scriptblock]$Action)
    Write-Host ''
    Write-Host "==== [$Name] ====" -ForegroundColor Cyan
    & $Action
}

try {
    $CMakeCmd = Resolve-CMakeCommand

    Invoke-Step '1/4 cmake configure: Visual Studio 17 2022, x64' {
        & $CMakeCmd -S $NativeDir -B $BuildDir -G 'Visual Studio 17 2022' -A x64
        if ($LASTEXITCODE -ne 0) { throw "cmake configure failed (exit=$LASTEXITCODE)" }
    }

    Invoke-Step "2/4 build: $Configuration (/W4 /WX zero-warning)" {
        & $CMakeCmd --build $BuildDir --config $Configuration --parallel
        if ($LASTEXITCODE -ne 0) { throw "build failed (exit=$LASTEXITCODE)" }
    }

    Invoke-Step '3/4 run unit tests (doctest)' {
        if (-not (Test-Path $TestExe)) { throw "test executable not found: $TestExe" }
        & $TestExe
        if ($LASTEXITCODE -ne 0) { throw "unit tests failed (exit=$LASTEXITCODE)" }
    }

    Invoke-Step '4/4 deploy zrender.dll to Unity plugins' {
        $PluginsRoot = Join-Path $RepoRoot 'UnityProject\Assets\Plugins'
        if (Test-Path $PluginsRoot) {
            if (-not (Test-Path $DllFile)) { throw "zrender.dll not found: $DllFile" }
            $DestDir = Join-Path $PluginsRoot 'ZRender'
            New-Item -ItemType Directory -Force -Path $DestDir | Out-Null
            Copy-Item $DllFile -Destination $DestDir -Force
            Write-Host "copied: zrender.dll -> $DestDir"
        }
        else {
            Write-Host 'skipped copy: UnityProject/Assets/Plugins does not exist yet (Unity side not ready - not an error)'
        }
    }

    Write-Host ''
    Write-Host 'ALL GREEN: configure + build + test succeeded' -ForegroundColor Green
    exit 0
}
catch {
    Write-Host ''
    Write-Host "FAILED: $_" -ForegroundColor Red
    exit 1
}
finally {
    # TASK-G-65: always hand the caller's working directory back, including on the
    # exit 0 / exit 1 paths above (finally runs for both).
    Pop-Location
}
