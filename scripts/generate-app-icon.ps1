# Regenerates assets/Icons/icon.ico from Square44x44Logo PNGs (Windows exe + Avalonia window icon).
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$iconDir  = Join-Path $repoRoot 'assets\Icons'
$icoPath  = Join-Path $iconDir 'icon.ico'
$source   = Join-Path $iconDir 'Square44x44Logo.targetsize-256.png'

if (-not (Test-Path $source)) {
    throw "Missing source PNG: $source"
}

Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap $source
try {
    $icon = [System.Drawing.Icon]::FromHandle($bitmap.GetHicon())
    try {
        $fs = [System.IO.File]::Create($icoPath)
        try { $icon.Save($fs) } finally { $fs.Close() }
    } finally { $icon.Dispose() }
} finally { $bitmap.Dispose() }

Write-Host "Wrote $icoPath"