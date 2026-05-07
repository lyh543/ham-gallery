param(
    [string]$SourceDir,
    [string]$DestPath
)

Add-Type -Assembly System.IO.Compression.FileSystem

if (Test-Path $DestPath) { Remove-Item $DestPath }

New-Item -ItemType Directory -Path (Split-Path $DestPath) -Force | Out-Null

[System.IO.Compression.ZipFile]::CreateFromDirectory(
    (Resolve-Path $SourceDir),
    [System.IO.Path]::GetFullPath($DestPath),
    [System.IO.Compression.CompressionLevel]::SmallestSize,
    $false
)

Write-Host "Zipped to: $DestPath"
