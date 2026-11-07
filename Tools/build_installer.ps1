# 构建 Windows 安装包（Inno Setup）
#   .\Tools\build_installer.ps1
#   产物：Build\Installer\ZFrame-Setup-<version>.exe
# 前置：已跑过 Tools\build_player.ps1（Build\Windows64 存在）
[CmdletBinding()]
param(
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot

function Get-IsccPath {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')  # winget 用户级安装落这里
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    throw "找不到 Inno Setup 6 的 ISCC.exe。装法：winget install -e --id JRSoftware.InnoSetup"
}

$versionJson = Join-Path $RepoRoot 'Tools\version.json'
if (-not (Test-Path $versionJson)) { throw "缺 $versionJson" }
$v = Get-Content $versionJson -Raw | ConvertFrom-Json
$fullVersion = "$($v.version).$($v.build)"

$playerDir = Join-Path $RepoRoot 'Build\Windows64'
if (-not (Test-Path (Join-Path $playerDir 'ZFrame.exe'))) {
    throw "缺 $playerDir\ZFrame.exe，先跑 Tools\build_player.ps1"
}

# ---- 输出目录（必须先于 staging 定义）----
$outDir = Join-Path $RepoRoot 'Build\Installer'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

# ---- staging：物理排除 Burst 调试目录（Inno 的 Excludes 对递归子目录不可靠）----
$stageDir = Join-Path $outDir 'staging'
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
$robo = Start-Process -FilePath 'robocopy.exe' -ArgumentList @(
    "`"$playerDir`"", "`"$stageDir`"", '/E', '/XD', 'ZFrame_BurstDebugInformation_DoNotShip',
    '/NFL', '/NDL', '/NJH', '/NJS', '/NC', '/NS'
) -Wait -PassThru -NoNewWindow
if ($robo.ExitCode -ge 8) { throw "robocopy 失败，exit=$($robo.ExitCode)" }

$iscc = Get-IsccPath
$iss  = Join-Path $PSScriptRoot 'installer\ZFrame.iss'

Write-Host "== 打包 ZFrame $fullVersion ==" -ForegroundColor Cyan
Write-Host "   Player : $playerDir"
Write-Host "   Output : $outDir"

$sw = [Diagnostics.Stopwatch]::StartNew()
& $iscc "/DMyAppVersion=$fullVersion" "/Qp" $iss
$rc = $LASTEXITCODE
$sw.Stop()
if ($rc -ne 0) { throw "ISCC 失败，exit=$rc" }

$setup = Join-Path $outDir "ZFrame-Setup-$fullVersion.exe"
if (-not (Test-Path $setup)) { throw "未生成 $setup" }
$sizeMB = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host "== 完成：$setup（$sizeMB MB，耗时 $([math]::Round($sw.Elapsed.TotalSeconds,1))s）==" -ForegroundColor Green
