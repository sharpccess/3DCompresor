# Compressor3D - Shell Extension Uninstaller
# Removes "Compress with Compressor3D" from the right-click context menu
# Must be run as Administrator

$ErrorActionPreference = "Stop"

Write-Host "Compressor3D Shell Extension Uninstaller" -ForegroundColor Cyan
Write-Host "=========================================" -ForegroundColor Cyan
Write-Host ""

# Check admin privileges
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "ERROR: This script must be run as Administrator" -ForegroundColor Red
    Write-Host "Right-click PowerShell and select 'Run as Administrator'" -ForegroundColor Yellow
    exit 1
}

# Registry paths
$fileShellPath = "Registry::HKEY_CLASSES_ROOT\*\shell\Compressor3D"
$dirShellPath = "Registry::HKEY_CLASSES_ROOT\Directory\shell\Compressor3D"
$dirBgShellPath = "Registry::HKEY_CLASSES_ROOT\Directory\Background\shell\Compressor3D"

Write-Host "Removing context menu entries..." -ForegroundColor Green

# Remove file entry
if (Test-Path $fileShellPath) {
    Remove-Item -Path $fileShellPath -Recurse -Force
    Write-Host "  Removed file context menu" -ForegroundColor Gray
}

# Remove directory entry
if (Test-Path $dirShellPath) {
    Remove-Item -Path $dirShellPath -Recurse -Force
    Write-Host "  Removed directory context menu" -ForegroundColor Gray
}

# Remove directory background entry
if (Test-Path $dirBgShellPath) {
    Remove-Item -Path $dirBgShellPath -Recurse -Force
    Write-Host "  Removed directory background context menu" -ForegroundColor Gray
}

Write-Host ""
Write-Host "Shell extension uninstalled successfully!" -ForegroundColor Green
