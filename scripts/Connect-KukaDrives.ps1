<#
.SYNOPSIS
    Maps the KUKA controller network shares for LFAM 1/2/3 as persistent drives.

.DESCRIPTION
    Each controller exposes two SMB shares - \krc and \d. This script maps all six to
    drive letters for the KukaUser account, waiting for each controller to come online
    first (the robots typically boot slower than the PC).

        LFAM 1  192.168.0.151   K: -> \krc   L: -> \d
        LFAM 2  192.168.0.152   M: -> \krc   N: -> \d
        LFAM 3  192.168.0.153   O: -> \krc   P: -> \d

    No password is stored in this file. The KukaUser password is entered once and saved
    to the Windows Credential Manager (via cmdkey); every mapping after that - including
    the silent logon task - reuses the stored credential.

    Run with no arguments to map the drives now (prompts for the password the first time).
    Use -Install to store the credential, map now, AND register a Scheduled Task that maps
    at every logon. -Uninstall removes the task; -Unmap disconnects the drives and clears
    the stored credential; -SetCredential just (re)stores the password.

.EXAMPLE
    .\Connect-KukaDrives.ps1 -Install
        Store the password, map the drives, and auto-map at every logon.

.EXAMPLE
    .\Connect-KukaDrives.ps1
        Map the drives now (prompts for the password if it isn't stored yet).
#>
[CmdletBinding(DefaultParameterSetName = 'Map')]
param(
    [Parameter(ParameterSetName = 'Install')]       [switch]$Install,
    [Parameter(ParameterSetName = 'Uninstall')]     [switch]$Uninstall,
    [Parameter(ParameterSetName = 'Unmap')]         [switch]$Unmap,
    [Parameter(ParameterSetName = 'SetCredential')] [switch]$SetCredential,

    # Seconds to wait for each controller to answer a ping before giving up on its shares.
    [int]$WaitSeconds = 90
)

# ----------------------------------------------------------------------------
# Account (username only - the password is never stored in this file).
# ----------------------------------------------------------------------------
$User = 'KukaUser'

# ----------------------------------------------------------------------------
# Drive map - edit the letters here if any collide with existing drives.
# ----------------------------------------------------------------------------
$Mappings = @(
    [pscustomobject]@{ Cell = 'LFAM 1'; Ip = '192.168.0.151'; Drive = 'K:'; Share = 'krc' }
    [pscustomobject]@{ Cell = 'LFAM 1'; Ip = '192.168.0.151'; Drive = 'L:'; Share = 'd'   }
    [pscustomobject]@{ Cell = 'LFAM 2'; Ip = '192.168.0.152'; Drive = 'M:'; Share = 'krc' }
    [pscustomobject]@{ Cell = 'LFAM 2'; Ip = '192.168.0.152'; Drive = 'N:'; Share = 'd'   }
    [pscustomobject]@{ Cell = 'LFAM 3'; Ip = '192.168.0.153'; Drive = 'O:'; Share = 'krc' }
    [pscustomobject]@{ Cell = 'LFAM 3'; Ip = '192.168.0.153'; Drive = 'P:'; Share = 'd'   }
)

$Hosts    = $Mappings.Ip | Select-Object -Unique
$TaskName = 'Connect KUKA Drives'

# ----------------------------------------------------------------------------
# Helpers
# ----------------------------------------------------------------------------
function Wait-Online {
    param([string]$Ip, [int]$TimeoutSec)
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (Test-Connection -ComputerName $Ip -Count 1 -Quiet -ErrorAction SilentlyContinue) { return $true }
        Start-Sleep -Seconds 3
    }
    return $false
}

# True if a Credential Manager entry already exists for the host.
function Test-StoredCredential {
    param([string]$Ip)
    $list = cmd.exe /c "cmdkey /list:$Ip" 2>$null
    return ($list -match [regex]::Escape($Ip))
}

# Prompt once for the password and store it for all controllers in Credential Manager.
function Set-KukaCredential {
    $secure = Read-Host "Enter the password for $User" -AsSecureString
    if (-not $secure -or $secure.Length -eq 0) {
        Write-Warning "No password entered - credential not stored."
        return $false
    }

    # SecureString -> plaintext only long enough to hand it to cmdkey; never written to disk.
    $bstr  = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try   { $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }

    foreach ($ip in $Hosts) {
        cmd.exe /c "cmdkey /add:$ip /user:$User /pass:$plain" | Out-Null
    }
    $plain = $null
    Write-Host "Stored the $User credential for: $($Hosts -join ', ')" -ForegroundColor Green
    return $true
}

function Connect-Drives {
    # If nothing is stored yet, prompt now (interactive run). The silent logon task relies on
    # the credential already being present, so it never blocks waiting for input.
    if (-not ($Hosts | Where-Object { Test-StoredCredential $_ })) {
        if ([Environment]::UserInteractive) {
            if (-not (Set-KukaCredential)) { return }
        }
        else {
            Write-Warning "No stored credential for KukaUser. Run '.\Connect-KukaDrives.ps1 -SetCredential' once."
            return
        }
    }

    Write-Host "Mapping KUKA controller shares..." -ForegroundColor Cyan
    foreach ($group in $Mappings | Group-Object Ip) {
        $ip   = $group.Name
        $cell = $group.Group[0].Cell

        Write-Host "`n$cell ($ip):"
        if (-not (Wait-Online -Ip $ip -TimeoutSec $WaitSeconds)) {
            Write-Warning "  unreachable after ${WaitSeconds}s - skipping its shares."
            continue
        }

        foreach ($m in $group.Group) {
            $unc = "\\$ip\$($m.Share)"

            # Drop any stale mapping on this letter (ignore "not connected" errors), then map
            # using the credential stored in Credential Manager (no /user - net use looks it up).
            cmd.exe /c "net use $($m.Drive) /delete /y" 2>$null | Out-Null
            $output = cmd.exe /c "net use $($m.Drive) `"$unc`" /persistent:yes 2>&1"
            if ($LASTEXITCODE -eq 0) {
                Write-Host ("  [ok]   {0,-3} -> {1}" -f $m.Drive, $unc) -ForegroundColor Green
            }
            else {
                Write-Warning ("  [fail] {0,-3} -> {1}  ({2})" -f $m.Drive, $unc, ($output | Select-Object -Last 1))
            }
        }
    }
    Write-Host "`nDone." -ForegroundColor Cyan
}

function Disconnect-Drives {
    Write-Host "Disconnecting KUKA controller shares..." -ForegroundColor Cyan
    foreach ($m in $Mappings) {
        cmd.exe /c "net use $($m.Drive) /delete /y" 2>$null | Out-Null
        Write-Host "  removed $($m.Drive)"
    }
    foreach ($ip in $Hosts) {
        cmd.exe /c "cmdkey /delete:$ip" 2>$null | Out-Null
    }
    Write-Host "Cleared stored credentials. Done." -ForegroundColor Cyan
}

function Install-Task {
    if (-not (Set-KukaCredential)) {
        Write-Warning "Aborting install - no credential stored."
        return
    }

    # Network drives are per-user and only visible when mapped in the interactive session,
    # so the task runs as the current user at logon (NOT as SYSTEM).
    $action  = New-ScheduledTaskAction -Execute 'powershell.exe' `
                   -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    $trigger = New-ScheduledTaskTrigger -AtLogOn
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                    -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 10)
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" `
                    -LogonType Interactive -RunLevel Limited

    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
        -Settings $settings -Principal $principal -Force | Out-Null
    Write-Host "Registered scheduled task '$TaskName' - runs at logon for $env:USERNAME." -ForegroundColor Green

    Connect-Drives   # map immediately too, so they're available without a re-logon
}

function Uninstall-Task {
    $existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($existing) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Host "Removed scheduled task '$TaskName'." -ForegroundColor Green
    }
    else {
        Write-Host "No scheduled task '$TaskName' found."
    }
}

# ----------------------------------------------------------------------------
# Dispatch
# ----------------------------------------------------------------------------
switch ($PSCmdlet.ParameterSetName) {
    'Install'       { Install-Task }
    'Uninstall'     { Uninstall-Task }
    'Unmap'         { Disconnect-Drives }
    'SetCredential' { Set-KukaCredential | Out-Null }
    default         { Connect-Drives }
}
