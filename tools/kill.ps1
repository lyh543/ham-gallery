param(
    [string]$PidFile = ".run.pid"
)

if (-not (Test-Path $PidFile)) { exit 0 }

$id = [int](Get-Content $PidFile | Select-Object -First 1)
$proc = Get-Process -Id $id -ErrorAction SilentlyContinue

if ($proc -and $proc.ProcessName -eq "FluentGallery") {
    taskkill /PID $id /T /F 2>$null
    Write-Host "Killed FluentGallery (PID $id)"
}

Remove-Item $PidFile -Force -ErrorAction SilentlyContinue
