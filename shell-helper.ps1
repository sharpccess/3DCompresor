# Shell Extension Helper - Accumulates selected files and compresses them together
# This script is called once per selected file, but waits briefly to collect all selections

param(
    [string]$FilePath
)

$exePath = "c:\Users\emoti\source\repos\compresor\Compresor3D\bin\Debug\net8.0\Compresor3D.exe"
$lockFile = Join-Path $env:TEMP "compressor3d_batch.lock"
$listFile = Join-Path $env:TEMP "compressor3d_batch.txt"

# Try to acquire lock (wait up to 2 seconds for other instances)
$lockAcquired = $false
$startTime = Get-Date
while (-not $lockAcquired -and ((Get-Date) - $startTime).TotalSeconds -lt 2) {
    try {
        $lockStream = [System.IO.File]::Open($lockFile, 'OpenOrCreate', 'ReadWrite', 'None')
        $lockAcquired = $true
        $lockStream.Close()
    } catch {
        Start-Sleep -Milliseconds 100
    }
}

# Add file to list
if (-not (Test-Path $listFile)) {
    # First file - create new list
    $FilePath | Out-File -FilePath $listFile -Encoding UTF8
} else {
    # Append to existing list
    Add-Content -Path $listFile -Value $FilePath
}

# Wait a bit to see if more files are coming (other instances)
Start-Sleep -Milliseconds 500

# Check if we're the last instance (no lock contention)
try {
    $lockStream = [System.IO.File]::Open($lockFile, 'OpenOrCreate', 'ReadWrite', 'None')
    $lockStream.Close()
    
    # We're the last one - read all files and compress
    if (Test-Path $listFile) {
        $files = Get-Content -Path $listFile | Where-Object { Test-Path $_ }
        
        if ($files.Count -gt 0) {
            # Build argument list
            $args = @("--batch")
            $args += $files | ForEach-Object { "`"$_`"" }
            
            # Execute compressor
            Start-Process -FilePath $exePath -ArgumentList $args -Wait -NoNewWindow
            
            # Clean up
            Remove-Item -Path $listFile -Force -ErrorAction SilentlyContinue
        }
    }
    
    # Release lock
    Remove-Item -Path $lockFile -Force -ErrorAction SilentlyContinue
} catch {
    # Another instance is handling it
    exit 0
}
