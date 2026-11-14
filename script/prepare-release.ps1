#Requires -Version 5.1
<#
.SYNOPSIS
    发版前准备：算下一个版本号，并按 commit 生成 release notes 草稿。

.DESCRIPTION
    发版还剩两件纯手工的事，这个脚本把它们变成「看一眼、改两行」：

      1. 版本号：读上一个 tag（形如 v1.3.4-fork.3），自动递增成 v1.3.4-fork.4；
         没有 tag 时从 1.0.0-fork.1 起算，也可以用 -Version 手工指定
      2. release notes：把「上一个 tag 到 HEAD」的提交按「新增 / 修复 / 其他」分类，
         填进 release_notes.txt 的骨架里，输出成草稿

    默认只写草稿到 .tmp/release-notes-draft.md，不碰 release_notes.txt；
    加 -Apply 才会把草稿写进 release_notes.txt（覆盖），之后自己补两句人话再发版。

.PARAMETER Version
    手工指定版本号（不带 v）。不传就按上一个 tag 自动递增。

.PARAMETER Apply
    把草稿写入 release_notes.txt（会覆盖原文件，注意先提交或备份）。

.PARAMETER Quiet
    只输出算出来的版本号，方便脚本里取用。

.EXAMPLE
    .\script\prepare-release.ps1

.EXAMPLE
    .\script\prepare-release.ps1 -Apply
#>
[CmdletBinding()]
param(
    [string]$Version,

    [switch]$Apply,

    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

function Invoke-Git {
    param([string[]]$Arguments)

    $output = & git @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    return (($output | ForEach-Object { "$_" }) -join "`n").Trim()
}

function Get-LatestTag {
    $tag = Invoke-Git @('describe', '--tags', '--abbrev=0', '--match', 'v*')
    if ([string]::IsNullOrWhiteSpace($tag)) { return $null }
    return $tag
}

# v2.0           -> 2.1
# v2.1           -> 2.2
# v1.3.4-fork.3  -> 2.0 (已进入 2.0 正式版体系)
# 无 tag          -> 2.0
function Get-NextVersion {
    param([string]$LatestTag)

    if ([string]::IsNullOrWhiteSpace($LatestTag)) { return '2.0' }

    $version = $LatestTag.TrimStart('v')

    # 2.0 / 2.1 递增为 2.1 / 2.2
    if ($version -match '^(?<major>\d+)\.(?<minor>\d+)$') {
        $nextMinor = [int]$Matches['minor'] + 1
        return "$($Matches['major']).$nextMinor"
    }

    # 三段式 2.0.0 递增
    if ($version -match '^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)$') {
        $nextPatch = [int]$Matches['patch'] + 1
        return "$($Matches['major']).$($Matches['minor']).$nextPatch"
    }

    # 历史 fork.X 规则平滑收口切入 2.0
    if ($version -match '-fork\.') {
        return '2.0'
    }

    return '2.0'
}

function Get-Commits {
    param([string]$SinceTag)

    $range = if ([string]::IsNullOrWhiteSpace($SinceTag)) { 'HEAD' } else { "$SinceTag..HEAD" }
    $log = Invoke-Git @('log', $range, '--pretty=format:%h%x09%s', '--no-merges')
    if ([string]::IsNullOrWhiteSpace($log)) { return @() }

    return @($log -split "`n" | Where-Object { $_ -match "`t" } | ForEach-Object {
            $parts = $_ -split "`t", 2
            [pscustomobject]@{ Hash = $parts[0]; Subject = $parts[1] }
        })
}

# 关键词归堆：不追求精确，草稿是给人改的
function Get-Bucket {
    param([string]$Subject)

    if ($Subject -match '^(feat|feature)|新增|增加|支持|引入') { return 'added' }
    if ($Subject -match '^(fix|bugfix)|修复|修好|解决|避免') { return 'fixed' }
    if ($Subject -match '^(perf|refactor|chore|docs|test)|优化|重构|文档|测试|整理|清理') { return 'other' }
    return 'other'
}

function Format-Draft {
    param(
        [string]$Version,
        [string]$RangeText,
        [object[]]$Commits
    )

    $Commits = @($Commits | Where-Object { $_ -and -not [string]::IsNullOrWhiteSpace($_.Subject) })

    $added = @($Commits | Where-Object { (Get-Bucket $_.Subject) -eq 'added' })
    $fixed = @($Commits | Where-Object { (Get-Bucket $_.Subject) -eq 'fixed' })
    $others = @($Commits | Where-Object { (Get-Bucket $_.Subject) -eq 'other' })

    $lines = @()
    $lines += "# v$Version 更新日志"
    $lines += ""
    $lines += "> ShiJian（时见）桌面端与内嵌 WebUI 发布更新。"
    $lines += "> 以下是提交列表自动生成的草稿（$RangeText），发版前请补上一句人话的小结。"
    $lines += ""

    if ($added.Count -gt 0) {
        $lines += "## 新增"
        $lines += ""
        foreach ($commit in $added) { $lines += "- $($commit.Subject)（$($commit.Hash)）" }
        $lines += ""
    }

    if ($fixed.Count -gt 0) {
        $lines += "## 修复"
        $lines += ""
        foreach ($commit in $fixed) { $lines += "- $($commit.Subject)（$($commit.Hash)）" }
        $lines += ""
    }

    if ($others.Count -gt 0) {
        $lines += "## 其他"
        $lines += ""
        foreach ($commit in $others) { $lines += "- $($commit.Subject)（$($commit.Hash)）" }
        $lines += ""
    }

    if ($Commits.Count -eq 0) {
        $lines += "（上一个 tag 之后没有新提交）"
        $lines += ""
    }

    $lines += "## 下载与安装"
    $lines += ""
    $lines += "产物由 GitHub Actions 产出，见本页下方 Assets："
    $lines += ""
    $lines += "| 产物 | 文件名 | 适用场景 |"
    $lines += "| --- | --- | --- |"
    $lines += "| Windows 安装器 | ``ShiJian-$Version-Setup.exe`` | 推荐普通用户使用 |"
    $lines += "| Windows 绿色版 | ``ShiJian-$Version-win-x64.zip`` | 便携使用，需手动注册开机自启 |"
    $lines += "| macOS | ``ShiJian-$Version-macos-arm64.dmg`` | Apple Silicon 设备，macOS 12.0+ |"
    $lines += ""
    $lines += "> 安装与升级细节（绿色版注册任务计划、macOS 隔离标记、旧动态库清理等）与上一版一致，"
    $lines += "> 需要时从上一个 release 的说明里复制过来。"

    return ($lines -join "`r`n")
}

try {
    $latestTag = Get-LatestTag
    $rangeText = if ($latestTag) { "$latestTag..HEAD" } else { '初始提交至今' }

    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = Get-NextVersion -LatestTag $latestTag
    }
    $Version = $Version.Trim().TrimStart('v')

    if ($Quiet) {
        Write-Output $Version
        return
    }

    $commits = Get-Commits -SinceTag $latestTag

    Write-Host '════ 发版准备 ════' -ForegroundColor Cyan
    Write-Host ("上一个 tag : {0}" -f ($(if ($latestTag) { $latestTag } else { '（无）' })))
    Write-Host ("提交区间   : {0}" -f $rangeText)
    Write-Host ("新版本号   : v{0}" -f $Version)
    Write-Host ("提交数量   : {0}" -f $commits.Count)

    $draft = Format-Draft -Version $Version -RangeText $rangeText -Commits $commits

    if (-not $Apply) {
        $draftDir = Join-Path $root '.tmp'
        if (-not (Test-Path $draftDir)) { New-Item -ItemType Directory -Path $draftDir | Out-Null }

        $draftPath = Join-Path $draftDir 'release-notes-draft.md'
        [IO.File]::WriteAllText($draftPath, $draft, [Text.UTF8Encoding]::new($false))

        Write-Host ''
        Write-Host ("草稿已写入: {0}" -f $draftPath) -ForegroundColor Green
        Write-Host '看完改完，用 -Apply 覆盖 release_notes.txt，或者直接把内容贴进 release_notes.txt。'
        Write-Host ''
        Write-Host ("下一步: .\script\release.ps1 -Version {0} -Commit" -f $Version) -ForegroundColor Yellow
        return
    }

    $notesPath = Join-Path $root 'release_notes.txt'
    [IO.File]::WriteAllText($notesPath, $draft, [Text.UTF8Encoding]::new($false))

    Write-Host ''
    Write-Host ("release_notes.txt 已更新（v{0}）" -f $Version) -ForegroundColor Green
    Write-Host '请补一句人话小结，然后执行:' -ForegroundColor Yellow
    Write-Host ("  .\script\release.ps1 -Version {0} -Commit" -f $Version)
}
finally {
    Pop-Location
}
