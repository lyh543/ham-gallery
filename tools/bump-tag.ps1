<#
.SYNOPSIS
    读取最新 git tag，计算下一个版本号，打 tag 并推送。

.PARAMETER Part
    要递增的版本段：major | minor | patch | build（默认 build）

.EXAMPLE
    .\tools\bump-tag.ps1 -Part build    # 递增 build：v0.1.2.0 → v0.1.2.1
    .\tools\bump-tag.ps1 [-Part patch]  # 递增 patch：v0.1.2.0 → v0.1.3.0
    .\tools\bump-tag.ps1 -Part minor    # 递增 minor：v0.1.2.0 → v0.2.0.0
    .\tools\bump-tag.ps1 -Part major    # 递增 major：v0.1.2.0 → v1.0.0.0
    .\tools\bump-tag.ps1 [-Part patch] -y # 递增 patch 并自动同意
#>
param(
    [ValidateSet("major", "minor", "patch", "build")]
    [string] $Part = "patch",

    [switch] $y
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── 1. 找最新 tag ────────────────────────────────────────────────────────────
$latest = git tag --sort=-version:refname 2>$null |
    Where-Object { $_ -match '^v\d+\.\d+\.\d+(\.\d+)?$' } |
    Select-Object -First 1

if (-not $latest) {
    Write-Host "No existing version tag found. Starting from v0.0.0.0"
    $latest = "v0.0.0.0"
}

Write-Host "Latest tag : $latest"

# ── 2. 解析版本号 ────────────────────────────────────────────────────────────
$raw = $latest.TrimStart('v')
$parts = $raw.Split('.')

$major = [int]$parts[0]
$minor = [int]$parts[1]
$patch = [int]$parts[2]
$build = if ($parts.Count -ge 4) { [int]$parts[3] } else { 0 }

# ── 3. 递增 ──────────────────────────────────────────────────────────────────
switch ($Part) {
    "major" { $major++; $minor = 0; $patch = 0; $build = 0 }
    "minor" { $minor++; $patch = 0; $build = 0 }
    "patch" { $patch++; $build = 0 }
    "build" { $build++ }
}

$newTag = "v$major.$minor.$patch.$build"
Write-Host "New tag    : $newTag"

# ── 4. 确认 ──────────────────────────────────────────────────────────────────
if (-not $y) {
    $confirm = Read-Host "Create and push '$newTag'? [y/N]"
    if ($confirm -notmatch '^[Yy]$') {
        Write-Host "Aborted."
        exit 0
    }
} else {
    Write-Host "Auto-confirming (use -y flag)..."
}

# ── 5. 打 tag 并推送 ─────────────────────────────────────────────────────────
git tag $newTag
if ($LASTEXITCODE -ne 0) { throw "git tag failed" }

git push origin $newTag
if ($LASTEXITCODE -ne 0) { throw "git push failed" }

Write-Host "Done: $newTag pushed."
