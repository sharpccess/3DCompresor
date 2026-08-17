# Compressor3D - Shell Extension Installer
# Adds compress/decompress options to the right-click context menu
# Must be run as Administrator

$ErrorActionPreference = "Stop"

# Find the executable
$exePath = Join-Path $PSScriptRoot "Compresor3D\bin\Debug\net8.0\Compresor3D.exe"
if (-not (Test-Path $exePath)) {
    $exePath = Join-Path $PSScriptRoot "Compresor3D\bin\Release\net8.0\Compresor3D.exe"
}
if (-not (Test-Path $exePath)) {
    Write-Host "ERROR: Compresor3D.exe not found. Build the project first." -ForegroundColor Red
    Write-Host "Run: dotnet build Compresor3D\Compresor3D.csproj" -ForegroundColor Yellow
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
    Write-Host "Right-click PowerShell -> 'Run as Administrator'" -ForegroundColor Yellow
    exit 1
}

# ---- COMPRESS entries ----
Write-Host "Creating COMPRESS context menu entries..." -ForegroundColor Green

# Compress selected files
$fileCompPath = "Registry::HKEY_CLASSES_ROOT\*\shell\Compressor3D.Compress"
New-Item -Path $fileCompPath -Force | Out-Null
Set-ItemProperty -Path $fileCompPath -Name "(Default)" -Value "Compress with Compressor3D"
Set-ItemProperty -Path $fileCompPath -Name "Icon" -Value $exePath
New-Item -Path "$fileCompPath\command" -Force | Out-Null
Set-ItemProperty -Path "$fileCompPath\command" -Name "(Default)" -Value "`"$exePath`" --batch `"%1`""

# Compress folder
$dirCompPath = "Registry::HKEY_CLASSES_ROOT\Directory\shell\Compressor3D.Compress"
New-Item -Path $dirCompPath -Force | Out-Null
Set-ItemProperty -Path $dirCompPath -Name "(Default)" -Value "Compress folder with Compressor3D"
Set-ItemProperty -Path $dirCompPath -Name "Icon" -Value $exePath
New-Item -Path "$dirCompPath\command" -Force | Out-Null
Set-ItemProperty -Path "$dirCompPath\command" -Name "(Default)" -Value "`"$exePath`" --batch `"%1`""

# Compress all in folder background
$bgCompPath = "Registry::HKEY_CLASSES_ROOT\Directory\Background\shell\Compressor3D.Compress"
New-Item -Path $bgCompPath -Force | Out-Null
Set-ItemProperty -Path $bgCompPath -Name "(Default)" -Value "Compress all with Compressor3D"
Set-ItemProperty -Path $bgCompPath -Name "Icon" -Value $exePath
New-Item -Path "$bgCompPath\command" -Force | Out-Null
Set-ItemProperty -Path "$bgCompPath\command" -Name "(Default)" -Value "`"$exePath`" --batch `"%V`""

# ---- DECOMPRESS entries ----
Write-Host "Creating DECOMPRESS context menu entries..." -ForegroundColor Green

# Decompress .cubo file
$fileDecompPath = "Registry::HKEY_CLASSES_ROOT\*\shell\Compressor3D.Decompress"
New-Item -Path $fileDecompPath -Force | Out-Null
Set-ItemProperty -Path $fileDecompPath -Name "(Default)" -Value "Extract here with Compressor3D"
Set-ItemProperty -Path $fileDecompPath -Name "Icon" -Value $exePath
# Only show for .cubo files
Set-ItemProperty -Path $fileDecompPath -Name "AppliesTo" -Value "System.FileExtension:.cubo"
New-Item -Path "$fileDecompPath\command" -Force | Out-Null
Set-ItemProperty -Path "$fileDecompPath\command" -Name "(Default)" -Value "`"$exePath`" --descomprimir `"%1`""

# ---- SEPARATOR ----
# Add separator between compress and decompress
Set-ItemProperty -Path $fileDecompPath -Name "SeparatorBefore" -Value ""

Write-Host ""
Write-Host "Shell extension installed successfully!" -ForegroundColor Green
Write-Host ""
Write-Host "Context menu options:" -ForegroundColor White
Write-Host "  Right-click FILE      -> 'Compress with Compressor3D'" -ForegroundColor White
Write-Host "  Right-click FOLDER    -> 'Compress folder with Compressor3D'" -ForegroundColor White
Write-Host "  Right-click BACKGROUND -> 'Compress all with Compressor3D'" -ForegroundColor White
Write-Host "  Right-click .CUBO     -> 'Extract here with Compressor3D'" -ForegroundColor White
Write-Host ""
Write-Host "To uninstall: .\uninstall-shell.ps1" -ForegroundColor Gray
