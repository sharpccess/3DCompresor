# Compressor3D - Shell Extension Installer
# Adds "Compress with Compressor3D" to the right-click context menu
# Must be run as Administrator

$ErrorActionPreference = "Stop"

# Find the executable
$exePath = Join-Path $PSScriptRoot "Compresor3D\bin\Debug\net8.0\Compresor3D.exe"
if (-not (Test-Path $exePath)) {
    # Try release path
    $exePath = Join-Path $PSScriptRoot "Compresor3D\bin\Release\net8.0\Compresor3D.exe"
}
if (-not (Test-Path $exePath)) {
    Write-Host "ERROR: Compresor3D.exe not found. Build the project first." -ForegroundColor Red
    Write-Host "Run: dotnet build Compresor3D\Compresor3D.csproj -c Debug" -ForegroundColor Yellow
    exit 1
}

Write-Host "Compressor3D Shell Extension Installer" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Executable: $exePath" -ForegroundColor Gray
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

# Create menu entries
Write-Host "Creating context menu entries..." -ForegroundColor Green

# For files
New-Item -Path $fileShellPath -Force | Out-Null
Set-ItemProperty -Path $fileShellPath -Name "(Default)" -Value "Compress with Compressor3D"
Set-ItemProperty -Path $fileShellPath -Name "Icon" -Value $exePath
New-Item -Path "$fileShellPath\command" -Force | Out-Null
Set-ItemProperty -Path "$fileShellPath\command" -Name "(Default)" -Value "`"$exePath`" --batch `"%1`""

# For directories (compress all files in folder)
New-Item -Path $dirShellPath -Force | Out-Null
Set-ItemProperty -Path $dirShellPath -Name "(Default)" -Value "Compress folder with Compressor3D"
Set-ItemProperty -Path $dirShellPath -Name "Icon" -Value $exePath
New-Item -Path "$dirShellPath\command" -Force | Out-Null
Set-ItemProperty -Path "$dirShellPath\command" -Name "(Default)" -Value "`"$exePath`" --batch `"%1`""

# For directory background (compress all files in current folder)
New-Item -Path $dirBgShellPath -Force | Out-Null
Set-ItemProperty -Path $dirBgShellPath -Name "(Default)" -Value "Compress all with Compressor3D"
Set-ItemProperty -Path $dirBgShellPath -Name "Icon" -Value $exePath
New-Item -Path "$dirBgShellPath\command" -Force | Out-Null
Set-ItemProperty -Path "$dirBgShellPath\command" -Name "(Default)" -Value "`"$exePath`" --batch `"%V`""

Write-Host ""
Write-Host "Shell extension installed successfully!" -ForegroundColor Green
Write-Host ""
Write-Host "You can now:" -ForegroundColor White
Write-Host "  - Right-click any file -> 'Compress with Compressor3D'" -ForegroundColor White
Write-Host "  - Right-click a folder -> 'Compress folder with Compressor3D'" -ForegroundColor White
Write-Host "  - Right-click in folder background -> 'Compress all with Compressor3D'" -ForegroundColor White
Write-Host ""
Write-Host "To uninstall, run: .\uninstall-shell.ps1" -ForegroundColor Gray
