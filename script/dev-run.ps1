#Requires -Version 5.1
<#
.SYNOPSIS
    一键本地调试启动器：编译并临时拉起最新版 shijian-server，随后启动调试版客户端预览效果。

.DESCRIPTION
    默认流程：
      1. 从安装目录补齐缺失的应用/网站图标到调试运行目录；
      2. 本地编译最新的 shijian-server（可通过 -ClientOnly 跳过）；
      3. 暂时停止已安装的 shijian-shell / shijian-server（不覆盖安装目录文件），
         使用本地 debug 版 shijian-server 连接安装目录的 Data/data.db 启动；
      4. 启动调试版 ShiJian.Client（连向本地新版服务端）；
      5. 关闭客户端或 Ctrl+C 退出后，自动停止调试版服务端并恢复原本的 shijian-shell。

.PARAMETER InstallDir
    安装版目录，用于取图标、真实数据库（Data）与退出后恢复服务。默认 "D:/Program Files/shijian"

.PARAMETER NoBuild
    跳过编译，直接运行已有构建产物

.PARAMETER ClientOnly
    只构建并启动调试版客户端，直接复用本机已在运行的服务端（不编/不重启 shijian-server）

.PARAMETER Detached
    后台分离启动：拉起客户端并确认窗口就绪后立即退出脚本，保持窗口与服务端存活（供 Agent/自动化测试使用）

.PARAMETER Stop
    停止当前运行的调试版客户端与服务端，并恢复安装版服务

.PARAMETER Port
    本地调试服务端监听端口。默认 37091

.EXAMPLE
    ./script/dev-run.ps1

.EXAMPLE
    ./script/dev-run.ps1 -ClientOnly

.EXAMPLE
    ./script/dev-run.ps1 -Detached

.EXAMPLE
    ./script/dev-run.ps1 -Stop
#>
[CmdletBinding()]
param(
    [string]$InstallDir = 'D:/Program Files/shijian',
    [switch]$NoBuild,
    [switch]$ClientOnly,
    [switch]$Detached,
    [switch]$Stop,
    [int]$Port = 37091
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$clientProject = [IO.Path]::Combine($root, 'ShiJian.Client', 'ShiJian.Client.csproj')
$outDir = [IO.Path]::Combine($root, 'ShiJian.Client', 'bin', 'Debug', 'net10.0')
$serverManifest = [IO.Path]::Combine($root, 'shijian-server', 'Cargo.toml')
$serverExe = [IO.Path]::Combine($root, 'shijian-server', 'target', 'debug', 'shijian-server.exe')

# 自动检测是否处于 Agent / 自动化非交互环境；若是且未指定 -Detached，默认启用 -Detached 避免前台阻塞超时
if (-not $PSBoundParameters.ContainsKey('Detached') -and ($env:ANTIGRAVITY_AGENT -eq '1' -or $env:AGENT_RUN -eq '1')) {
    Write-Host '[agent] 检测到 Agent / 自动化运行环境，自动启用 -Detached 模式避免前台阻塞'
    $Detached = [switch]::Present
}

function Copy-IconFolder {
    param(
        [string]$Source,
        [string]$Destination,
        [string]$Label
    )

    if (Test-Path $Destination) {
        $existing = @(Get-ChildItem -Path $Destination -Recurse -File -ErrorAction SilentlyContinue)
        if ($existing.Count -gt 0) {
            Write-Host ("[skip] {0}：已有 {1} 个文件" -f $Label, $existing.Count)
            return
        }
    }

    if (-not (Test-Path $Source)) {
        Write-Warning ("{0} 源目录不存在，跳过：{1}" -f $Label, $Source)
        return
    }

    $parent = Split-Path -Parent $Destination
    if (-not (Test-Path $parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    if (Test-Path $Destination) {
        # 目标已存在（比如空目录）时，把源目录里的内容拷进去，避免套成 AppIcons/AppIcons
        Copy-Item -Path (Join-Path $Source '*') -Destination $Destination -Recurse -Force
    }
    else {
        Copy-Item -Path $Source -Destination $Destination -Recurse -Force
    }
    $copied = @(Get-ChildItem -Path $Destination -Recurse -File -ErrorAction SilentlyContinue)
    Write-Host ("[copy] {0}：已从安装目录复制 {1} 个文件" -f $Label, $copied.Count)
}

function Start-ClientDetachedProcess {
    param(
        [string]$FilePath,
        [string]$WorkingDirectory,
        [string]$ArgumentList = ""
    )

    $cmd = ('"{0}" {1}' -f $FilePath, $ArgumentList).Trim()
    $result = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{
        CommandLine = $cmd
        CurrentDirectory = $WorkingDirectory
    }

    if ($result.ReturnValue -ne 0) {
        throw ("WMI 启动客户端失败，返回值：{0}" -f $result.ReturnValue)
    }

    return (Get-Process -Id $result.ProcessId)
}

function Stop-ServerStack {
    $targets = @('shijian-shell', 'shijian-server')
    $hadShell = @(Get-Process -Name 'shijian-shell' -ErrorAction SilentlyContinue).Count -gt 0
    $hadServer = @(Get-Process -Name 'shijian-server' -ErrorAction SilentlyContinue).Count -gt 0

    if (-not $hadShell -and -not $hadServer) {
        return @{ HadShell = $false; HadServer = $false }
    }

    # 先尝试普通权限停止
    foreach ($name in $targets) {
        Get-Process -Name $name -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    }

    $deadline = [DateTime]::UtcNow.AddMilliseconds(1500)
    while ([DateTime]::UtcNow -lt $deadline) {
        $remaining = @(Get-Process -Name $targets -ErrorAction SilentlyContinue)
        if ($remaining.Count -eq 0) {
            return @{ HadShell = $hadShell; HadServer = $hadServer }
        }
        Start-Sleep -Milliseconds 150
    }

    # shijian-shell 计划任务以 HighestAvailable 启动时，非管理员终端无法直接结束它；通过一次性 UAC 提权停止
    Write-Host '[server] 安装版后台进程以管理员权限运行，即将请求一次 UAC 权限停止旧进程...'
    $psExe = if (Get-Command powershell.exe -ErrorAction SilentlyContinue) { 'powershell.exe' } else { 'pwsh.exe' }
    $stopCmd = 'Stop-Process -Name shijian-shell,shijian-server -Force -ErrorAction SilentlyContinue'
    try {
        Start-Process -FilePath $psExe -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList @('-NoProfile', '-Command', $stopCmd)
    }
    catch {
        throw '已取消 UAC 提权，无法停止安装版 shijian-shell / shijian-server。可先右键系统托盘图标退出 ShiJian，或用管理员终端运行本脚本。'
    }

    $deadline = [DateTime]::UtcNow.AddMilliseconds(5000)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (@(Get-Process -Name $targets -ErrorAction SilentlyContinue).Count -eq 0) {
            return @{ HadShell = $hadShell; HadServer = $hadServer }
        }
        Start-Sleep -Milliseconds 150
    }

    throw '等待进程 shijian-shell / shijian-server 退出超时，请先右键系统托盘图标退出 ShiJian 后重试。'
}

function Wait-ServerHealthy {
    param(
        [System.Diagnostics.Process]$Process,
        [string]$HealthUrl,
        [int]$TimeoutSec = 15
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($Process.HasExited) {
            $logDir = [IO.Path]::Combine((Split-Path -Parent $Process.Path), 'Logs')
            throw ("本地 shijian-server 启动后立即退出（退出码 {0}），可查看日志目录：{1}" -f $Process.ExitCode, $logDir)
        }

        try {
            $resp = Invoke-WebRequest -Uri $HealthUrl -UseBasicParsing -TimeoutSec 2 -ErrorAction Stop
            if ($resp.StatusCode -eq 200) {
                return
            }
        }
        catch {
            # 尚未监听就绪，继续轮询
        }
        Start-Sleep -Milliseconds 250
    }

    throw ("等待本地 shijian-server 就绪超时：{0}" -f $HealthUrl)
}

function Restore-ServerStack {
    param(
        [bool]$HadShell,
        [bool]$HadServer,
        [string]$InstallDir
    )

    if ($HadShell) {
        $shellExe = [IO.Path]::Combine($InstallDir, 'shijian-shell.exe')
        Write-Host '[restore] 正在恢复安装版 shijian-shell...'
        $restored = $false
        if (Get-Command schtasks.exe -ErrorAction SilentlyContinue) {
            try {
                & schtasks.exe /run /tn ShiJianShell *> $null
                if ($LASTEXITCODE -eq 0) {
                    $restored = $true
                }
            }
            catch {
                # 非管理员在 PS 5.1 下运行高权限计划任务可能失败，回退直接启动进程
            }
        }
        if (-not $restored -and (Test-Path $shellExe)) {
            Start-Process -FilePath $shellExe -WorkingDirectory $InstallDir | Out-Null
        }
    }
    elseif ($HadServer) {
        $installedServerExe = [IO.Path]::Combine($InstallDir, 'shijian-server.exe')
        if (Test-Path $installedServerExe) {
            Write-Host '[restore] 正在恢复安装版 shijian-server...'
            Start-Process -FilePath $installedServerExe -WorkingDirectory $InstallDir -WindowStyle Hidden | Out-Null
        }
    }
}

if ($Stop) {
    Write-Host '== 正在停止本地调试环境并恢复安装版服务 =='
    Get-Process -Name 'ShiJian' -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -like "*$root*" -or $_.Path -like "*Debug*"
    } | Stop-Process -Force -ErrorAction SilentlyContinue

    Get-Process -Name 'shijian-server' -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -like "*$root*" -or $_.Path -like "*target*debug*"
    } | Stop-Process -Force -ErrorAction SilentlyContinue

    Restore-ServerStack -HadShell $true -HadServer $true -InstallDir $InstallDir
    Write-Host '== 调试环境已清理，安装版服务已恢复 =='
    return
}

$serverUrl = ("http://127.0.0.1:{0}" -f $Port)
$hadShell = $false
$hadServer = $false
$serverProc = $null
$clientProc = $null
$prevServerEnv = $env:SHIJIAN_SERVER
$prevWebUiEnv = $env:SHIJIAN_WEBUI_DIR
$prevDataDirEnv = $env:SHIJIAN_DATA_DIR
$changedServerEnv = $false
$changedWebUiEnv = $false
$changedDataDirEnv = $false
$pushedLocation = $false
$runFailed = $false

try {
    Push-Location $root
    $pushedLocation = $true

    Write-Host '== ShiJian 本地一键预览 =='
    Write-Host ("安装目录: {0}" -f $InstallDir)

    Copy-IconFolder `
        -Source ([IO.Path]::Combine($InstallDir, 'Data', 'AppIcons')) `
        -Destination ([IO.Path]::Combine($outDir, 'Data', 'AppIcons')) `
        -Label '应用图标'

    Copy-IconFolder `
        -Source ([IO.Path]::Combine($InstallDir, 'WebFavicons')) `
        -Destination ([IO.Path]::Combine($outDir, 'WebFavicons')) `
        -Label '网站图标'

    if (-not $ClientOnly) {
        if (-not $NoBuild) {
            if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
                throw '未找到 cargo；如仅需连接已有服务端预览客户端，可改用：./script/dev-run.ps1 -ClientOnly'
            }
            Write-Host ''
            Write-Host '[build] 正在编译 shijian-server (debug)...'
            & cargo build --manifest-path $serverManifest --locked
            if ($LASTEXITCODE -ne 0) {
                throw ("shijian-server 编译失败（退出码 {0}）" -f $LASTEXITCODE)
            }
        }

        if (-not (Test-Path $serverExe)) {
            throw ("未找到 shijian-server 构建产物：{0}（请去掉 -NoBuild 重试）" -f $serverExe)
        }

        # shijian-server 使用全局单实例互斥锁，若不先停掉旧实例，新版起不来；
        # 同时 shijian-shell 会自动拉起旧版 server，因此也要先暂停 shell。
        $stopResult = Stop-ServerStack
        $hadShell = $stopResult.HadShell
        $hadServer = $stopResult.HadServer
        if ($hadShell -or $hadServer) {
            Write-Host '[server] 已临时停止安装版后台进程（退出预览时会自动恢复）'
        }

        $env:SHIJIAN_SERVER = $serverUrl
        $changedServerEnv = $true

        $installWebUi = [IO.Path]::Combine($InstallDir, 'webui')
        if ((Test-Path $installWebUi) -and [string]::IsNullOrEmpty($prevWebUiEnv)) {
            $env:SHIJIAN_WEBUI_DIR = $installWebUi
            $changedWebUiEnv = $true
        }

        $serverArgs = @()
        $installDataDir = [IO.Path]::GetFullPath([IO.Path]::Combine($InstallDir, 'Data'))
        if (Test-Path $installDataDir) {
            $env:SHIJIAN_DATA_DIR = $installDataDir
            $changedDataDirEnv = $true
            # Start-Process 拼接参数数组时不会自动给带空格的路径加引号，必须手动加双引号
            $serverArgs += @('--data-dir', ('"{0}"' -f $installDataDir))
            Write-Host ("[server] 数据目录：{0}" -f $installDataDir)
        }

        Write-Host ("[server] 正在启动本地调试版 shijian-server（{0}）..." -f $serverUrl)
        $serverProc = Start-Process `
            -FilePath $serverExe `
            -ArgumentList $serverArgs `
            -WorkingDirectory (Split-Path -Parent $serverExe) `
            -PassThru `
            -WindowStyle Hidden

        Wait-ServerHealthy -Process $serverProc -HealthUrl ("{0}/api/health" -f $serverUrl)
        Write-Host '[server] 本地新版服务端已就绪'
    }
    elseif ($Port -ne 37091) {
        $env:SHIJIAN_SERVER = $serverUrl
        $changedServerEnv = $true
    }

    # 清理可能残留的前次调试版客户端，确保只有一个调试窗口供 GUI 自动化与视觉检查
    Get-Process -Name 'ShiJian' -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -like "*$root*" -or $_.Path -like "*Debug*"
    } | Stop-Process -Force -ErrorAction SilentlyContinue

    $clientExe = [IO.Path]::Combine($outDir, 'ShiJian.exe')

    if (-not $NoBuild) {
        Write-Host ''
        Write-Host '[build] 正在编译 ShiJian.Client (Debug)...'
        & dotnet build $clientProject -c Debug --nologo
        if ($LASTEXITCODE -ne 0) {
            throw ("ShiJian.Client 编译失败（退出码 {0}）" -f $LASTEXITCODE)
        }
    }

    if (-not (Test-Path $clientExe)) {
        throw ("未找到客户端可执行文件：{0}（请去掉 -NoBuild 重试）" -f $clientExe)
    }

    Write-Host ''
    Write-Host ("[client] 正在启动调试版客户端（server: {0}）..." -f $serverUrl)
    Write-Host ''

    if ($Detached) {
        $clientProc = Start-ClientDetachedProcess `
            -FilePath $clientExe `
            -WorkingDirectory $outDir

        # 后台分离模式：等待客户端主窗口就绪后立即返回控制权，保持进程存活
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        $windowReady = $false
        while ([DateTime]::UtcNow -lt $deadline) {
            if ($clientProc.HasExited) {
                throw ("客户端启动后异常退出（退出码 {0}）" -f $clientProc.ExitCode)
            }
            $clientProc.Refresh()
            if ($clientProc.MainWindowHandle -ne 0 -or ($clientProc.MainWindowTitle -like '*时见*')) {
                $windowReady = $true
                break
            }
            Start-Sleep -Milliseconds 300
        }
        Start-Sleep -Seconds 1
        Write-Host ("[client] 调试版客户端已就绪（PID: {0}，MainWindowHandle: {1}）" -f $clientProc.Id, $clientProc.MainWindowHandle)
    }
    else {
        # 前台交互模式：保持原有体验，阻塞直到用户关闭窗口
        $clientProc = Start-Process `
            -FilePath $clientExe `
            -WorkingDirectory $outDir `
            -PassThru

        $null = $clientProc.WaitForExit()
        if ($clientProc.ExitCode -ne 0) {
            throw ("ShiJian.Client 退出异常（退出码 {0}）" -f $clientProc.ExitCode)
        }
    }
}
catch {
    $runFailed = $true
    Write-Host ''
    Write-Host ("[error] {0}" -f $_.Exception.Message) -ForegroundColor Red
}
finally {
    if (-not $Detached) {
        if ($null -ne $serverProc -and -not $serverProc.HasExited) {
            Write-Host ''
            Write-Host '[cleanup] 正在停止本地调试版 shijian-server...'
            Stop-Process -Id $serverProc.Id -Force -ErrorAction SilentlyContinue
            try { $null = $serverProc.WaitForExit(3000) } catch {}
        }

        if ($changedServerEnv) {
            if ($null -eq $prevServerEnv) { Remove-Item Env:SHIJIAN_SERVER -ErrorAction SilentlyContinue }
            else { $env:SHIJIAN_SERVER = $prevServerEnv }
        }
        if ($changedWebUiEnv) {
            if ($null -eq $prevWebUiEnv) { Remove-Item Env:SHIJIAN_WEBUI_DIR -ErrorAction SilentlyContinue }
            else { $env:SHIJIAN_WEBUI_DIR = $prevWebUiEnv }
        }
        if ($changedDataDirEnv) {
            if ($null -eq $prevDataDirEnv) { Remove-Item Env:SHIJIAN_DATA_DIR -ErrorAction SilentlyContinue }
            else { $env:SHIJIAN_DATA_DIR = $prevDataDirEnv }
        }

        Restore-ServerStack -HadShell $hadShell -HadServer $hadServer -InstallDir $InstallDir
    }
    else {
        Write-Host ''
        $cId = if ($clientProc) { $clientProc.Id } else { '未知' }
        $sId = if ($serverProc -and -not $serverProc.HasExited) { (', 服务端 PID: {0}' -f $serverProc.Id) } else { '' }
        Write-Host ("[detached] 调试环境保持运行中（客户端 PID: {0}{1}）。" -f $cId, $sId)
        Write-Host '[detached] 验证完毕后可执行 ./script/dev-run.ps1 -Stop 停止并恢复原服务。'
    }

    if ($pushedLocation) {
        Pop-Location
    }

    if ($runFailed) {
        # 从资源管理器右键“使用 PowerShell 运行”时会新开控制台并在脚本结束时立刻关窗；出错时暂停方便看清报错
        $launchedFromExplorer = ($Host.UI.RawUI.WindowTitle -like 'PowerShell 7*') -or ([Environment]::GetCommandLineArgs() -contains '-File')
        if ($launchedFromExplorer -and -not [Console]::IsInputRedirected) {
            Write-Host ''
            $null = Read-Host '预览启动失败，按回车键关闭窗口'
        }
        exit 1
    }
}
