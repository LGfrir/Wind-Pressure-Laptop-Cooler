$ErrorActionPreference = "Stop"

$ProjectPath = "G:\ESP32\SmartFan"
$BuildPath = Join-Path $ProjectPath "build"

$resolvedProject = (Resolve-Path -LiteralPath $ProjectPath).Path

if (-not (Test-Path -LiteralPath $BuildPath)) {
    Write-Host "No build directory: $BuildPath" -ForegroundColor Yellow
    return
}

$resolvedBuild = (Resolve-Path -LiteralPath $BuildPath).Path

if ($resolvedBuild -ne (Join-Path $resolvedProject "build")) {
    throw "Refusing to remove unexpected path: $resolvedBuild"
}

Remove-Item -LiteralPath $resolvedBuild -Recurse -Force
Write-Host "Removed: $resolvedBuild" -ForegroundColor Green
