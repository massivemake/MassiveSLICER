# Build MassiveSlicer from the current repo state (no git pull) and launch it.
# This is the canonical build+launch location — always build here, not in a
# worktree/temp folder, so this shortcut stays valid.
$repoRoot = $PSScriptRoot
Set-Location $repoRoot

# Git for Windows isn't on the system PATH here (only Git Bash sees it), but the
# csproj's build-number step shells out to git — without this, that step fails
# silently (exit 9009) and corrupts the generated BuildInfo.g.cs, breaking the build.
$gitCmdDir = 'C:\Program Files\Git\cmd'
if ((Test-Path "$gitCmdDir\git.exe") -and ($env:PATH -notlike "*$gitCmdDir*")) {
    $env:PATH = "$gitCmdDir;$env:PATH"
}

try {
    Write-Host "== Stopping any running MassiveSlicer instance ==" -ForegroundColor Cyan
    Get-Process -Name 'MassiveSlicer.App' -ErrorAction SilentlyContinue | Stop-Process -Force

    Write-Host "== Building (Release) ==" -ForegroundColor Cyan
    dotnet build "$repoRoot\src\MassiveSlicer.App\MassiveSlicer.App.csproj" -c Release
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit code $LASTEXITCODE)" }

    $exe = Join-Path $repoRoot 'src\MassiveSlicer.App\bin\Release\net8.0-windows\MassiveSlicer.App.exe'
    if (-not (Test-Path $exe)) { throw "Build did not produce an exe at expected path: $exe" }

    Write-Host "== Launching MassiveSlicer ==" -ForegroundColor Cyan
    Start-Process $exe
    Start-Sleep -Seconds 2
}
catch {
    Write-Host ""
    Write-Host "ERROR: $_" -ForegroundColor Red
    Read-Host "Press Enter to close"
    exit 1
}
