# Build scripts

Two PowerShell scripts that automate the local Windows DataB_OpenNest build. Run both from the
**repo root**. For the full manual walkthrough see [../docs/building_yak_packages.md](../docs/building_yak_packages.md).

```
pwsh scripts/build_native.ps1   # (only if C++ changed) build nfp_nest.dll + nest_physics.dll
pwsh scripts/build_yak.ps1      # build .gha + .rhp, produce & install the .yak
pwsh scripts/release_github.ps1 # publish the .yak as a GitHub Release asset
```

## `build_native.ps1` — native C++ engines

Builds the C++ engines with CMake and drops the DLLs where the managed build reads them:

| DLL | Lands at |
|---|---|
| `nfp_nest.dll` | `src/opennest_cpp/build/Release/` |
| `nest_physics.dll` | `src/opennest_2/` |

Run **only when the C++ under `src/opennest_cpp` or `src/nest_physics_cpp` changed** — otherwise the
committed DLLs are fine and you can skip straight to `build_yak.ps1`.

```
pwsh scripts/build_native.ps1            # both engines
pwsh scripts/build_native.ps1 -Nfp       # only nfp_nest
pwsh scripts/build_native.ps1 -Physics   # only nest_physics
```

Requires CMake 3.20+ and Visual Studio 2022 (MSVC).

## `build_yak.ps1` — managed plug-ins + package

1. Builds `DataB.OpenNest.gha` (opennest_2) and `opennest_commands.rhp`.
2. Stages the `net48/` + `net7.0-windows/` layout.
3. Downloads `yak.exe` (if missing), runs `yak build`, retags `-any-` → `-rh8_0-`.
4. Installs the `.yak` and refreshed framework folders into `grasshopper_plugin/datab_opennest_win/`,
   then deletes `yak.exe`.

```
pwsh scripts/build_yak.ps1
```

Uses whatever `nfp_nest.dll` / `nest_physics.dll` are already in place, so run `build_native.ps1`
first if the engine changed.

## `release_github.ps1` — publish to GitHub Releases

Uploads the built `.yak` to a GitHub Release so others can download it without Yak. It reads the
version from `manifest.yml`, finds `datab_opennest-<version>-rh8_0-win.yak`, and creates a release
tagged `v<version>` on `github.com/bauzwilling/OpenNest` with the `.yak` attached.

One-time setup — install and log in the GitHub CLI:

```
gh auth login          # pick GitHub.com -> HTTPS -> browser
```

Then, after building the package:

```
pwsh scripts/build_yak.ps1        # produces the .yak
pwsh scripts/release_github.ps1   # creates release v2.89.2.0 and uploads the .yak
```

Options:

```
pwsh scripts/release_github.ps1 -Draft            # create as a draft to review before publishing
pwsh scripts/release_github.ps1 -Notes "message"  # custom release notes
```

Behaviour:

- If the tag **doesn't exist**, it creates the release (tag = `v<version>`, e.g. `v2.89.2.0`) from the
  current commit and attaches the `.yak`.
- If the tag **already exists**, it re-uploads the asset with `--clobber` (replaces the old file).
- **Bump the version in `manifest.yml` and rebuild before each new release** — a fresh version means a
  fresh tag. (Re-running on the same version just overwrites the existing release's asset.)

> This publishes to **GitHub Releases**, not to the Yak server. To push to Yak instead, see
> [../docs/building_yak_packages.md](../docs/building_yak_packages.md#publishing-to-yak-optional).

## Updating the version

The version lives in **one place** — `build_yak.ps1` reads it from there and names the package to
match. Edit [../grasshopper_plugin/datab_opennest_win/manifest.yml](../grasshopper_plugin/datab_opennest_win/manifest.yml):

```yaml
version: 2.89.2.0   # major.minor.patch.0 — bump the patch for an engine/bugfix change
```

Use 4 parts (`major.minor.patch.0`). Bump it **before every build you intend to publish** — Yak
rejects a re-pushed duplicate version. No other file needs editing.
