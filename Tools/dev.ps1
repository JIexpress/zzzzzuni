# Tools/dev.ps1 —— Editor 常驻 Dev Server 的最小客户端
#
# 用法（在仓库根执行）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools/dev.ps1 health
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools/dev.ps1 compile
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools/dev.ps1 test editmode
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools/dev.ps1 test playmode
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools/dev.ps1 log [-Since <seq>]
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools/dev.ps1 shot [name]
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools/dev.ps1 menu "Tools/ZFrame/Dev Server/Log status"
#   （/menu 只接受白名单前缀，默认 Tools/；需要别的用环境变量 ZBF_DEV_MENU_PREFIXES 追加，分号分隔）
#
# 目标服务：UnityProject/Assets/ZFrame/Editor/DevServer（Editor 内常驻，只监听 127.0.0.1）
# 端口：默认 17321，可用环境变量 ZBF_DEV_PORT 覆盖。
# 注意：这只是开发期快捷通道；CI 仍走 Tools/run_tests.ps1。

[CmdletBinding()]
param(
    [Parameter(Position = 0)][string]$Command = 'health',
    [Parameter(Position = 1)][string]$Arg,
    [int]$Since = 0,
    [int]$Port = 0,
    [int]$PollSeconds = 900
)

$ErrorActionPreference = 'Stop'

# 服务返回 UTF-8 JSON，中文在 GBK 控制台下会变成乱码；统一按 UTF-8 输出。
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# 本地回环一律不走代理（代理变量存在时 Invoke-RestMethod 会试图走 HTTP_PROXY）
[System.Net.WebRequest]::DefaultWebProxy = $null

if ($Port -le 0) {
    $envPort = 0
    if ([int]::TryParse([string]$env:ZBF_DEV_PORT, [ref]$envPort) -and $envPort -gt 0) {
        $Port = $envPort
    } else {
        $Port = 17321
    }
}

$BaseUrl = "http://127.0.0.1:$Port"

function Invoke-DevApi {
    param(
        [string]$Method,
        [string]$Path,
        [int]$TimeoutSec = 15
    )
    $uri = "$BaseUrl$Path"
    try {
        return Invoke-RestMethod -Method $Method -Uri $uri -TimeoutSec $TimeoutSec -ContentType 'application/json'
    } catch {
        $message = $_.Exception.Message
        Write-Host "[dev] 无法访问 $uri" -ForegroundColor Red
        Write-Host "[dev] $message" -ForegroundColor DarkGray
        Write-Host "[dev] 排查：1) Unity Editor 是否开着 2) 菜单 Tools/ZFrame/Dev Server/Enabled 是否勾选 3) 端口是否被其它进程占用" -ForegroundColor Yellow
        exit 2
    }
}

function Show-Json($object) {
    $object | ConvertTo-Json -Depth 8
}

switch ($Command.ToLowerInvariant()) {
    'health' {
        Show-Json (Invoke-DevApi -Method 'Get' -Path '/health' -TimeoutSec 5)
    }

    'compile' {
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $result = Invoke-DevApi -Method 'Post' -Path '/compile' -TimeoutSec 360
        $watch.Stop()
        Show-Json $result
        Write-Host ("[dev] /compile 墙钟耗时 {0} ms（服务端自报 {1} ms）" -f $watch.ElapsedMilliseconds, $result.ms) -ForegroundColor Cyan
        if (-not $result.ok) { exit 1 }
    }

    'test' {
        $mode = 'editmode'
        if (-not [string]::IsNullOrWhiteSpace($Arg)) { $mode = $Arg.ToLowerInvariant() }

        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $started = Invoke-DevApi -Method 'Post' -Path "/test?mode=$mode" -TimeoutSec 30
        $runId = $started.runId
        if ([string]::IsNullOrWhiteSpace($runId)) {
            Show-Json $started
            Write-Host "[dev] 没有拿到 runId" -ForegroundColor Red
            exit 1
        }

        Write-Host ("[dev] runId={0} mode={1}（{2} ms 内返回，不等结果）" -f $runId, $mode, $watch.ElapsedMilliseconds) -ForegroundColor Cyan

        while ($watch.Elapsed.TotalSeconds -lt $PollSeconds) {
            Start-Sleep -Milliseconds 900
            $state = Invoke-DevApi -Method 'Get' -Path "/test?id=$runId" -TimeoutSec 20
            if ($state.done) {
                $watch.Stop()
                Show-Json $state
                Write-Host ("[dev] {0}: total={1} passed={2} failed={3} skipped={4} | 测试耗时 {5} ms | 端到端 {6} ms" -f `
                        $mode, $state.total, $state.passed, $state.failed, $state.skipped, $state.durationMs, $watch.ElapsedMilliseconds) -ForegroundColor Green
                if ([int]$state.failed -gt 0) { exit 1 }
                exit 0
            }
        }

        Write-Host "[dev] 轮询超时（$PollSeconds s），测试仍未结束" -ForegroundColor Red
        exit 1
    }

    'log' {
        $since = $Since
        if ($since -le 0) { $since = 0 }
        Show-Json (Invoke-DevApi -Method 'Get' -Path "/console?since=$since&limit=200" -TimeoutSec 15)
    }

    'shot' {
        $name = $Arg
        $path = '/screenshot'
        if (-not [string]::IsNullOrWhiteSpace($name)) {
            $path = "/screenshot?name=$([uri]::EscapeDataString($name))"
        }
        $result = Invoke-DevApi -Method 'Post' -Path $path -TimeoutSec 60
        Show-Json $result
        if ($result.ok) {
            Write-Host ("[dev] 截图: {0} ({1}x{2}, source={3})" -f $result.path, $result.width, $result.height, $result.source) -ForegroundColor Green
        } else {
            exit 1
        }
    }

    'menu' {
        if ([string]::IsNullOrWhiteSpace($Arg)) {
            Write-Host '[dev] 需要菜单路径，例：dev.ps1 menu "Edit/Play"' -ForegroundColor Red
            exit 2
        }
        $encoded = [uri]::EscapeDataString($Arg)
        $result = Invoke-DevApi -Method 'Post' -Path "/menu?path=$encoded" -TimeoutSec 30
        Show-Json $result
        if (-not $result.ok) { exit 1 }
    }

    default {
        Write-Host "[dev] 未知命令 '$Command'" -ForegroundColor Red
        Write-Host '  可用: health | compile | test <editmode|playmode> | log | shot [name] | menu <path>' -ForegroundColor Yellow
        exit 2
    }
}
