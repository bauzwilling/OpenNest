# Build the native C++ engines and place the DLLs where the managed build expects them.
#
#   nfp_nest.dll     -> src/opennest_cpp/build/Release/   (opennest_2 PostBuild copies it next to the .gha)
#   nest_physics.dll -> src/opennest_2/                   (checked-in location the PostBuild copies from)
#
# Run this ONLY when the C++ under src/opennest_cpp or src/nest_physics_cpp changed.
# Requires: CMake 3.20+, Visual Studio 2022 (MSVC).
#
#   pwsh scripts/build_native.ps1            # build both engines
#   pwsh scripts/build_native.ps1 -Nfp       # only nfp_nest
#   pwsh scripts/build_native.ps1 -Physics   # only nest_physics

param(
    [switch]$Nfp,
    [switch]$Physics
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot   # repo root (scripts/ is one level down)
Set-Location $root

# No flag given -> build both.
if (-not $Nfp -and -not $Physics) { $Nfp = $true; $Physics = $true }

if ($Nfp) {
    Write-Host "==> Building nfp_nest.dll" -ForegroundColor Cyan
    cmake -S src/opennest_cpp -B src/opennest_cpp/build -A x64
    cmake --build src/opennest_cpp/build --config Release --target nfp_nest
    if ($LASTEXITCODE -ne 0) { throw "nfp_nest build failed" }
    Write-Host "    -> src/opennest_cpp/build/Release/nfp_nest.dll" -ForegroundColor Green
}

if ($Physics) {
    Write-Host "==> Building nest_physics.dll" -ForegroundColor Cyan
    cmake -S src/nest_physics_cpp -B src/nest_physics_cpp/build -A x64
    cmake --build src/nest_physics_cpp/build --config Release --target nest_physics
    if ($LASTEXITCODE -ne 0) { throw "nest_physics build failed" }
    Copy-Item "src/nest_physics_cpp/build/Release/nest_physics.dll" "src/opennest_2/nest_physics.dll" -Force
    Write-Host "    -> src/opennest_2/nest_physics.dll" -ForegroundColor Green
}

Write-Host "Native build done. Now run scripts/build_yak.ps1" -ForegroundColor Cyan
