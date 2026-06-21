# Decompresses meshopt-compressed LFAM3 GLBs so SharpGLTF can load them.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$files = Get-ChildItem (Join-Path $root 'assets\cells\LFAM3') -Recurse -Filter '*.glb' |
    Where-Object { $_.Name -notmatch 'LFAM3Robot|LFAM3Bed|booster_frame' } |
    ForEach-Object { $_.FullName.Substring($root.Length + 1) }
Push-Location $root
try {
    foreach ($rel in $files) {
        $src = Join-Path $root $rel
        $tmp = $src + '.tmp'
        Write-Host "Repairing $rel ..."
        npx --yes @gltf-transform/cli copy $src $tmp
        Move-Item -Force $tmp $src
    }
    Write-Host 'Done.'
} finally { Pop-Location }