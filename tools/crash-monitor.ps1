<#
.SYNOPSIS
    Launches FluentGallery under monitoring and captures diagnostics on crash.

.DESCRIPTION
    1. Enables WER LocalDumps for FluentGallery.exe (full dump).
    2. Launches the app.
    3. Waits for it to exit.
    4. On non-zero exit or unexpected termination:
       - Copies the latest app log.
       - Copies any WER crash dump.
       - Prints a summary with exit code, timestamps, and file paths.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/crash-monitor.ps1
#>

param(
    [string]$DumpDir = "$env:LOCALAPPDATA\FluentGallery-Dev\crash-dumps"
)

$ErrorActionPreference = 'Stop'

# ── Paths ──────────────────────────────────────────────────────────────────────
$exePath   = "FluentGallery\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\FluentGallery.exe"
$logDir    = "$env:LOCALAPPDATA\FluentGallery-Dev\logs"
$werDumps  = "$env:LOCALAPPDATA\CrashDumps"

if (-not (Test-Path $exePath)) {
    Write-Host "ERROR: $exePath not found. Run 'make build' first." -ForegroundColor Red
    exit 1
}

# ── Ensure dump output directory ───────────────────────────────────────────────
New-Item -ItemType Directory -Path $DumpDir -Force | Out-Null

# ── Configure WER LocalDumps (per-app, no admin required for HKCU) ─────────────
$werKey = "HKCU:\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps\FluentGallery.exe"
if (-not (Test-Path $werKey)) {
    New-Item -Path $werKey -Force | Out-Null
}
Set-ItemProperty -Path $werKey -Name "DumpFolder" -Value $DumpDir -Type ExpandString
Set-ItemProperty -Path $werKey -Name "DumpType"   -Value 2         -Type DWord  # 2 = Full dump
Set-ItemProperty -Path $werKey -Name "DumpCount"  -Value 5         -Type DWord
Write-Host "[*] WER LocalDumps configured -> $DumpDir" -ForegroundColor Cyan

# ── Snapshot: existing dumps and logs ──────────────────────────────────────────
$existingDumps = @(Get-ChildItem -Path $DumpDir -Filter "*.dmp" -ErrorAction SilentlyContinue)
$existingWer   = @(Get-ChildItem -Path $werDumps -Filter "FluentGallery*.dmp" -ErrorAction SilentlyContinue)

# ── Launch ─────────────────────────────────────────────────────────────────────
$startTime = Get-Date
Write-Host "[*] Launching $exePath at $($startTime.ToString('HH:mm:ss'))" -ForegroundColor Green
$proc = Start-Process -FilePath $exePath -PassThru

Write-Host "[*] PID = $($proc.Id). Waiting for exit... (Ctrl+C to stop monitoring)" -ForegroundColor Yellow

try {
    $proc.WaitForExit()
} catch {
    # User pressed Ctrl+C
    Write-Host "`n[!] Monitoring interrupted." -ForegroundColor Yellow
    exit 0
}

$exitTime = Get-Date
$exitCode = $proc.ExitCode

# ── Analyse ────────────────────────────────────────────────────────────────────
$crashed = $exitCode -ne 0
$color   = if ($crashed) { "Red" } else { "Green" }
$status  = if ($crashed) { "CRASHED" } else { "Normal exit" }

Write-Host ""
Write-Host "════════════════════════════════════════════════════════" -ForegroundColor $color
Write-Host "  $status" -ForegroundColor $color
Write-Host "  Exit code : $exitCode (0x$($exitCode.ToString('X8')))" -ForegroundColor $color
Write-Host "  Started   : $($startTime.ToString('yyyy-MM-dd HH:mm:ss.fff'))" -ForegroundColor $color
Write-Host "  Exited    : $($exitTime.ToString('yyyy-MM-dd HH:mm:ss.fff'))" -ForegroundColor $color
Write-Host "  Duration  : $( ($exitTime - $startTime).ToString('hh\:mm\:ss\.fff') )" -ForegroundColor $color
Write-Host "════════════════════════════════════════════════════════" -ForegroundColor $color

if (-not $crashed) {
    Write-Host "[*] Clean exit, no diagnostics needed." -ForegroundColor Green
    exit 0
}

# ── Collect crash dump ─────────────────────────────────────────────────────────
$ts = $exitTime.ToString("yyyyMMdd-HHmmss")

# Check for new WER dumps in both locations
$newDumps = @()
foreach ($dir in @($DumpDir, $werDumps)) {
    $newDumps += @(Get-ChildItem -Path $dir -Filter "*.dmp" -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -gt $startTime -and $_.Name -like "FluentGallery*" })
}

if ($newDumps.Count -gt 0) {
    foreach ($d in $newDumps) {
        $dest = Join-Path $DumpDir "crash-$ts.dmp"
        if ($d.FullName -ne $dest) {
            Copy-Item $d.FullName $dest -Force -ErrorAction SilentlyContinue
        }
        Write-Host "[+] Crash dump: $($d.FullName) ($([math]::Round($d.Length/1MB, 1)) MB)" -ForegroundColor Cyan
    }
} else {
    Write-Host "[!] No WER crash dump found (TerminateProcess bypasses WER)." -ForegroundColor Yellow
}

# ── Collect app log ────────────────────────────────────────────────────────────
$logDate = $exitTime.ToString("yyyyMMdd")
$logFile = Join-Path $logDir "app-$logDate.log"
if (Test-Path $logFile) {
    $destLog = Join-Path $DumpDir "crash-$ts.log"
    Copy-Item $logFile $destLog -Force

    # Extract last N lines around crash time
    $lines = Get-Content $logFile -Tail 60
    Write-Host ""
    Write-Host "── Last 60 lines of app log ──────────────────────────────" -ForegroundColor Cyan
    $lines | ForEach-Object { Write-Host $_ }
    Write-Host "──────────────────────────────────────────────────────────" -ForegroundColor Cyan
    Write-Host "[+] Full log saved: $destLog" -ForegroundColor Cyan
} else {
    Write-Host "[!] App log not found: $logFile" -ForegroundColor Yellow
}

# ── Windows Event Log ──────────────────────────────────────────────────────────
Write-Host ""
Write-Host "── Recent Application Error events ───────────────────────" -ForegroundColor Cyan
try {
    $events = Get-WinEvent -FilterHashtable @{
        LogName   = 'Application'
        Level     = 1,2  # Critical, Error
        StartTime = $startTime
    } -MaxEvents 10 -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match "FluentGallery|$($proc.Id)" }

    if ($events) {
        $events | Format-List TimeCreated, Id, Message
        # Save to file
        $evtFile = Join-Path $DumpDir "crash-$ts-events.txt"
        $events | Format-List TimeCreated, Id, Message | Out-File $evtFile
        Write-Host "[+] Events saved: $evtFile" -ForegroundColor Cyan
    } else {
        Write-Host "(none found)" -ForegroundColor Yellow
    }
} catch {
    Write-Host "(could not query event log)" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "[*] All crash artifacts in: $DumpDir" -ForegroundColor Green
