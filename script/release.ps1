#Requires -Version 5.1
<#
.SYNOPSIS
    打 tag 并触发 GitHub Actions 打包发布 Release。

.DESCRIPTION
    封装 fork 上的发版流程，避免每次手敲：

      1. 预检：gh 登录、当前分支、工作区是否干净、tag 是否已存在（本地/远端）、Release 是否已存在
      2. （可选 -Commit）把当前改动提交掉
      3. 打 annotated tag  v<Version>
      4. 推送 main 和 tag
      5. 手动触发 release.yml，并把 --ref 指到 tag 上

    第 5 步的 --ref 是关键：在 fork 上 push tag 不会自动触发 Actions，
    必须用 workflow_dispatch；而只有 --ref 指向 tag 时 github.ref 才是 refs/tags/...，
    workflow 里 release job 的 if 条件才成立，才会自动建 Release 并上传产物。

.PARAMETER Version
    版本号，不带前导 v。例如 1.3.4-fork.2；省略时按上一个 tag 自动递增

.PARAMETER Message
    tag 说明，同时作为 -Commit 时的提交信息。默认 "release v<Version>"

.PARAMETER Commit
    工作区不干净时，把所有改动提交后再发版。不加这个开关时遇到未提交改动会直接中止

.PARAMETER Watch
    触发后等待构建结束并输出 Release 链接（约 15~25 分钟）

.PARAMETER DryRun
    只做预检并打印将要执行的命令，不改动任何东西

.EXAMPLE
    ./script/release.ps1 -Version 1.3.4-fork.2

.EXAMPLE
    ./script/release.ps1 -Version 1.3.4-fork.2 -Commit -Watch

.EXAMPLE
    ./script/release.ps1 -Version 1.3.4-fork.3 -DryRun
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Version,

    [string]$Message,

    [switch]$Commit,

    [switch]$Watch,

    [switch]$DryRun
)

# 原生命令（git/gh）往 stderr 写东西是常态（进度、警告、查询失败），
# 在 Stop 模式下会被当成终止性异常，这里统一用 Continue + 检查 $LASTEXITCODE
$ErrorActionPreference = 'Continue'

# 不传版本号就按上一个 tag 自动递增（规则见 prepare-release.ps1）
if ([string]::IsNullOrWhiteSpace($Version))
{
    $autoVersion = & (Join-Path $PSScriptRoot 'prepare-release.ps1') -Quiet
    if ([string]::IsNullOrWhiteSpace($autoVersion)) {
        Write-Host '无法自动推出下一个版本号，请用 -Version 指定' -ForegroundColor Red
        exit 1
    }
    $Version = "$autoVersion".Trim()
    Write-Host ("版本号自动递增: {0}" -f $Version) -ForegroundColor DarkGray
}

$Version = $Version.Trim()
if ($Version.StartsWith('v')) { $Version = $Version.Substring(1) }

if ($Version -notmatch '^\d+\.\d+(\.\d+)?(-.+)?$') {
    Write-Host ("版本号格式不对：'{0}'，应形如 2.0、2.1 或 1.3.4-fork.2" -f $Version) -ForegroundColor Red
    exit 1
}

$Tag = "v$Version"
if (-not $Message) { $Message = "release $Tag" }

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root

$tagCreated = $false

function Write-Step {
    param([string]$Text)
    Write-Host ''
    Write-Host $Text -ForegroundColor Cyan
}

# 执行 git/gh 命令，失败即抛；输出直接打到控制台
function Invoke-Native {
    param(
        [string]$Exe,
        [string[]]$Arguments,
        [string]$Hint
    )

    $display = "$Exe $($Arguments -join ' ')"
    if ($DryRun) {
        Write-Host "[dry] $display" -ForegroundColor DarkGray
        return
    }

    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) {
        $msg = "$display 失败（exit $LASTEXITCODE）"
        if ($Hint) { $msg = "$msg`n$Hint" }
        throw $msg
    }
}

# 取命令的标准输出（stderr 丢弃）；失败返回 $null
function Get-NativeOutput {
    param(
        [string]$Exe,
        [string[]]$Arguments
    )

    $lines = & $Exe @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    return (($lines | ForEach-Object { "$_" }) -join "`n")
}

function Test-TagExists {
    param([string]$Name)

    & git rev-parse --verify --quiet "refs/tags/$Name" *> $null
    if ($LASTEXITCODE -eq 0) { return $true }

    $remote = & git ls-remote --tags origin "refs/tags/$Name" 2>$null
    if ($LASTEXITCODE -eq 0 -and $remote) { return $true }

    return $false
}

function Get-OriginSlug {
    $url = Get-NativeOutput git @('remote', 'get-url', 'origin')
    if (-not $url) {
        throw '拿不到 origin 远端，先确认这是从 fork 克隆的仓库'
    }

    if ($url -match 'github\.com[:/](?<slug>[^/\s]+/[^/\s]+?)(\.git)?$') {
        return $Matches['slug']
    }

    throw "origin 不是 GitHub 地址：$url"
}

try {
    Write-Host '════ ShiJian 发版 ════' -ForegroundColor Cyan
    Write-Host ("版本: {0}" -f $Tag)
    if ($DryRun) { Write-Host '模式: DryRun（只预检，不做任何改动）' -ForegroundColor Yellow }

    # ── 预检 ─────────────────────────────────────────────────────────────────
    Write-Step '[1/5] 预检'

    & gh auth status *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'gh 未登录，先执行 gh auth login'
    }

    $slug = Get-OriginSlug
    Write-Host ("  仓库     : {0}" -f $slug)

    $branch = Get-NativeOutput git @('rev-parse', '--abbrev-ref', 'HEAD')
    Write-Host ("  当前分支 : {0}" -f $branch)
    if ($branch -ne 'main') {
        throw "当前不在 main 分支（$branch），请先切回 main"
    }

    if (Test-TagExists -Name $Tag) {
        throw "tag $Tag 已存在（本地或远端），换个版本号，或先删掉旧 tag"
    }
    Write-Host ("  tag      : {0} 可用" -f $Tag) -ForegroundColor DarkGray

    & gh release view $Tag -R $slug *> $null
    if ($LASTEXITCODE -eq 0) {
        throw "Release $Tag 已存在，换个版本号"
    }

    $dirty = Get-NativeOutput git @('status', '--porcelain')
    if ($dirty) {
        if (-not $Commit) {
            Write-Host ''
            Write-Host '  工作区有未提交改动：' -ForegroundColor Yellow
            $dirty -split "`n" | Select-Object -First 10 | ForEach-Object { Write-Host ("    $_") }
            throw '请先提交，或者加 -Commit 让脚本代提交'
        }
        Write-Host '  工作区   : 有改动，将随本次发版一起提交' -ForegroundColor Yellow
    }
    else {
        Write-Host '  工作区   : 干净'
    }

    # ── 提交（可选） ─────────────────────────────────────────────────────────
    Write-Step '[2/5] 提交改动'
    if ($dirty -and $Commit) {
        Invoke-Native git @('add', '-A')
        Invoke-Native git @('commit', '-m', $Message)
        if (-not $DryRun) { Write-Host '  已提交' }
    }
    else {
        Write-Host '  跳过（无需提交）'
    }

    # ── 打 tag ───────────────────────────────────────────────────────────────
    Write-Step '[3/5] 打 tag'
    Invoke-Native git @('tag', '-a', $Tag, '-m', $Message) -Hint "清理本地 tag: git tag -d $Tag"
    $tagCreated = $true
    if (-not $DryRun) {
        $sha = Get-NativeOutput git @('rev-parse', '--short', 'HEAD')
        Write-Host ("  {0} -> {1}" -f $Tag, $sha)
    }

    # ── 推送 ─────────────────────────────────────────────────────────────────
    Write-Step '[4/5] 推送'
    Invoke-Native git @('push', 'origin', 'main')
    Invoke-Native git @('push', 'origin', $Tag)
    if (-not $DryRun) { Write-Host ("  main 与 {0} 已推送" -f $Tag) }

    # ── 触发 Actions ─────────────────────────────────────────────────────────
    Write-Step '[5/5] 触发 Actions（--ref 指向 tag，否则不会建 Release）'

    if ($DryRun) {
        Write-Host ("[dry] gh workflow run release.yml -R {0} --ref {1} -f version={2}" -f $slug, $Tag, $Version) -ForegroundColor DarkGray
        Write-Host ''
        Write-Host 'DryRun 结束，未做任何改动' -ForegroundColor Yellow
        return
    }

    Invoke-Native gh @('workflow', 'run', 'release.yml', '-R', $slug, '--ref', $Tag, '-f', "version=$Version") `
        -Hint "tag 已推送成功，也可以到 Actions 页面手动 Run workflow 并选择 $Tag"

    # 等一小会儿让 run 出现在列表里。
    # 注意不要用 --jq 带引号表达式：PowerShell 5.1 传给原生命令时会吞掉内嵌双引号，
    # jq 会收到 .headBranch==v1.3.4 这种非法 token。这里在 PS 里解析 JSON 再过滤。
    $runId = $null
    for ($i = 0; $i -lt 10 -and -not $runId; $i++) {
        Start-Sleep -Seconds 2
        $json = Get-NativeOutput gh @('run', 'list', '-R', $slug, '--limit', '10', '--json', 'databaseId,headBranch')
        if (-not $json) { continue }

        try {
            $runs = $json | ConvertFrom-Json
        }
        catch {
            continue
        }

        $hit = $runs | Where-Object { $_.headBranch -eq $Tag } | Select-Object -First 1
        if ($hit) { $runId = "$($hit.databaseId)" }
    }

    Write-Host ''
    if ($runId) {
        Write-Host ("构建已启动: https://github.com/{0}/actions/runs/{1}" -f $slug, $runId)
    }
    else {
        Write-Host ("构建已启动，进度见: https://github.com/{0}/actions" -f $slug)
    }

    if ($Watch -and $runId) {
        Write-Host ''
        Write-Host '等待构建结束（约 15~25 分钟）...' -ForegroundColor Yellow
        & gh run watch $runId -R $slug --exit-status
        if ($LASTEXITCODE -ne 0) {
            throw ("构建失败，日志: https://github.com/{0}/actions/runs/{1}" -f $slug, $runId)
        }
        Write-Host ''
        Write-Host ("Release: https://github.com/{0}/releases/tag/{1}" -f $slug, $Tag) -ForegroundColor Green
    }
    else {
        Write-Host ''
        Write-Host ("产物会出现在: https://github.com/{0}/releases/tag/{1}" -f $slug, $Tag)
        Write-Host '加 -Watch 可以让脚本等构建结束并校验结果'
    }
}
catch {
    Write-Host ''
    Write-Host ("失败: {0}" -f $_.Exception.Message) -ForegroundColor Red
    if ($tagCreated -and -not $DryRun) {
        Write-Host ''
        Write-Host ("提示：本地 tag {0} 已创建。若要重来，先 git tag -d {0}（推过远端还要 git push origin :refs/tags/{0}）" -f $Tag) -ForegroundColor Yellow
    }
    exit 1
}
finally {
    Pop-Location
}
