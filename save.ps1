#Requires -Version 5.1
<#
.SYNOPSIS
    Save MassiveSLICER: commit real work, pull, push. Shop PC (PowerShell).

.DESCRIPTION
    Does NOT stash - stash breaks on this SMB share
    ("unable to create file save.sh: File exists").
    Flow: stage real files -> commit if needed -> pull -> push.
    Every git call uses: git -c safe.directory=*

.EXAMPLE
    .\save.ps1
    .\save.ps1 "fixed KRL export"
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$Message,
    [switch]$NoPause
)

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$script:GitSafe = @('-c', 'safe.directory=*')

function Say {
    param(
        [string]$Text,
        [string]$Color = 'Cyan'
    )
    $ts = Get-Date -Format 'HH:mm:ss'
    Write-Host "[$ts] $Text" -ForegroundColor $Color
    try { [Console]::Out.Flush() } catch {}
}

function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$GitArgs
    )
    $all = $script:GitSafe + $GitArgs
    Say ("git " + ($GitArgs -join ' ')) 'DarkGray'
    & git @all
    $code = $LASTEXITCODE
    if ($code -ne 0) { Say "  exit $code" 'Yellow' }
    return $code
}

function Git-Out {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$GitArgs
    )
    $all = $script:GitSafe + $GitArgs
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $out = & git @all 2>&1 | ForEach-Object { "$_" }
    $ErrorActionPreference = $prev
    return ($out -join "`n").Trim()
}

function Add-SafeDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    $norm = ($Path -replace '\\', '/').TrimEnd('/')
    $have = @()
    try {
        $have = @(git -c safe.directory=* config --global --get-all safe.directory 2>$null)
    } catch {}
    if ($have -notcontains $norm) {
        git -c safe.directory=* config --global --add safe.directory $norm 2>$null | Out-Null
        Say "safe.directory (global) += $norm" 'DarkGray'
    }
}

function Unstage-ChmodOnly {
    $numstat = Git-Out @('diff', '--cached', '--numstat')
    $skipped = 0
    foreach ($line in ($numstat -split "`n")) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $parts = $line -split "`t", 3
        if ($parts.Count -lt 3) { continue }
        if ($parts[0] -eq '0' -and $parts[1] -eq '0') {
            Invoke-Git @('reset', '-q', 'HEAD', '--', $parts[2]) | Out-Null
            $skipped++
        }
    }
    if ($skipped -gt 0) {
        Say "unstaged $skipped chmod-only file(s)" 'DarkGray'
    }
}

Say "=== MassiveSLICER save.ps1 ===" 'Green'
Say "folder: $PSScriptRoot"
Set-Location -LiteralPath $PSScriptRoot
Say "cwd:    $(Get-Location)"

Add-SafeDirectory $PSScriptRoot
Add-SafeDirectory 'Z:/Research/LFAM/MassiveSLICER'
Add-SafeDirectory '//192.168.0.191/MassiveFILES/Research/LFAM/MassiveSLICER'
Add-SafeDirectory '*'
Say "using: git -c safe.directory=*  (no stash - SMB-safe)" 'DarkGray'

Invoke-Git @('config', 'core.filemode', 'false') | Out-Null
Invoke-Git @('config', 'core.trustctime', 'false') | Out-Null
Invoke-Git @('config', 'core.untrackedCache', 'false') | Out-Null

$lock = Join-Path $PSScriptRoot '.git\index.lock'
if (Test-Path -LiteralPath $lock) {
    $gitRunning = Get-Process -Name git -ErrorAction SilentlyContinue
    if ($gitRunning) {
        Say "STOP: .git/index.lock exists and git is running. Wait, then retry." 'Red'
        if (-not $NoPause) { Read-Host "Press Enter" | Out-Null }
        exit 1
    }
    Say "removing stale .git/index.lock" 'Yellow'
    Remove-Item -LiteralPath $lock -Force
}

# Drop empty / broken auto-stashes from older save.ps1 runs that failed mid-stash.
$stashList = Git-Out @('stash', 'list')
if ($stashList -match 'save\.ps1 auto-stash|save\.sh auto-stash') {
    Say "dropping leftover save auto-stash (previous failed run)..." 'Yellow'
    Invoke-Git @('stash', 'clear') | Out-Null
}

Invoke-Git @('update-index', '--refresh') | Out-Null

$inside = Git-Out @('rev-parse', '--is-inside-work-tree')
Say "rev-parse --is-inside-work-tree => '$inside'"
if ($inside -ne 'true') {
    Say "STOP: Git still does not see a repo here." 'Red'
    Say '  git -c safe.directory=* status' 'Yellow'
    if (-not $NoPause) { Read-Host "Press Enter" | Out-Null }
    exit 1
}

if ($Message -and -not [string]::IsNullOrWhiteSpace(($Message -join ' '))) {
    $msg = ($Message -join ' ').Trim()
} else {
    $msg = "wip: save progress ($(Get-Date -Format 'yyyy-MM-dd HH:mm'))"
}

$branch = (Git-Out @('rev-parse', '--abbrev-ref', 'HEAD'))
$before = (Git-Out @('log', '-1', '--oneline'))
Say "branch: $branch"
Say "HEAD:   $before"
Say "message: $msg"

Say "--- status before ---" 'White'
Invoke-Git @('status', '-sb') | Out-Null
Invoke-Git @('diff', '--stat') | Out-Null

# 1) Commit local work FIRST (no stash - SMB cannot reset save.sh cleanly).
Say "staging real files..."
Invoke-Git @('add', '-A') | Out-Null
Invoke-Git @('reset', '-q', '--', 'install.sh') | Out-Null
Unstage-ChmodOnly

Say "--- staged ---" 'White'
Invoke-Git @('diff', '--cached', '--stat') | Out-Null

$null = Invoke-Git @('diff', '--cached', '--quiet')
if ($LASTEXITCODE -eq 0) {
    Say "nothing new to commit" 'Yellow'
} else {
    Say "committing: $msg"
    if ((Invoke-Git @('commit', '-m', $msg)) -ne 0) {
        Say "STOP: commit failed." 'Red'
        if (-not $NoPause) { Read-Host "Press Enter" | Out-Null }
        exit 1
    }
    Say "commit OK" 'Green'
}

# 2) Pull teammates' work
Say "pulling origin/$branch ..."
if ((Invoke-Git @('pull', '--no-edit', 'origin', $branch)) -ne 0) {
    Say "STOP: pull failed (likely merge conflict)." 'Red'
    Say "Fix conflicts, then commit and push by hand." 'Yellow'
    if (-not $NoPause) { Read-Host "Press Enter" | Out-Null }
    exit 1
}
Say "pull OK" 'Green'

# 3) Push
Say "pushing origin/$branch ..."
if ((Invoke-Git @('push', 'origin', $branch)) -ne 0) {
    Say "STOP: push failed. Pull, fix, push. Do NOT force-push." 'Red'
    if (-not $NoPause) { Read-Host "Press Enter" | Out-Null }
    exit 1
}

$after = (Git-Out @('log', '-1', '--oneline'))
Say "--- status after ---" 'White'
Invoke-Git @('status', '-sb') | Out-Null
Say "HEAD now: $after" 'Green'
Say "=== DONE. $branch is on GitHub. ===" 'Green'

if (-not $NoPause) {
    Write-Host ""
    Read-Host "Press Enter to close"
}
