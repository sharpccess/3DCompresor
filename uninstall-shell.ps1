# Compressor3D - Shell Extension Uninstaller
# Must be run as Administrator

$ErrorActionPreference = "Stop"

Write-Host "Compressor3D Shell Extension Uninstaller" -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host ""

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "ERROR: Run as Administrator" -ForegroundColor Red
    exit 1
}

$paths = @(
    "Registry::HKEY_CLASSES_ROOT\*\shell\Compressor3D.Compress",
    "Registry::HKEY_CLASSES_ROOT\*\shell\Compressor3D.Decompress",
    "Registry::HKEY_CLASSES_ROOT\Directory\shell\Compressor3D.Compress",
    "Registry::HKEY_CLASSES_ROOT\Directory\Background\shell\Compressor3D.Compress",
    "Registry::HKEY_CLASSES_ROOT\*\shell\Compressor3D",
    "Registry::HKEY_CLASSES_ROOT\Directory\shell\Compressor3D",
    "Registry::HKEY_CLASSES_ROOT\Directory\Background\shell\Compressor3D"
)

foreach ($p in $paths) {
    if (Test-Path $p) {
        Remove-Item -Path $p -Recurse -Force
        Write-Host "  Removed: $p" -ForegroundColor Gray
    }
}

Write-Host ""
Write-Host "Shell extension uninstalled!" -ForegroundColor Green
