# Building the Yak packages (DataB_OpenNest)

How to build the **DataB_OpenNest** Yak package (`.yak`) locally after changing the engine or the
plug-ins. This is the private DataB fork of OpenNest; it ships as `DataB.OpenNest.gha` so it never
collides with the public `OpenNest` package on Yak.

> **TL;DR** — rebuild the binaries → bump the manifest version → stage the files into `net48/` +
> `net7.0-windows/` folders next to `manifest.yml` → run `yak build --platform win` → retag `-any-` to
> `-rh8_0-` → drop the result into `grasshopper_plugin/datab_opennest_win/`.

---

## What a package contains

A `.yak` is just a ZIP with a `manifest.yml`, an `icon.png`, and one folder **per target framework**.
The Windows package has two:

```
manifest.yml
icon.png
net48/                          # Rhino 6/7 (.NET Framework 4.8)
  DataB.OpenNest.gha
  nfp_nest.dll                  # native NFP/GA engine
  nest_physics.dll              # native collision engine
  opennest_commands.rhp         # the OpenNest Rhino command
net7.0-windows/                 # Rhino 8 (.NET 7)
  DataB.OpenNest.gha
  nfp_nest.dll
  nest_physics.dll
  opennest_commands.rhp
  opennest_commands.deps.json
  opennest_commands.runtimeconfig.json
```

> `minkowski.dll` used to ship here but was **dropped** — the Minkowski/NFP interop is now entirely
> inside `nfp_nest.dll`. Don't add it back.

---

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| CMake | 3.20+ | Configures/builds the native engine |
| MSVC | Visual Studio 2022 | Compiles `nfp_nest.dll` on Windows |
| .NET SDK | 8.0.x+ | Builds the `.gha` / `.rhp` (a newer SDK works — the projects set `EnableWindowsTargeting=true` so `net48` builds too) |
| Yak | ships with Rhino | McNeel's package tool, at `C:\Program Files\Rhino 8\System\Yak.exe`. Only download a standalone copy if you have no Rhino (step 4) |

macOS `.dylib` engines can only be built on a Mac, so the **mac package cannot be produced on
Windows**. Build it via CI (`.github/workflows/publish.yml`) or on a Mac. See
[Cross-platform / CI](#cross-platform--the-ci-release) below.

---

## Step-by-step (Windows)

Run everything from the repo root.

### 1. Rebuild the native engine (only if C++ under `src/opennest_cpp` changed)

```bash
cmake -S src/opennest_cpp -B src/opennest_cpp/build -A x64
cmake --build src/opennest_cpp/build --config Release --target nfp_nest
# -> src/opennest_cpp/build/Release/nfp_nest.dll
```

`nest_physics.dll` is shipped pre-built under `src/opennest_2/`; only rebuild it (same pattern, from
`src/nest_physics_cpp`) if you changed the collision solver.

### 2. Build the managed plug-ins

The `opennest_2` PostBuild step copies `nfp_nest.dll` / `nest_physics.dll` next to each `.gha`, so
build the engine **first**.

```bash
dotnet build src/opennest_2/opennest_2.csproj        -c Release   # DataB.OpenNest.gha (net48, net7.0-windows, net7.0)
dotnet build src/opennest_commands/opennest_commands.csproj -c Release   # opennest_commands.rhp
```

Sanity check that the fresh engine landed next to the `.gha` (sizes/timestamps should match
`src/opennest_cpp/build/Release/nfp_nest.dll`):

```bash
ls -l src/opennest_2/bin/Release/net7.0-windows/nfp_nest.dll \
      src/opennest_2/bin/Release/net48/nfp_nest.dll
```

### 3. Bump the package version

The engine binary changed, so the package needs a new version — Yak **rejects a re-pushed duplicate
version**. Edit `grasshopper_plugin/datab_opennest_win/manifest.yml`:

```yaml
version: 2.89.2.0   # was 2.89.1.0 — bump the patch for an engine/bugfix change
```

Use 4 parts (`major.minor.patch.0`) to match the existing convention.

### 4. Get Yak

Use the one that ships with Rhino:

```bash
YAK="/c/Program Files/Rhino 8/System/Yak.exe"
```

Only if you have no Rhino installed, download the standalone build:

```bash
curl -sL "https://files.mcneel.com/yak/tools/latest/yak.exe" -o yak.exe && YAK=./yak.exe
```

That standalone build is **deprecated** and the current copy dies with a
`NullReferenceException in Yak.Helpers.Content.GetInspector` on this package — prefer Rhino's. If you
do download it, leave it in the repo root while building and **delete it afterward**: it must not be
committed (a 10 MB binary, and it isn't gitignored).

### 5. Stage the package contents

Copy the built files into a clean staging directory with the framework layout shown above:

```bash
STAGE=/tmp/yak_win_stage
rm -rf "$STAGE" && mkdir -p "$STAGE/net48" "$STAGE/net7.0-windows"

cp grasshopper_plugin/datab_opennest_win/manifest.yml "$STAGE/"
cp grasshopper_plugin/datab_opennest_win/icon.png     "$STAGE/"

# net48 (Rhino 6/7)
cp src/opennest_2/bin/Release/net48/DataB.OpenNest.gha         "$STAGE/net48/"
cp src/opennest_2/bin/Release/net48/nfp_nest.dll               "$STAGE/net48/"
cp src/opennest_2/bin/Release/net48/nest_physics.dll           "$STAGE/net48/"
cp src/opennest_commands/bin/Release/net48/opennest_commands.rhp "$STAGE/net48/"

# net7.0-windows (Rhino 8)
cp src/opennest_2/bin/Release/net7.0-windows/DataB.OpenNest.gha "$STAGE/net7.0-windows/"
cp src/opennest_2/bin/Release/net7.0-windows/nfp_nest.dll       "$STAGE/net7.0-windows/"
cp src/opennest_2/bin/Release/net7.0-windows/nest_physics.dll   "$STAGE/net7.0-windows/"
cp src/opennest_commands/bin/Release/net7.0-windows/opennest_commands.rhp              "$STAGE/net7.0-windows/"
cp src/opennest_commands/bin/Release/net7.0-windows/opennest_commands.deps.json        "$STAGE/net7.0-windows/"
cp src/opennest_commands/bin/Release/net7.0-windows/opennest_commands.runtimeconfig.json "$STAGE/net7.0-windows/"
```

### 6. Build the `.yak`

```bash
cd "$STAGE" && "$YAK" build --platform win && cd -
```

Two warnings are expected and harmless — the `.rhp` carries its own assembly version/name, which the
manifest deliberately does not track:

```
WARNING: Content version doesn't match manifest: '2.89.1.0+<sha>' != '2.89.4.0'
WARNING: Content name doesn't match manifest: 'opennest_commands' != 'DataB_OpenNest'
```

Rhino's Yak reads the `net7.0-windows` plug-in and tags the file for Rhino 8 directly:

```
datab_opennest-2.89.4-rh8_0-win.yak
```

The deprecated standalone `yak.exe` instead prints `WARNING: No files found that could be inspected`
and tags the file `-any-`, because it can't find a plug-in at the package **root** to infer the Rhino
version. Rename it — the package does target Rhino 8 (its newest command is
`net7.0-windows`/RhinoCommon 8), and this mirrors the CI's guide-sanctioned rename:

```bash
mv "$STAGE/datab_opennest-2.89.4-any-win.yak" "$STAGE/datab_opennest-2.89.4-rh8_0-win.yak"
```

### 7. Install into the repo

Replace the committed package and refresh the loose framework folders so the repo tree matches the
`.yak`:

```bash
PKG=grasshopper_plugin/datab_opennest_win
rm -f "$PKG"/*.yak
cp "$STAGE/datab_opennest-2.89.4-rh8_0-win.yak" "$PKG/"

rm -rf "$PKG/net48" "$PKG/net7.0-windows"
cp -r "$STAGE/net48" "$STAGE/net7.0-windows" "$PKG/"

rm -f yak.exe   # only if you downloaded one; don't commit it
```

### 8. Verify

Unzip the result and confirm the engine inside is your rebuild and the version is right:

```bash
cd /tmp && rm -rf yakverify && mkdir yakverify && cd yakverify
unzip -q "$OLDPWD/grasshopper_plugin/datab_opennest_win/datab_opennest-2.89.4-rh8_0-win.yak"
ls -l net48/nfp_nest.dll net7.0-windows/nfp_nest.dll   # sizes match the fresh build
grep version: manifest.yml
```

---

## Installing the package into Rhino (to test)

Drag the `.yak` onto Rhino, or:

```
_PackageManager        # search for it, or install from file
```

or point Yak at the local folder as a source. After install, restart Rhino; the `DataB_OpenNest`
components appear in Grasshopper and the `OpenNest` command becomes available.

---

## Publishing to Yak (optional)

Building does **not** publish. To push (needs a `YAK_TOKEN` from `yak login`):

```bash
"$YAK" push grasshopper_plugin/datab_opennest_win/datab_opennest-2.89.4-rh8_0-win.yak
```

A version can only be pushed **once** — bump the manifest before every push.

---

## Cross-platform / the CI release

The upstream public `OpenNest` package (Windows **and** macOS, Rhino 8 **and** 9) is built end-to-end
by `.github/workflows/publish.yml` on every push to `main`: it bumps the version once, builds all
native engines on both OSes (mac dylibs are universal x86_64 + arm64), builds the `.gha` + `.rhp`s,
and assembles/pushes four distributions (`{rh8,rh9} × {win,mac}`). Use that workflow — not this local
process — for a full cross-platform release. This page covers the **local Windows DataB fork build**,
which is what you need after an engine change you want to test or ship privately.

### The mac DataB package

Every DataB release needs a **matching `-mac` package**. A Mac that installs the `-win` `.yak` loads
the `net48/` folder and fails with `Unable to load shared library 'nfp_nest'`, because the Windows
package contains no `.dylib`s. Never mix a newer `.gha` with older dylibs either: the engine's C ABI
changes between releases.

Build it with the **Build DataB_OpenNest (macOS package)** workflow (`.github/workflows/datab_mac.yml`).
It runs from the same commit as the Windows package in one of three ways:

- **Automatically on a release tag:** pushing a `v*` tag whose commit contains the workflow builds that tag.
- **For an older ref whose tree predates the workflow:** branch off it, add only the workflow file, and
  push to `datab-mac/<anything>`. A push trigger reads the workflow from the pushed branch, so this works
  without the file being on `main`:

  ```bash
  git checkout -b datab-mac/v2.89.7.0 v2.89.7.0
  git checkout ci/datab-mac-package -- .github/workflows/datab_mac.yml   # or any branch that has it
  git commit -m "ci: add datab_mac workflow" && git push -u bauzwilling datab-mac/v2.89.7.0
  ```

- **Manually**, once the file is on `main`:
  `gh workflow run datab_mac.yml -R bauzwilling/OpenNest -f source_ref=v2.89.7.0`

Then fetch the result:

```bash
gh run download -R bauzwilling/OpenNest <run-id> -n datab_opennest_mac -D grasshopper_plugin/datab_opennest_mac
```

The workflow compiles universal (x86_64 + arm64) dylibs on macOS, builds the `net7.0` `.gha` and `.rhp`,
takes the version from `datab_opennest_win/manifest.yml` at that ref, and uploads
`datab_opennest-<ver>-rh8_0-mac.yak` plus the loose package files. It does not push or commit. Delete the
old `.yak` in `datab_opennest_mac/`, then commit the refreshed folder.

To build by hand on a Mac instead: build both engines with `cmake -DCMAKE_OSX_ARCHITECTURES="x86_64;arm64" …`,
stage them into `grasshopper_plugin/datab_opennest_mac/` with the `net7.0` build outputs, set the manifest
version to the Windows one, and run `yak build --platform mac`.
