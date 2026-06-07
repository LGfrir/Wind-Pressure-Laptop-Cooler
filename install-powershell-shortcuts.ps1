$ErrorActionPreference = "Stop"

$ProjectPath = "G:\ESP32\SmartFan"
$ProfilePath = $PROFILE
$ProfileDir = Split-Path -Parent $ProfilePath

if (-not (Test-Path -LiteralPath $ProfileDir)) {
    New-Item -ItemType Directory -Path $ProfileDir -Force | Out-Null
}

$Block = @'

# SmartFan ESP-IDF shortcuts
function idfenv {
    Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force
    . "G:\ESP32\SmartFan\idfenv.ps1"
}

function rmbd {
    Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force
    . "G:\ESP32\SmartFan\rmbd.ps1"
}
# End SmartFan ESP-IDF shortcuts
'@

if ((Test-Path -LiteralPath $ProfilePath) -and
    ((Get-Content -LiteralPath $ProfilePath -Raw) -match "SmartFan ESP-IDF shortcuts")) {
    Write-Host "SmartFan shortcuts already exist in profile: $ProfilePath" -ForegroundColor Yellow
    return
}

Add-Content -LiteralPath $ProfilePath -Value $Block
Write-Host "Installed shortcuts into profile: $ProfilePath" -ForegroundColor Green
Write-Host "Open a new PowerShell window, then run: idfenv"
