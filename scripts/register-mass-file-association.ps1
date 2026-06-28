# Registers the current-user Windows file association for MassiveSlicer .mass workspaces.
# Usage:
#   .\register-mass-file-association.ps1
#   .\register-mass-file-association.ps1 -InstallDir "C:\path\to\build"
#   .\register-mass-file-association.ps1 -Unregister
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'MassiveSlicer\build'),
    [switch]$Unregister
)

$ErrorActionPreference = 'Stop'

$progId    = 'MassiveSlicer.Mass'
$extension = '.mass'
$appExe    = Join-Path $InstallDir 'MassiveSlicer.App.exe'
$iconIco   = Join-Path $InstallDir 'icon.ico'
$classes   = 'HKCU:\Software\Classes'

function Notify-AssociationChanged {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ShellNotify {
    [DllImport("shell32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
}
"@ -ErrorAction SilentlyContinue | Out-Null
    [ShellNotify]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
}

if ($Unregister) {
    Remove-Item -LiteralPath "$classes\$extension" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath "$classes\$progId" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath "$classes\Applications\MassiveSlicer.App.exe" -Recurse -Force -ErrorAction SilentlyContinue
    Notify-AssociationChanged
    Write-Host "Unregistered $extension file association."
    exit 0
}

if (-not (Test-Path -LiteralPath $appExe)) {
    throw "MassiveSlicer.App.exe not found at $appExe. Publish the app first."
}

$iconRef = if (Test-Path -LiteralPath $iconIco) { $iconIco } else { "$appExe,0" }
$openCmd = "`"$appExe`" `"%1`""

New-Item -Path "$classes\$extension" -Force | Out-Null
Set-ItemProperty -LiteralPath "$classes\$extension" -Name '(default)' -Value $progId

New-Item -Path "$classes\$progId" -Force | Out-Null
Set-ItemProperty -LiteralPath "$classes\$progId" -Name '(default)' -Value 'MassiveSlicer Workspace'
New-ItemProperty -LiteralPath "$classes\$progId" -Name 'FriendlyTypeName' -Value 'MassiveSlicer Workspace' -Force | Out-Null

New-Item -Path "$classes\$progId\DefaultIcon" -Force | Out-Null
Set-ItemProperty -LiteralPath "$classes\$progId\DefaultIcon" -Name '(default)' -Value $iconRef

New-Item -Path "$classes\$progId\shell\open\command" -Force | Out-Null
Set-ItemProperty -LiteralPath "$classes\$progId\shell\open\command" -Name '(default)' -Value $openCmd

New-Item -Path "$classes\$progId\shell\edit\command" -Force | Out-Null
Set-ItemProperty -LiteralPath "$classes\$progId\shell\edit\command" -Name '(default)' -Value $openCmd

New-Item -Path "$classes\Applications\MassiveSlicer.App.exe\shell\open\command" -Force | Out-Null
Set-ItemProperty -LiteralPath "$classes\Applications\MassiveSlicer.App.exe\shell\open\command" -Name '(default)' -Value $openCmd

New-Item -Path "$classes\Applications\MassiveSlicer.App.exe\DefaultIcon" -Force | Out-Null
Set-ItemProperty -LiteralPath "$classes\Applications\MassiveSlicer.App.exe\DefaultIcon" -Name '(default)' -Value $iconRef

Notify-AssociationChanged
Write-Host "Registered $extension -> $appExe"
Write-Host "Icon: $iconRef"
Write-Host "Double-click a .mass file to open it in Massive Slicer."