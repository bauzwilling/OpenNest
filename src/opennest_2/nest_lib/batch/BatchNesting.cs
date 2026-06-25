using System;
using System.Collections.Generic;
using System.Linq;

// ENGINE-AGNOSTIC batch-nesting layer. Nothing in this namespace references Rhino, nest_geo, or any
// concrete nester — the only contact with a real engine is through IBatchNestEngine. The idea: a good
// NFP nester gives great results in acceptable time up to ~50 parts, but degrades on 1000s. So we split
// the parts into batches the engine handles well, nest each, KEEP every full sheet, and re-pool the
// (typically not-full) LAST sheet of every batch into the next round — repeating until the leftover fits
// in a single batch. HOW the parts are split into batches is pluggable (IPartDistributor); the first
// method preserves the area distribution of the whole set inside each batch (AreaNormalDistributor).
namespace nest_lib.batch
{
    // A single nestable instance, as the distributor sees it. Id is a stable, run-wide identifier the
    // engine adapter can map back to its own geometry; Area drives area-aware distribution. Copies are
    // EXPANDED to instances upstream (one descriptor per physical piece) so "the parts on the last sheet"
    // is always a set of concrete instances, never a fractional quantity.
    public readonly struct PartDescriptor
    {
        public readonly int Id;
        public readonly double Area;
        public PartDescriptor(int id, double area) { Id = id; Area = area; }
    }

    // What an engine returns for one nested batch: how many sheets it used and which sheet each instance
    // landed on (-1 = could not be placed). EngineData is opaque, owned by the adapter — typically the
    // per-instance placement transforms — and is read back only by that same adapter during assembly.
    public sealed class BatchSolveResult
    {
        public int SolveId;                              // unique per SolveBatch call (set by the orchestrator)
        public int SheetCount;                           // sheets this batch occupied
        public IReadOnlyDictionary<int, int> SheetOf;    // instanceId -> local sheet index (0..SheetCount-1), or -1
        public object EngineData;                        // adapter-private payload (e.g. transforms)

        public int LastSheet => SheetCount - 1;
    }

    // The pluggable nester. SolveBatch nests EXACTLY the given instances (a subset chosen by the
    // distributor) onto a fresh stack of identical sheets and reports where each one landed. The engine
    // may keep whatever it needs in BatchSolveResult.EngineData to rebuild geometry later. Implementations
    // are expected to be sequential/process-global safe — the orchestrator never calls this concurrently.
    public interface IBatchNestEngine
    {
        BatchSolveResult SolveBatch(IReadOnlyList<int> instanceIds);
    }

    // Splits a set of instances into batches the engine can nest well (typically <= batchSize each).
    // Registered in DistributorRegistry so the GH dropdown lists every method without per-method edits.
    public interface IPartDistributor
    {
        // Stable key (used in serialization / dropdown tokens).
        string Key { get; }
        // Human label for the dropdown.
        string Label { get; }
        // One-line tooltip.
        string Description { get; }
        // Partition `parts` into batches; each batch is a list of instance Ids. No instance is dropped or
        // duplicated; batch sizes are <= batchSize (the last may be smaller).
        List<List<int>> Distribute(IReadOnlyList<PartDescriptor> parts, int batchSize);
    }

    // One placed instance in the final layout. GlobalSheet is the consolidated, run-wide sheet index
    // assigned by the orchestrator; Solve + LocalSheet let the adapter pull the right placement transform.
    public sealed class Placement
    {
        public int InstanceId;
        public BatchSolveResult Solve;
        public int LocalSheet;
        public int GlobalSheet;
    }

    public sealed class BatchNestPlan
    {
        public List<Placement> Placements = new List<Placement>();
        public List<int> Unplaced = new List<int>();   // instances no sheet could hold (engine returned -1 in the final pass)
        public int TotalSheets;
        public int Rounds;
        public List<string> Log = new List<string>();
    }

    // Drives the whole multi-batch / multi-round consolidation. Pure bookkeeping over instance Ids — it
    // asks the distributor how to split and the engine to nest, then decides what to commit vs re-pool.
    public sealed class BatchNestOrchestrator
    {
        private readonly IBatchNestEngine _engine;
        private readonly IPartDistributor _distributor;
        private readonly int _batchSize;
        private readonly int _maxRounds;
        private int _solveCounter;

        // Optional cooperative cancel + progress hooks (wired to the GH component's ESC / live label).
        public Func<bool> IsCancelled;
        public Action<string> OnProgress;

        public BatchNestOrchestrator(IBatchNestEngine engine, IPartDistributor distributor,
                                     int batchSize, int maxRounds = 12)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _distributor = distributor ?? throw new ArgumentNullException(nameof(distributor));
            _batchSize = Math.Max(1, batchSize);
            _maxRounds = Math.Max(1, maxRounds);
        }

        private BatchSolveResult Solve(IReadOnlyList<int> ids)
        {
            var res = _engine.SolveBatch(ids);
            if (res != null) res.SolveId = _solveCounter++;
            return res;
        }

        private bool Cancelled => IsCancelled != null && IsCancelled();
        private void Log(BatchNestPlan plan, string msg) { plan.Log.Add(msg); OnProgress?.Invoke(msg); }

        public BatchNestPlan Run(IReadOnlyList<PartDescriptor> parts)
        {
            var plan = new BatchNestPlan();
            if (parts == null || parts.Count == 0) return plan;

            // Area lookup so later rounds (which only carry Ids) can still build PartDescriptors.
            var areaOf = new Dictionary<int, double>(parts.Count);
            foreach (var p in parts) areaOf[p.Id] = p.Area;

            // committed[(solveId, localSheet)] -> the eventual global sheet index (assigned lazily).
            var committed = new List<Placement>();
            var remaining = parts.Select(p => p.Id).ToList();
            int round = 0;

            while (remaining.Count > 0 && !Cancelled)
            {
                // Final consolidation: the leftover fits in one batch — nest it once and KEEP every sheet
                // (this is the user's "until we're done"). Any instance the engine still can't place is
                // reported as unplaced rather than looping forever.
                if (remaining.Count <= _batchSize)
                {
                    Log(plan, $"round {round}: final pass, {remaining.Count} part(s)");
                    var res = Solve(remaining);
                    if (res == null) { plan.Unplaced.AddRange(remaining); break; }
                    foreach (var id in remaining)
                    {
                        int s = res.SheetOf != null && res.SheetOf.TryGetValue(id, out var v) ? v : -1;
                        if (s < 0) plan.Unplaced.Add(id);
                        else committed.Add(new Placement { InstanceId = id, Solve = res, LocalSheet = s });
                    }
                    round++;
                    break;
                }

                var descriptors = remaining.Select(id => new PartDescriptor(id, areaOf.TryGetValue(id, out var a) ? a : 0.0)).ToList();
                var batches = _distributor.Distribute(descriptors, _batchSize);
                Log(plan, $"round {round}: {remaining.Count} part(s) -> {batches.Count} batch(es) of <= {_batchSize}");

                var nextLeftover = new List<int>();
                int committedThisRound = 0;
                for (int b = 0; b < batches.Count && !Cancelled; b++)
                {
                    var batch = batches[b];
                    if (batch == null || batch.Count == 0) continue;
                    OnProgress?.Invoke($"round {round}: batch {b + 1}/{batches.Count} ({batch.Count} parts)");
                    var res = Solve(batch);
                    if (res == null) { nextLeftover.AddRange(batch); continue; }

                    int last = res.LastSheet;
                    foreach (var id in batch)
                    {
                        int s = res.SheetOf != null && res.SheetOf.TryGetValue(id, out var v) ? v : -1;
                        // Re-pool the not-full LAST sheet (and anything unplaced) into the next round; KEEP
                        // every earlier (full) sheet. A batch that needed only one sheet commits nothing —
                        // its parts all funnel down, which the no-progress guard below catches.
                        if (s < 0 || s >= last) nextLeftover.Add(id);
                        else { committed.Add(new Placement { InstanceId = id, Solve = res, LocalSheet = s }); committedThisRound++; }
                    }
                }

                round++;
                // No-progress guard: if a whole round committed nothing (e.g. every batch fit on one sheet,
                // or the leftover stopped shrinking) re-batching can't help — nest the whole leftover as a
                // final pass and keep all its sheets.
                if (committedThisRound == 0 || nextLeftover.Count >= remaining.Count || round >= _maxRounds)
                {
                    Log(plan, $"round {round}: consolidating remaining {nextLeftover.Count} part(s) in a final pass");
                    var leftover = nextLeftover;
                    // Nest the leftover in <=batchSize chunks and keep every sheet (no more re-pooling).
                    for (int i = 0; i < leftover.Count && !Cancelled; i += _batchSize)
                    {
                        var chunk = leftover.Skip(i).Take(_batchSize).ToList();
                        var res = Solve(chunk);
                        if (res == null) { plan.Unplaced.AddRange(chunk); continue; }
                        foreach (var id in chunk)
                        {
                            int s = res.SheetOf != null && res.SheetOf.TryGetValue(id, out var v) ? v : -1;
                            if (s < 0) plan.Unplaced.Add(id);
                            else committed.Add(new Placement { InstanceId = id, Solve = res, LocalSheet = s });
                        }
                    }
                    break;
                }

                remaining = nextLeftover;
            }

            // Assign consolidated global sheet indices: each distinct (solve, local sheet) that has at
            // least one committed instance becomes one output sheet, numbered in commit order.
            var sheetIndex = new Dictionary<long, int>();
            foreach (var pl in committed)
            {
                long key = ((long)pl.Solve.SolveId << 32) | (uint)pl.LocalSheet;
                if (!sheetIndex.TryGetValue(key, out var g)) { g = sheetIndex.Count; sheetIndex[key] = g; }
                pl.GlobalSheet = g;
            }

            plan.Placements = committed;
            plan.TotalSheets = sheetIndex.Count;
            plan.Rounds = round;
            Log(plan, $"done: {plan.TotalSheets} sheet(s), {plan.Placements.Count} placed, {plan.Unplaced.Count} unplaced, {round} round(s)");
            return plan;
        }
    }
}
