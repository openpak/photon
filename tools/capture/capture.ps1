# Captures UDP traffic for the Black Ice process to a timestamped pcapng.
# Requires tshark (Wireshark) on PATH or at the default install location.
# Usage: pwsh tools/capture/capture.ps1 -Seconds 120
param([int]$Seconds = 120, [string]$OutDir = "$PSScriptRoot\..\..\captures")

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$out = Join-Path $OutDir "blackice-$stamp.pcapng"

$tshark = (Get-Command tshark -ErrorAction SilentlyContinue).Source
if (-not $tshark) {
    $default = "C:\Program Files\Wireshark\tshark.exe"
    if (Test-Path $default) { $tshark = $default }
    else { Write-Error "tshark not found. Install Wireshark (winget install WiresharkFoundation.Wireshark)."; exit 1 }
}

# Photon endpoints observed in recon: Name/Master on :5055, Game on :5056 (UDP).
# Capture all UDP and filter by host/port during analysis.
Write-Host "Capturing UDP for $Seconds s -> $out"
& $tshark -a "duration:$Seconds" -f "udp" -w $out
Write-Host "Saved $out"
