# Sheet selection: choosing each sheet as you go

**Status:** implemented: native engine (`nfp_nest_v2`, tests in `src/opennest_cpp/tests`), Grasshopper
(Sheets inputs, OpenNest2 / Batch option) and batch stock tracking (tests in `tests/nest_lib_batch_tests`). Target release: **v2.89.8.0** (`batch_nesting`; this fork stays on 2.89.x, because 2.90+ is reserved for the
upstream OpenNest maintainer).
**Hosts:** the Rhino/Grasshopper plugin and NestingService. Both call the same native engine, so both get
identical behavior.

## 1. Problem

Today the engine **doesn't choose sheets**. `NfpWorker::placeParts` opens `sheets.front()`, fills it, and opens
the next one in list order. With several sheet sizes, the result depends on the order the sheets were supplied
in, not on which sheet suits the remaining parts. A job that leaves 3 small parts on its last sheet still opens a
full-size sheet for them.

**Goal:** let a job offer several **sheet types** (each limited or unlimited) and have the engine pick, **each
time it opens a sheet**, the type that best serves an objective:

- **Minimum total sheet area** (least material).
- **Minimum total price**, given a price **per sheet** of each type. A sheet's price is paid in full whatever it
  holds; its scrap is wasted.

One job may mix types. The last sheet's leftover **counts** as used material, so it isn't free offcut.

## 2. Concepts

| Term | Meaning |
|---|---|
| **Sheet type** | One outline plus holes, a **quantity** (`-1` = unlimited, `N ≥ 1` = at most N for the job) and an optional **price** (> 0): the cost of **one whole sheet** of that type, scrap included. |
| **Opened sheet** | One physical sheet the engine starts filling. It is an instance of exactly one type. |
| **Selection mode** | How the next sheet's type is chosen: `0` list order (today), `1` min area, `2` min price. |
| **Fill score** | How well a candidate sheet serves the objective once filled (§4.2). |

## 3. C ABI: `nfp_nest_v2`

`nfp_nest` (v1) stays as it is. A new entry point takes **sheet types**, not an already-expanded list of copies:

```c
NFP_API int nfp_nest_v2(
    // parts: identical to nfp_nest
    int part_count, const int* part_vertex_counts, const double* part_xy,
    const int* part_quantities, const int* part_rotations,
    const int* part_hole_counts, const int* part_hole_vertex_counts, const double* part_hole_xy,
    // sheet TYPES
    int           sheet_type_count,
    const int*    sheet_vertex_counts,        // [sheet_type_count]
    const double* sheet_xy,                   // [sum(sheet_vertex_counts)*2]
    const int*    sheet_hole_counts,          // [sheet_type_count]
    const int*    sheet_hole_vertex_counts,   // [sum(sheet_hole_counts)]
    const double* sheet_hole_xy,              // [sum(sheet_hole_vertex_counts)*2]
    const int*    sheet_quantities,           // [sheet_type_count]  -1 = unlimited, N>=1 = limited
    const double* sheet_prices,               // [sheet_type_count] or NULL (required for mode 2)
    int           sheet_selection,            // 0 = list order, 1 = min area, 2 = min price
    const NfpParams* params,                  // unchanged struct (no ABI break for v1 callers)
    // outputs: per instance, in expansion order (as nfp_nest)
    double* out_tx, double* out_ty, double* out_angle,
    int*    out_sheet_id,                     // index of the OPENED sheet (0..n_sheets-1), -1 = unplaced
    int*    out_part_index,
    // outputs: per opened sheet
    int*    out_sheet_type,                   // [instance_count]; first *out_n_sheets entries valid
    int*    out_n_sheets,
    double* out_fitness);
```

**Rules:**
- `NfpParams` is **not** extended. It is a blittable struct mirrored by every host, and new fields would break
  existing v1 callers. The new inputs are separate arguments.
- `out_sheet_id` numbers **opened** sheets in opening order. `out_sheet_type[k]` is the type of opened sheet `k`.
  At most one sheet is opened per instance, so `instance_count` is a safe buffer length.
- `params->maxSheets` is **not used**, as in `nfp_nest` today (the engine config never reads it).
- **Errors.** Today every invalid input returns `-1`. v2 keeps `-1` for the same invalid inputs as v1, and
  **adds named codes** to the header so hosts can report the cause:
  - `#define NFP_ERR_INVALID_PRICE -10`: mode `2` with `sheet_prices == NULL`, or any price `≤ 0`.
  - `#define NFP_ERR_INVALID_QUANTITY -11`: a quantity of `0` or `< -1`.
  - `#define NFP_ERR_INVALID_SELECTION -12`: an unknown mode.
- **v1 is v2 with one type per supplied sheet,** each with quantity 1, no prices and mode 0. `nfp_nest` must
  keep producing **identical** layouts for the same seed (§8, parity test).

## 4. Algorithm

### 4.1 Sheet pool

Each evaluation of a candidate solution (one `placeParts` call) gets a fresh **pool**: a remaining count per type
(`∞` for unlimited). This mirrors how `sheets` is passed by value today. Copies of one type share **one** `NFP`
whose `source` is the type index, so the inner-fit cache (`getInnerNfp`, keyed by sheet `source`) is computed
once per type. Today it is recomputed for every copy.

### 4.2 Opening a sheet

`placeParts` today does `sheet = sheets.front()`, then the per-sheet placement loop. The per-sheet loop
(candidate rotations, NFP, positioning, compaction) is extracted **unchanged** into:

```text
SheetFill fillSheet(sheetType, remainingParts, config)
    -> { placements, placedParts, placedArea, minwidth, minarea }
```

Then opening a sheet becomes:

| Mode | Choice |
|---|---|
| `0` list order | The first type in input order with stock left. With one type per sheet, quantity 1 (v1), this is exactly `sheets.front()`. |
| `1` min area | Run `fillSheet` for **every** type with stock left. Keep the one with the highest `placedArea / sheetArea` (utilization). |
| `2` min price | Same candidates. Keep the one with the lowest `price / placedArea`: the cost of the whole sheet divided by the part area it would hold, i.e. what each unit of part area costs on that sheet. Scrap earns nothing, so a sheet that would be mostly empty scores badly. |

- **Ties:** keep the smaller sheet area, then the lower type index. This keeps the choice deterministic.
- **Skipped candidates:** a candidate that places **0** parts is never chosen. If every candidate places 0,
  opening stops and the remaining parts are unplaced, as today when a sheet takes nothing.
- **After choosing:** the winner's placements are committed exactly as the current loop does, and its type's
  count goes down by 1. A type at 0 is no longer a candidate.
- **The last sheet** follows the same rule. Because the score is utilization (or cost per part area), a small
  or cheap type that holds all remaining parts beats a large, mostly empty one, so the leftover is charged at
  the smallest (or cheapest) sheet that works.

**Example (mode 2).** Big sheet, price 120, would hold 3.6 m² of parts: 120 ÷ 3.6 = **33.3** per m². Small sheet,
price 60, would hold 1.5 m²: 60 ÷ 1.5 = **40.0** per m². The big sheet is opened. On the last sheet only 1.2 m²
is left: big = 120 ÷ 1.2 = 100, small = 60 ÷ 1.2 = **50**, so the small sheet is opened. The job's total price is
the sum of the prices of all opened sheets (§4.3).

**Why this works:** every part must be placed anyway, so total sheet area = placed area ÷ average
utilization, and total price = placed area × average price per part area. Maximizing each opened sheet's
utilization (mode 1), or minimizing its price per part area (mode 2), is the natural greedy step towards the
minimum. It is greedy, so it is not guaranteed optimal. The GA still searches part order and rotations on top
of it (§4.3), which is where the remaining improvement comes from.

**Cost:** opening a sheet costs `T` placement runs instead of 1, where `T` is the number of candidate types. NFP
work for parts against each other is cached and shared. Each type adds one inner-fit computation per
(part, rotation) **once** (§4.1). For the expected `T ≤ 5` this is acceptable; the host already accepts longer
runs for this feature.

### 4.3 Fitness

Default (non-faithful) mode today compares candidate solutions by `placementLess`: **unplaced area** first, then
`fitness` (the sum of opened sheet areas plus packing terms). With selection on, a **total cost** tier is added
between them:

| Mode | Order of comparison |
|---|---|
| `0` | unplaced area → fitness (unchanged) |
| `1` | unplaced area → **Σ opened sheet area** → fitness |
| `2` | unplaced area → **Σ opened sheet price** → fitness |

The cost tier uses a relative tolerance of `1e-9`, so floating-point noise doesn't hide the packing terms.
`SheetPlacement` gains `totalCost` (`std::optional<double>`, set in modes 1 and 2).

**Faithful mode** (`mode = 0` in `NfpParams`, the C# parity mode) is a reference implementation. It **ignores**
`sheet_selection` (uses list order) and says so in the header comment.

### 4.4 Determinism and threading

Candidate types are evaluated **sequentially** inside one `placeParts` call. Population-level parallelism is
unchanged. With serial evaluation the same seed and input give the same result bit for bit. With parallel
evaluation (as in `nfp_nest` today) threads race to fill the shared NFP cache, and a cached fit differs from a
fresh one in the last floating-point digits, so two runs can end in different, equally valid layouts.

## 5. Grasshopper / Rhino

**Sheet types are sheet sets.** Each input sheet set (one tree branch on Sheets, one surface on Sheets
(surfaces)) is a type. The laid-out copies cycle through the sets, so copy `i` is type `i % setCount`.

| Component | Change |
|---|---|
| **Sheets**, **Sheets (surfaces)** (and the Grasshopper 2 **Sheets**) | New optional list inputs **Quantity** and **Price**, one value per sheet set (the last value repeats). A negative or missing quantity = unlimited; a price is per whole sheet. Stored on `nest_sheets` (`type_quantities`, `type_prices`). Older definitions without these inputs behave as before. |
| **OpenNest2**, **OpenNest2 Batch** (and the Grasshopper 2 **OpenNest2**) | New NestOptions row **Sheet Select**: List Order (default) · Min Area · Min Price. Min Price without a price on every set is a component error. Changing only Quantity / Price re-solves. |
| `rhino_example_cpp` | With Sheet Select = List Order and no limited quantity it calls `nfp_nest` exactly as before. Otherwise it calls `nfp_nest_v2` with one type per set. A type may open at most as many sheets as it has laid-out copies (each opened sheet needs a slot to be drawn in); opened sheet `k` of type `t` is drawn in slot `t + k * setCount`. |

Not changed: **OpenNest1** (takes raw sheet geometry, no sheet sets) and **OpenNestCollision** (different
engine). No separate "Sheet Type" output: the existing **Sheet Id** output is the slot, and its type is
`slot % setCount`.

List Order with v2 (only when a quantity is limited) opens a set's sheets before moving to the next set,
whereas plain v1 follows the laid-out (interleaved) order. Min Area / Min Price don't depend on order, apart
from breaking ties.

## 6. Batch nesting (`nest_lib/batch`)

1. **Stock across batches.** A new optional interface `ISheetStockEngine` (in `BatchNesting.cs`):
   `OpenNestBatchEngine` reserves every sheet a solve opens, and the orchestrator calls `ReleaseSheet` for each
   not-full last sheet whose parts it re-pools (at every re-pool point; never for sheets it keeps). Each solve
   is offered only the stock that is left; when every type is used up the solve returns nothing, so the
   remaining parts end unplaced. Same rule as NestingService (its SPEC 07 §3.5a).
2. **Local sheets in opening order.** v2 slots are not in opening order, so the engine maps them back
   (`rhino_example.OpenedSheetSlots` / `OpenedSheetTypes`); the last local sheet is the last opened one.
3. **Each output sheet is drawn with its own type's outline** (the type's first laid-out copy), not sheet 0's.
4. **`sheet_selection` is passed through** to every batch solve.

## 7. NestingService (host) integration

These changes live in NestingService, after this fork release:

- **Option** `nesting.sheetSelection`: `"drawingOrder"` (default) / `"minArea"` / `"minPrice"`.
- **Sheet `price` metadata** (`NEST` XData). Mode `minPrice` with a sheet missing a price fails the job with
  `INVALID_OPTIONS`, naming the sheet.
- **The adapter calls `nfp_nest_v2`** with one type per sheet definition. Quantity is what's left (limited) or
  `-1` (unlimited); the service's orchestrator keeps its stock tracking across batches.
- **Report:** per placed sheet, its type (already `sheetDefinitionId`). When prices are present, a new summary
  field `totalSheetPrice`.

## 8. Tests (fork)

1. **Parity:** v1 `nfp_nest` and v2 mode 0 (one type per sheet, quantity 1) give identical outputs for a fixed
   seed. A type with quantity N matches N identical v1 sheets exactly in faithful mode; in the default modes it
   places as many parts on as many sheets, but the layout can differ, because the copies share one inner-fit
   cache entry and a cached inner fit differs from a fresh one in the last floating-point digits. The v1
   path itself is unchanged: `nfp_bench` dumps from v2.89.7.0 and from this change are identical across the
   `concave`, `rects` and `rings` datasets, engine modes 0/1/2, serial and parallel, single- and multi-sheet.
2. **Right-sizing the last sheet:** a big type plus a small type, with parts filling 2.2 big sheets. Mode 1 puts
   the remainder on the small type, and its total area is lower than mode 0.
3. **Mixing:** an unlimited big type plus a limited offcut type (quantity 2) that fits the parts better. Mode 1
   uses at most 2 offcuts, then big sheets.
4. **Price:** two same-size types, one cheaper. Mode 2 uses only the cheaper one, unless its stock runs out.
5. **Limits:** a limited type never exceeds its quantity; `maxSheets` caps the total.
6. **Errors:** each new error code is returned for its invalid input.
7. **Determinism:** with serial evaluation the same seed gives identical outputs; a parallel run places as
   many parts, on valid sheets.
8. **Batch:** stock is respected across batches, and output sheets use their own type's outline.

## 9. Keeping the fork and NestingService in step

- **The fork is the source of truth.** A feature lands, is tested and is released here (`release/vX`) before
  NestingService adopts it.
- **NestingService pins a fork release** in `native/OPENNEST_REF`, and vendors the C header for diffing. Its
  `NfpParamsAbiTests` fail if the struct drifts. A new test there will check the `nfp_nest_v2` signature
  against the vendored header.
- **Rhino-free C# files** (`nest_lib/batch`, `nest_lib/preorient`, and any new shared logic) stay
  Rhino-free so both hosts can compile the **same file**. NestingService will check its vendored copies against
  the pinned ref in CI rather than keep hand-edited ports.

## 10. Decisions

1. **No 90° sheet rotation as a candidate.** Supply both orientations as separate types if needed.
2. **Price units are opaque:** only their ratios matter. Mixing currencies is the caller's problem.
3. **Mode 1 does not prefer fewer sheets when areas tie.** Ties keep the smaller sheet, then the lower index.
