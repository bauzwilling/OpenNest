# Publish the built .yak package as a GitHub Release asset.
#
# Reads the version from manifest.yml, finds the matching .yak in grasshopper_plugin/datab_opennest_win/,
# creates (or reuses) a release tagged v<version>, and uploads the .yak as an asset.
#
# Prerequisites:
#   - gh CLI installed and authenticated:  gh auth login
#   - the package already built:           pwsh scripts/build_yak.ps1
#
#   pwsh scripts/release_github.ps1                 # release from current branch's HEAD
#   pwsh scripts/release_github.ps1 -Draft          # create as a draft (review before publishing)
#   pwsh scripts/release_github.ps1 -Notes "..."    # custom release notes

param(
    [switch]$Draft,
    [string]$Notes
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot   # repo root (scripts/ is one level down)
Set-Location $root

$PKG = Join-Path $root "grasshopper_plugin\datab_opennest_win"

# --- version from the manifest (single source of truth) ---
$manifest = Get-Content "$PKG\manifest.yml" -Raw
if ($manifest -notmatch "(?m)^version:\s*(\d+)\.(\d+)\.(\d+)\.(\d+)\s*$") {
    throw "Could not parse version from $PKG\manifest.yml"
}
$verFull  = "$($Matches[1]).$($Matches[2]).$($Matches[3]).$($Matches[4])"
$verShort = "$($Matches[1]).$($Matches[2]).$($Matches[3])"
$tag      = "v$verFull"

# --- locate the built .yak ---
$yakFile = Join-Path $PKG "datab_opennest-$verShort-rh8_0-win.yak"
if (-not (Test-Path $yakFile)) {
    throw "Package not found: $yakFile`nBuild it first:  pwsh scripts/build_yak.ps1"
}
Write-Host "==> Release $tag" -ForegroundColor Cyan
Write-Host "    asset: $yakFile" -ForegroundColor Cyan

# --- gh must be authenticated ---
gh auth status 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { throw "gh is not authenticated. Run: gh auth login" }

if (-not $Notes) { $Notes = "DataB_OpenNest $verFull (Windows, Rhino 8)." }

# --- create the release if the tag doesn't already exist, else just upload the asset ---
gh release view $tag 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "==> Creating release $tag" -ForegroundColor Cyan
    $args = @("release","create",$tag,$yakFile,"--title",$tag,"--notes",$Notes)
    if ($Draft) { $args += "--draft" }
    gh @args
} else {
    Write-Host "==> Release $tag exists; uploading asset (--clobber)" -ForegroundColor Yellow
    gh release upload $tag $yakFile --clobber
}
if ($LASTEXITCODE -ne 0) { throw "gh release failed" }

Write-Host "Done. https://github.com/bauzwilling/OpenNest/releases/tag/$tag" -ForegroundColor Green
