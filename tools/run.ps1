param(
    [Parameter(Mandatory)][string]$ExePath,
    [string]$PidFile = ".run.pid"
)

$p = Start-Process -PassThru $ExePath
Set-Content $PidFile $p.Id

try {
    $p | Wait-Process
} finally {
    if (-not $p.HasExited) {
        taskkill /PID $p.Id /T /F 2>$null | Out-Null
    }
    Remove-Item $PidFile -Force -ErrorAction SilentlyContinue
}
