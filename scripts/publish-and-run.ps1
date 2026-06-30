# Single canonical publish folder — do not use build2/build3/build4.
$ErrorActionPreference = 'Stop'
$repo   = '\\192.168.0.191\MassiveFILES\Research\LFAM\MassiveSLICER V2'
$outDir = Join-Path $env:LOCALAPPDATA 'MassiveSlicer\build'

Stop-Process -Name 'MassiveSlicer.App' -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

Set-Location $repo
dotnet publish 'src/MassiveSlicer.App/MassiveSlicer.App.csproj' -c Release -o $outDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& (Join-Path $repo 'scripts\register-mass-file-association.ps1') -InstallDir $outDir

Start-Process -FilePath (Join-Path $outDir 'MassiveSlicer.App.exe') -WorkingDirectory $outDir