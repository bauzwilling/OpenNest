# nest_eval — agent guide

How an agent uses this harness to change a nesting engine and **prove** the change is
correct and measure whether it improved packing. Read `README.md` for the layout; this
doc is the workflow.

## TL;DR loop

```sh
# 1. baseline (before touching anything)
dotnet test tests/nest_eval -c Release
cp eval-results/results.csv eval-results/baseline.csv     # snapshot to compare against

# 2. make ONE focused change to an engine

# 3. rebuild native DLL IF you touched C++ (see "After a C++ change" below)

# 4. re-run and compare
dotnet test tests/nest_eval -c Release
```

- **Exit code 0 / "Passed!"** → the layout is still correct (no overlap, in-bounds, all placed).
  This is a **hard gate**: a change that breaks it is rejected, no matter what it does to `util`.
- **`util` in `eval-results/summary.md` went up** (same or fewer `sheets`, `overlap`/`oob` still 0)
  → the change packed tighter. That is the objective.
- **Tests fail** → the change is invalid. Read the assert message: it names the engine/dataset and
  says whether it was overlap, out-of-bounds, or unplaced parts. Fix or revert.

## Reading the result

`dotnet test` output tells you pass/fail. The numbers live in two files, rewritten every run:

- `eval-results/summary.md` — human table. Look at the `util` column.
- `eval-results/results.csv` — same data for diffing/tooling:
  `dataset,engine,seed,placed,total,sheets,util,overlap_area,oob_area,part_area,wall_ms,fitness`

To decide "better or worse", compare row-by-row against your `baseline.csv` on the **same
dataset+engine**:

| signal | meaning | want |
|--------|---------|------|
| `overlap_area`, `oob_area` | correctness | **must stay 0** (gate fails otherwise) |
| `placed`/`total` | completeness | **must be equal** (gate fails otherwise) |
| `sheets` | bins used | lower or equal |
| `util` | part area / used sheet area | **higher = tighter = better** |
| `wall_ms` | solve time | lower or equal (unless quality is the goal) |
| `fitness` | nfp GA internal score | informational only — **do not optimize against it**, use `util` |

`part_area` is fixed per dataset (same parts), so it's your sanity check that you compared like
with like.

Rule of thumb: an improvement is `util` up (or `sheets` down) on one or more datasets, with **no
regression** on any dataset and correctness still green. A change that helps `concave` but hurts
`rects` is a trade-off, not a win — report both.

## After a C++ engine change

The tests call the **native DLLs**, not C++ source. If you edited `src/opennest_cpp` (nfp) or
`src/nest_physics_cpp` (collision), you must rebuild the DLL first, then re-run:

```sh
# nfp_nest.dll  (build tree already configured under src/opennest_cpp/build)
cmake --build src/opennest_cpp/build --config Release

# nest_physics.dll — configure the build tree first if it doesn't exist yet, then build:
cmake -S src/nest_physics_cpp -B src/nest_physics_cpp/build -DCMAKE_BUILD_TYPE=Release
cmake --build src/nest_physics_cpp/build --config Release

dotnet test tests/nest_eval -c Release
```

The test build's `CopyNativeDlls` target copies the freshly built DLL next to the test assembly,
picking it up from `src/opennest_cpp/build/Release/` (nfp) or `src/nest_physics_cpp/build/Release/`
(collision). Note: a **prebuilt `nest_physics.dll` ships at `src/opennest_2/nest_physics.dll`** and
is used if you never build the collision engine yourself — so a fresh checkout runs green without
any C++ build. `CopyNativeDlls` copies fallbacks first and the `build/Release/` outputs last, so a
freshly rebuilt engine DLL always wins over an older shipped/`bin` copy.
so the next `dotnet test` exercises your new binary. If you skip the rebuild you will silently
test the **old** DLL and see no change — always rebuild after a C++ edit.

If you edited only C# (wrappers, this harness), `dotnet test` rebuilds it automatically.

## Determinism

Every run uses a fixed seed (`Seed = 100` in `NestingTests.cs`) and fixed, in-code datasets — so
results are reproducible and a metric delta is caused by *your change*, not RNG. If you widen the
budget to chase quality (`generations` for nfp, `iterBudget` for collision, both in
`NestingTests.cs`), keep it the same across baseline and comparison, and note it — a bigger budget
raising `util` is not an engine improvement.

## Scope & guardrails

- **Never weaken a correctness gate to make a change pass.** The tol in `NestingTests.cs` (0.1% of
  part area) absorbs integer rounding only. If real `overlap`/`oob` appears, the engine is wrong.
- This harness covers the **console/engine layer** (nfp + collision via P/Invoke). It does **not**
  cover the Rhino batch library (`OpenNestBatchEngine`).
- To add a dataset (e.g. a harder concave set), add a case in `Datasets.cs` and an `[InlineData]`
  row in `NestingTests.cs`. Keep it sized to fit within `NestRunner.MaxSheets` (6) sheets so
  "all placed" stays a valid gate.
- To add a metric, extend `NestMetrics` + `Evaluator.Evaluate` and the writers in `EvalReport.cs`.
  If you add a correctness metric, guard it against a synthetic case in `EvaluatorTests.cs` so the
  detector itself is tested.

## Common failures

| symptom | cause | fix |
|---------|-------|-----|
| `native DLL not found` assert | DLL not built / not copied | build the engine (above), confirm the path in `CopyNativeDlls` exists |
| `BadImageFormatException` / P/Invoke crash | 32-bit host vs 64-bit DLL | the project forces x64; don't override `PlatformTarget`/`RuntimeIdentifier` |
| metrics identical after a C++ change | tested the stale DLL | rebuild the native DLL, then `dotnet test` |
| a dataset drops from "all placed" | change made packing worse or budget too low | that's the gate working — the change regressed placement |
