# Batch nesting — limitations & how to set Batch Size

Batch nesting splits a large job into batches the NFP engine handles well (~50 parts), nests each on
its own sheets, **keeps every full sheet**, and re-pools the not-full **last** sheet of every batch into
the next round — repeating until the leftover fits one batch. It exists because the NFP nester gives
great results up to ~50 parts but degrades badly on hundreds/thousands.

## The one rule that matters: Batch Size vs. parts-per-sheet

> **Batch Size must be comfortably larger than the number of parts that fit on a single sheet.**

The engine never nests more than `Batch Size` parts in a single solve. So if a sheet holds **more**
parts than `Batch Size`, **no solve is ever handed enough parts to fill a sheet** — every batch produces
an under-filled sheet, and the plain (non-batch) nester will beat it. This is the root cause of the
"half-empty sheets that never merge" symptom.

Let `C` = how many parts fit on one sheet (parts-per-sheet).

| Batch Size vs. C | What happens |
|---|---|
| `Batch Size < C` | ❌ Sheets can't be filled. Batching is counter-productive — use plain nesting. |
| `Batch Size ≈ C` | ⚠️ Marginal. Each batch ≈ one sheet; little consolidation, fragile. |
| `Batch Size ≥ 2–3 × C` | ✅ Sweet spot. Each batch overflows into several full sheets + one partial; the partials from all batches consolidate cleanly. |

**Recommended:** `Batch Size = 2–4 × parts-per-sheet`, capped at what the engine stays fast on (~50–70).

### Estimating parts-per-sheet

Nest a single sheet's worth once (plain OpenNest, or a small batch) and count how many parts landed on
sheet 0. That count is `C`. Then set `Batch Size ≈ 3 × C`.

Example: a sheet holds ~12 of your parts (`C = 12`) → `Batch Size ≈ 36`. With 108 parts that's 3 batches
of ~36, each filling ~3 sheets — exactly the case batching is built for.

## When to use it

**Use batch nesting when:**
- You have **many** parts (hundreds to thousands) and plain nesting is too slow or degrades.
- **Parts-per-sheet is small relative to Batch Size** (many small parts share a sheet), so `Batch Size ≥ 2–3 × C` is achievable while staying ≤ ~50–70.

**Don't use it (use plain nesting) when:**
- The whole job already fits in one batch (`part count ≤ Batch Size`) — batching just adds overhead.
- A single sheet holds **more** parts than a comfortable Batch Size (large sheet, small parts, `C` in the
  hundreds). Batching physically can't fill sheets here.
- You need the tightest possible packing on a small job — a single global solve packs better than
  independent batches.

## Other limitations

- **Batches are nested independently.** Cross-batch packing only happens for the re-pooled partial
  sheets, not the committed full ones. Global optimality is traded for speed.
- **Determinism vs. sheet template.** Each batch is nested on a fresh copy of the sheet template; if the
  template defines multiple sheets, the engine may spread a batch across them.
- **Sheet stock.** A sheet set's **Quantity** (Sheets component) holds across ALL batches: every solve reserves
  the sheets it opens, and a batch's not-full last sheet is handed back when its parts are re-pooled, so a
  limited set is never used more often than its quantity. Each output sheet is drawn with its own set's outline.
  See [sheet selection](sheet_selection.md).
- **Unplaced parts** (parts too big for any sheet, or that the engine can't place after the final
  consolidation/escalation, or that the **Timeout** ran out before) are reported as unplaced rather than
  dropped.
- **Timeout** is the time for the whole run, split across the solves (see [Timeout](#timeout) below).
- **Distribution method** (dropdown) only changes how parts are grouped, not how many batches
  (`ceil(N / Batch Size)`). *Area Balanced* (default) equalizes total area per batch; it does **not**
  fix a too-small Batch Size.
- **Batch membership matters; the order inside a batch barely does.** The native engine sorts every batch
  largest-area-first before the genetic algorithm starts, so the feed order is set by area, not by how the
  batch was arranged. The one thing the arrangement still affects is the order of parts with *equal* area
  (the sort does not keep their input order), so reordering can change the layout when many parts share an
  area, but not in any consistent direction. That is why the former *Spread In Batch* option was removed in
  2.89.8.0: it was meant to feed each batch large/small alternating, but the engine re-sorted it, and the
  only remaining effect was an arbitrary reshuffle of equal-area parts. Saved files that had it still open;
  the old value is ignored.

## What the engine does automatically when a round stalls

If a re-batching round commits nothing (every batch fit on one sheet), the orchestrator escalates:
1. switch to full `Batch Size` sequential chunks (fuller batches that can overflow a sheet),
2. if still nothing, nest **all** the leftover as one uncapped batch,
3. if that still can't place them, mark them unplaced.

This recovers gracefully, but it's a safety net — it is **not** a substitute for setting `Batch Size`
correctly relative to parts-per-sheet.

## Timeout

**Timeout (s)** (0 = no limit) is the time for the **whole** batch run. Each solve gets a share of the time
left, and when its share runs out it stops and keeps its best layout so far, the same as pressing ESC for that
one solve:

- **A batch** gets a share in proportion to its estimated cost, the **vertex count** of its parts' nesting
  outlines (outer + holes), with one average batch held back for later rounds and the final pass:
  `left × cost(b) / (Σ cost of the batches left in the round + their mean)`. With equal costs that is
  `left / (batches left + 1)`.
- **The final pass** gets all the time left (split by cost when it runs in more than one chunk).
- Every share is at least 1 s.

The split is by vertices, not by part count, because solve time follows the outlines' size: on a 779-part job
whose batches had near-equal part counts and areas, solve times ran from 12 s (≈300 vertices) to 48 s
(≈850), within about 12% of proportional to the vertex count (the part count missed by about 43%). An
equal split gave the heavy batches too little time and left time unused at the end.

**When the time is up**, no further solve runs. The not-full last sheets of the round are **kept as they are**
(nothing would re-nest them now), so no computed sheet is thrown away; parts that never got a sheet (batches
that did not run yet) are reported unplaced. The component then shows `time limit — N sheet(s)`, and a remark
counts the solves that stopped at their share before finishing their **Iterations**. Pressing **ESC** keeps the
last sheets the same way.

Before 2.89.10.0 the Timeout was a single timer for the whole run: when it fired, the running batch stopped,
every later batch was skipped and the re-pooled last sheets of the round were dropped, so all those parts came
back unplaced. A watchdog still stops the run as a safety net if it runs past the Timeout by 10% (at least
10 s).
