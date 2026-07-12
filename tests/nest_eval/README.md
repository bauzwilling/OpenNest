# nest_eval — automated test + eval harness

Runs both OpenNest engines **Rhino-free**, gates correctness, and dumps packing-quality
metrics so an agent (or CI) can tell whether a change made nesting better or worse.

It drives the same P/Invoke wrappers the standalone console uses:

- **nfp** — OpenNest2 NFP + genetic algorithm (`nfp_nest.dll`)
- **collision** — OpenNestCollision penetration-depth relaxation (`nest_physics.dll`)

## Run

```sh
dotnet test tests/nest_eval          # correctness gate (pass/fail)
```

Native solver DLLs are copied next to the test assembly by the build (`CopyNativeDlls`).
The process is forced to **x64** so the bare-name P/Invoke binds to the native x64 DLLs.

## What each test does

One test per `(engine, dataset)` over three deterministic, self-contained datasets
(`rects`, `concave`, `mixed` — defined in `Datasets.cs`, no external files). Each test:

1. runs the engine on the dataset (fixed seed → reproducible),
2. **gates correctness** with asserts:
   - no overlap between placed parts (exact area via bundled Clipper, tol = 0.1% of part area),
   - no part outside its sheet,
   - all parts placed,
3. records quality metrics to `eval-results/`.

## Reading the result — eval your progress

After the run, look at **`eval-results/summary.md`** (and `results.csv` for tooling):

| column | meaning |
|--------|---------|
| `placed/total` | parts placed — must be all |
| `sheets` | sheets used — fewer is better |
| `util` | **utilization = part area / used sheet area — the quality signal, higher is tighter** |
| `overlap` / `oob` | correctness — must stay ~0 |
| `wall ms` | solve time |

A **passing** run means correct. To judge whether a change *improved* packing, compare the
`util` (and `sheets` / `wall ms`) columns against the previous run's `summary.md`.

## Files

- `Datasets.cs` — deterministic part sets + the 510×635 test sheet.
- `NestRunner.cs` — invokes each engine, returns resolved placements + wall time.
- `Evaluator.cs` — computes utilization / overlap / out-of-bounds (Clipper).
- `NestGeom.cs` — ring flatten + placement transform.
- `EvalReport.cs` — writes `eval-results/{results.csv,summary.md}`.
- `NestingTests.cs` — the xunit theories (the correctness gates).
