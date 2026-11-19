$ErrorActionPreference = "Stop"

$IdfPath = "E:\ESP-IDF\esp-idf-v6.0.1"
$ProjectPath = "G:\ESP32\LaptopCooler"
$ExportScript = Join-Path $IdfPath "export.ps1"

if (-not (Test-Path -LiteralPath $ExportScript)) {
    throw "ESP-IDF export script not found: $ExportScript"
}

Set-ExecutionPolicy -ExecutionPolicy Bypass -Scope Process -Force
Set-Location -LiteralPath $IdfPath
. $ExportScript
Set-Location -LiteralPath $ProjectPath

Write-Host "ESP-IDF environment ready." -ForegroundColor Green
Write-Host "Project: $ProjectPath"
Write-Host "Target command examples:"
Write-Host "  idf.py build"
Write-Host "  idf.py -p COMx flash monitor"
