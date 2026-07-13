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
- **Unplaced parts** (parts too big for any sheet, or that the engine can't place after the final
  consolidation/escalation) are reported as unplaced rather than dropped.
- **Distribution method** (dropdown) only changes how parts are grouped, not how many batches
  (`ceil(N / Batch Size)`). *Area Balanced* (default) equalizes total area per batch; it does **not**
  fix a too-small Batch Size.

## What the engine does automatically when a round stalls

If a re-batching round commits nothing (every batch fit on one sheet), the orchestrator escalates:
1. switch to full `Batch Size` sequential chunks (fuller batches that can overflow a sheet),
2. if still nothing, nest **all** the leftover as one uncapped batch,
3. if that still can't place them, mark them unplaced.

This recovers gracefully, but it's a safety net — it is **not** a substitute for setting `Batch Size`
correctly relative to parts-per-sheet.
