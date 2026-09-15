# control-massiveslicer.ps1 - drive MassiveSLICER via LocalControlBridge
param(
  [Parameter(Position=0)][string]$Action = 'help',
  [Parameter(Position=1, ValueFromRemainingArguments=$true)][string[]]$Rest,
  [int]$Port = 0,
  [string]$Path = '',
  [int]$N = 40
)
$ErrorActionPreference = 'Stop'

function Get-BridgePort {
  if ($Port -gt 0) { return $Port }
  $f = Join-Path $env:LOCALAPPDATA 'MassiveSlicer\bridge.port'
  if (-not (Test-Path $f)) { throw "bridge.port not found at $f - is MassiveSlicer running?" }
  $p = [int]((Get-Content $f -Raw).Trim())
  if ($p -lt 1) { throw "invalid port in $f" }
  return $p
}

function Invoke-Bridge {
  param([string]$Method,[string]$Rel,[string]$Body,[string]$OutFile)
  $p = Get-BridgePort
  $uri = "http://127.0.0.1:$p$Rel"
  if (-not [string]::IsNullOrEmpty($OutFile)) {
    Invoke-WebRequest -Method $Method -Uri $uri -OutFile $OutFile -TimeoutSec 120 | Out-Null
    return @{ ok = $true; path = (Resolve-Path $OutFile).Path; bytes = (Get-Item $OutFile).Length }
  }
  if (-not [string]::IsNullOrEmpty($Body)) {
    return Invoke-RestMethod -Method $Method -Uri $uri -ContentType 'application/json' -Body $Body -TimeoutSec 120
  }
  return Invoke-RestMethod -Method $Method -Uri $uri -TimeoutSec 60
}

function Write-Json($obj) { $obj | ConvertTo-Json -Depth 8 -Compress }

switch ($Action.ToLowerInvariant()) {
  'help' {
    Write-Output 'control-massiveslicer.ps1 actions:'
    Write-Output '  doctor'
    Write-Output '  ping'
    Write-Output '  status'
    Write-Output '  console [-N 40]'
    Write-Output '  screenshot [-Path out.png]'
    Write-Output '  command TEXT'
    Write-Output '  open PATH.mass'
    Write-Output '  help'
    exit 0
  }
  'ping' {
    $r = Invoke-Bridge GET '/ping'
    Write-Json $r
    if (-not $r.ok) { exit 1 }
  }
  'status' {
    $r = Invoke-Bridge GET '/status'
    Write-Json $r
    if (-not $r.ok) { exit 1 }
  }
  'console' {
    $rel = '/console?n=' + $N
    $r = Invoke-Bridge GET $rel
    Write-Json $r
    if (-not $r.ok) { exit 1 }
  }
  'screenshot' {
    if ([string]::IsNullOrWhiteSpace($Path)) {
      $dir = Join-Path $PSScriptRoot 'artifacts\latest'
      New-Item -ItemType Directory -Force -Path $dir | Out-Null
      $Path = Join-Path $dir ('shot_{0:yyyyMMdd_HHmmss}.png' -f (Get-Date))
    }
    $r = Invoke-Bridge GET '/screenshot?format=png' -OutFile $Path
    Write-Json $r
  }
  'command' {
    $cmd = ($Rest -join ' ').Trim()
    if (-not $cmd) { throw 'command requires text, e.g. command help' }
    $body = (@{ command = $cmd } | ConvertTo-Json -Compress)
    $r = Invoke-Bridge POST '/command' -Body $body
    Write-Json $r
    if (-not $r.ok) { exit 1 }
  }
  'open' {
    $openPath = if ($Path) { $Path } else { ($Rest -join ' ').Trim() }
    if (-not $openPath) { throw 'open requires a .mass path' }
    $body = (@{ path = $openPath } | ConvertTo-Json -Compress)
    $r = Invoke-Bridge POST '/open' -Body $body
    Write-Json $r
    if (-not $r.ok) { exit 1 }
  }
  'doctor' {
    $p = Get-BridgePort
    $ping = Invoke-Bridge GET '/ping'
    $procs = @(Get-Process -Name 'MassiveSlicer.App' -ErrorAction SilentlyContinue)
    $report = [ordered]@{
      ok = [bool]($ping.ok -and $procs.Count -ge 1)
      port = $p
      ping = $ping
      processCount = $procs.Count
      pids = @($procs | ForEach-Object { $_.Id })
      bridgePortFile = (Join-Path $env:LOCALAPPDATA 'MassiveSlicer\bridge.port')
    }
    Write-Json $report
    if (-not $report.ok) { exit 1 }
  }
  default { throw ("unknown action: " + $Action) }
}
