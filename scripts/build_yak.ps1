# Build the managed plug-ins (DataB.OpenNest.gha + opennest_commands.rhp) and produce the
# Windows .yak package, then install it into grasshopper_plugin/datab_opennest_win/.
#
# Uses whatever nfp_nest.dll / nest_physics.dll are already in place (run scripts/build_native.ps1
# first if the C++ engine changed). To change the package VERSION, edit:
#
#     grasshopper_plugin/datab_opennest_win/manifest.yml   ->   version: X.Y.Z.0
#
#   pwsh scripts/build_yak.ps1
#
# yak.exe is downloaded to the repo root if missing and deleted afterward (never committed).

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot   # repo root (scripts/ is one level down)
Set-Location $root

$PKG = Join-Path $root "grasshopper_plugin\datab_opennest_win"

# --- read version from the manifest (single source of truth) ---
$manifest = Get-Content "$PKG\manifest.yml" -Raw
if ($manifest -notmatch "(?m)^version:\s*(\d+)\.(\d+)\.(\d+)\.(\d+)\s*$") {
    throw "Could not parse version from $PKG\manifest.yml"
}
$verFull  = "$($Matches[1]).$($Matches[2]).$($Matches[3]).$($Matches[4])"
$verShort = "$($Matches[1]).$($Matches[2]).$($Matches[3])"   # yak filename uses 3 parts
Write-Host "==> Package version $verFull" -ForegroundColor Cyan

# --- build the managed plug-ins ---
Write-Host "==> Building DataB.OpenNest.gha" -ForegroundColor Cyan
dotnet build src/opennest_2/opennest_2.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "opennest_2 build failed" }

Write-Host "==> Building opennest_commands.rhp" -ForegroundColor Cyan
dotnet build src/opennest_commands/opennest_commands.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "opennest_commands build failed" }

# --- get yak.exe ---
$yak = Join-Path $root "yak.exe"
$downloadedYak = $false
if (-not (Test-Path $yak)) {
    Write-Host "==> Downloading yak.exe" -ForegroundColor Cyan
    curl.exe -sL "https://files.mcneel.com/yak/tools/latest/yak.exe" -o $yak
    $downloadedYak = $true
}

try {
    # --- stage ---
    Write-Host "==> Staging package contents" -ForegroundColor Cyan
    $STAGE = Join-Path $env:TEMP "yak_win_stage"
    Remove-Item -Recurse -Force $STAGE -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force "$STAGE\net48","$STAGE\net7.0-windows" | Out-Null

    Copy-Item "$PKG\manifest.yml","$PKG\icon.png" $STAGE

    # net48 (Rhino 6/7)
    Copy-Item "src\opennest_2\bin\Release\net48\DataB.OpenNest.gha"           "$STAGE\net48\"
    Copy-Item "src\opennest_2\bin\Release\net48\nfp_nest.dll"                 "$STAGE\net48\"
    Copy-Item "src\opennest_2\bin\Release\net48\nest_physics.dll"             "$STAGE\net48\"
    Copy-Item "src\opennest_commands\bin\Release\net48\opennest_commands.rhp" "$STAGE\net48\"

    # net7.0-windows (Rhino 8)
    Copy-Item "src\opennest_2\bin\Release\net7.0-windows\DataB.OpenNest.gha"                    "$STAGE\net7.0-windows\"
    Copy-Item "src\opennest_2\bin\Release\net7.0-windows\nfp_nest.dll"                          "$STAGE\net7.0-windows\"
    Copy-Item "src\opennest_2\bin\Release\net7.0-windows\nest_physics.dll"                      "$STAGE\net7.0-windows\"
    Copy-Item "src\opennest_commands\bin\Release\net7.0-windows\opennest_commands.rhp"                    "$STAGE\net7.0-windows\"
    Copy-Item "src\opennest_commands\bin\Release\net7.0-windows\opennest_commands.deps.json"              "$STAGE\net7.0-windows\"
    Copy-Item "src\opennest_commands\bin\Release\net7.0-windows\opennest_commands.runtimeconfig.json"     "$STAGE\net7.0-windows\"

    # --- build the .yak ---
    Write-Host "==> Running yak build" -ForegroundColor Cyan
    Push-Location $STAGE
    & $yak build --platform win
    Pop-Location

    $anyTag  = "datab_opennest-$verShort-any-win.yak"
    $rh8Tag  = "datab_opennest-$verShort-rh8_0-win.yak"
    if (-not (Test-Path "$STAGE\$anyTag")) { throw "yak build did not produce $anyTag" }

    # --- retag -any- -> -rh8_0- and install into the repo ---
    Write-Host "==> Retagging and installing into $PKG" -ForegroundColor Cyan
    Move-Item "$STAGE\$anyTag" "$STAGE\$rh8Tag" -Force
    Remove-Item "$PKG\*.yak" -Force -ErrorAction SilentlyContinue
    Copy-Item "$STAGE\$rh8Tag" $PKG
    Remove-Item -Recurse -Force "$PKG\net48","$PKG\net7.0-windows" -ErrorAction SilentlyContinue
    Copy-Item -Recurse "$STAGE\net48","$STAGE\net7.0-windows" $PKG

    Write-Host "Done: $PKG\$rh8Tag" -ForegroundColor Green
}
finally {
    if ($downloadedYak) { Remove-Item $yak -Force -ErrorAction SilentlyContinue }
}
