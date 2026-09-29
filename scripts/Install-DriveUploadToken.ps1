# Install the MassiveDRIVE upload token on this PC.
# Run once, as a user who can type the Samba password for 'massive'.
# Every later login on this PC can Send without typing it.
# The token is not stored in GitHub or the cell JSON.
$ErrorActionPreference = 'Stop'

$Cells = @(
    [pscustomobject]@{ Id = 'lfam1'; Unc = '\\192.168.0.189\MassiveDRIVE-LFAM1' }
    [pscustomobject]@{ Id = 'lfam2'; Unc = '\\192.168.0.173\MassiveDRIVE-LFAM2' }
    [pscustomobject]@{ Id = 'lfam3'; Unc = '\\192.168.0.201\MassiveDRIVE' }
)

function New-Token {
    $bytes = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function Connect-Share([string]$Unc) {
    $hostIp = $Unc.TrimStart([char]92).Split([char]92)[0]
    cmd.exe /c "net use `"$Unc`" /delete /y" 2>$null | Out-Null
    $secure = Read-Host "Password for Samba user massive ($Unc)" -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { $plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    cmd.exe /c "cmdkey /add:$hostIp /user:massive /pass:$plain" | Out-Null
    $plain = $null
    $out = cmd.exe /c "net use `"$Unc`" /persistent:no 2>&1"
    if ($LASTEXITCODE -ne 0) {
        throw "Could not connect $Unc. $out"
    }
}

$map = [ordered]@{}
foreach ($cell in $Cells) {
    $tokenFile = Join-Path $cell.Unc 'var\slicer_upload.token'
    if (-not (Test-Path -LiteralPath $tokenFile)) {
        Connect-Share $cell.Unc
    }
    if (Test-Path -LiteralPath $tokenFile) {
        $existing = (Get-Content -LiteralPath $tokenFile -TotalCount 1).Trim()
        if ($existing) {
            $map[$cell.Id] = $existing
            Write-Host "$($cell.Id): using the token already on the Drive host"
            continue
        }
    }
    $fresh = New-Token
    $dir = Split-Path -Parent $tokenFile
    if (-not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir | Out-Null
    }
    Set-Content -LiteralPath $tokenFile -Value $fresh -NoNewline
    Add-Content -LiteralPath $tokenFile -Value ''
    $map[$cell.Id] = $fresh
    Write-Host "$($cell.Id): wrote a new token on the Drive host"
}

$dirOut = Join-Path $env:ProgramData 'MassiveSlicer'
New-Item -ItemType Directory -Force -Path $dirOut | Out-Null
$json = ($map | ConvertTo-Json -Compress)
$bytes = [Text.Encoding]::UTF8.GetBytes($json)
Add-Type -AssemblyName System.Security
$protected = [Security.Cryptography.ProtectedData]::Protect(
    $bytes, $null, [Security.Cryptography.DataProtectionScope]::LocalMachine)
$outFile = Join-Path $dirOut 'drive-upload.bin'
[IO.File]::WriteAllBytes($outFile, $protected)
Write-Host "Installed $outFile for every login on this PC."
Write-Host "Quit MassiveSLICER and open it again, then Send. Do not type a Samba password."
