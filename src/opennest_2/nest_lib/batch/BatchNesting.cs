using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

// ENGINE-AGNOSTIC batch-nesting layer. Nothing in this namespace references Rhino, nest_geo, or any
// concrete nester — the only contact with a real engine is through IBatchNestEngine. The idea: a good
// NFP nester gives great results in acceptable time up to ~50 parts, but degrades on 1000s. So we split
// the parts into batches the engine handles well, nest each, KEEP every full sheet, and re-pool the
// (typically not-full) LAST sheet of every batch into the next round — repeating until the leftover fits
// in a single batch. HOW the parts are split into batches is pluggable (IPartDistributor); the default
// method gives every batch a nearly equal total part area (AreaNormalDistributor, shown as "Area Balanced").
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
        // Axis-aligned bounding-box size of the part's outer nesting loop. Carried so a shape-aware
        // distributor could derive aspect ratio / compactness without re-reading geometry. The current
        // distributors are area-only and ignore these; the legacy (id, area) constructor leaves them 0.
        public readonly double Width;
        public readonly double Height;
        // Vertex count of the part's nesting outline (outer + holes): the solve-time estimate the orchestrator
        // splits a time limit by (BatchNestOrchestrator.SolveCost). 0 = unknown, counted as 1.
        public readonly int Vertices;

        public PartDescriptor(int id, double area) : this(id, area, 0.0, 0.0, 0) { }
        public PartDescriptor(int id, double area, double width, double height) : this(id, area, width, height, 0) { }
        public PartDescriptor(int id, double area, double width, double height, int vertices)
        {
            Id = id; Area = area; Width = width; Height = height; Vertices = vertices;
        }
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
        public bool StoppedByTimeBudget;                 // the solve reached its time share before its iterations

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

    // Optional engine hook for LIMITED sheet stock (docs/sheet_selection.md). The engine reserves every sheet
    // a solve opens, so batches in one round never use more than the stock. The orchestrator hands back the
    // not-full last sheet of a batch whenever it re-pools that sheet's parts, so a later solve can use that
    // stock again. Sheets it keeps are never handed back.
    public interface ISheetStockEngine
    {
        void ReleaseSheet(BatchSolveResult solve, int localSheet);
    }

    // Optional engine hook for a TIME LIMIT (BatchNestOrchestrator.TimeLimitSeconds). Before each solve the
    // orchestrator sets that solve's share of the time left; the engine stops the search when it runs out and
    // returns its best layout so far as a normal result, flagged BatchSolveResult.StoppedByTimeBudget.
    // 0 = no budget (run the full iterations).
    public interface ITimeBudgetEngine
    {
        void SetTimeBudget(double seconds);
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
        // duplicated; batch sizes are <= batchSize (the last may be smaller). Only membership matters: the
        // native engine re-sorts each batch largest-area-first before nesting; the order within a batch only
        // decides how parts of EQUAL area are ordered (the sort is not stable), which can change the layout
        // but is not a lever for improving it.
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
        public List<int> Unplaced = new List<int>();   // instances without a sheet: the engine returned -1 in the final pass, or the run stopped / ran out of time first
        public int TotalSheets;
        public int Rounds;
        public bool TimeLimitReached;                  // the time limit ran out before every part had a sheet
        public int SolvesStoppedByTimeBudget;          // solves that reached their time share before their iterations
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
        private int _stoppedByBudget;
        private Func<double> _clock = () => 0.0;
        private Dictionary<int, PartDescriptor> _descOf = new Dictionary<int, PartDescriptor>();

        // Optional cooperative cancel + progress hooks (wired to the GH component's ESC / live label).
        public Func<bool> IsCancelled;
        public Action<string> OnProgress;

        // Optional time limit for the whole run, in seconds (0 = none). Each solve gets a share of the time left
        // (TimeShare): a batch in proportion to its SolveCost, the final pass all of it. Once nothing is left no
        // further solve runs: the not-full last sheets of the round are kept as they are (nothing would re-nest
        // them now) and parts without a sheet are unplaced. An ITimeBudgetEngine stops each solve at its share;
        // any other engine is only stopped between solves.
        public double TimeLimitSeconds;
        // Seconds since Run started. Replaceable for tests; defaults to a wall-clock stopwatch.
        public Func<double> Clock;

        // The shortest share a solve gets, so a nearly spent limit still runs a solve that can place something.
        public const double MinShareSeconds = 1.0;

        public BatchNestOrchestrator(IBatchNestEngine engine, IPartDistributor distributor,
                                     int batchSize, int maxRounds = 12)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _distributor = distributor ?? throw new ArgumentNullException(nameof(distributor));
            _batchSize = Math.Max(1, batchSize);
            _maxRounds = Math.Max(1, maxRounds);
        }

        private bool HasTimeLimit => TimeLimitSeconds > 0 && !double.IsInfinity(TimeLimitSeconds);
        private double TimeLeft() => HasTimeLimit ? TimeLimitSeconds - _clock() : double.PositiveInfinity;
        private bool OutOfTime() => HasTimeLimit && TimeLeft() <= 0;

        /// <summary>
        /// A batch's estimated solve time, in arbitrary units: the vertices of its parts' nesting outlines (outer
        /// and holes), each part at least 1. The engine's work grows with the outlines' size more than with the
        /// part count: on a 779-part job with batches of 48-49 parts of near-equal area, solve times ran from 12 s
        /// (300 vertices) to 48 s (850), within about 12% of proportional to the vertex count, where the part
        /// count missed by about 43%. Parts without a vertex count count 1 each, so the split is by part count.
        /// </summary>
        public static double SolveCost(IEnumerable<PartDescriptor> parts)
        {
            double cost = 0;
            if (parts != null) foreach (var p in parts) cost += Math.Max(1, p.Vertices);
            return cost;
        }

        private double SolveCost(IReadOnlyList<int> ids) =>
            ids == null ? 0 : SolveCost(ids.Select(id => _descOf.TryGetValue(id, out var d) ? d : new PartDescriptor(id, 0.0)));

        /// <summary>
        /// The share of <paramref name="left"/> seconds for the first of <paramref name="costs"/> (the solves left,
        /// in order), in proportion to its cost: <c>left × cost[0] / Σ cost</c>. With <paramref name="reserve"/>
        /// (a batch inside a round) one average solve more is counted, <c>left × cost[0] / (Σ cost + mean)</c>, which
        /// holds back time for later rounds and the final pass; with equal costs that is <c>left / (n + 1)</c>.
        /// At least <see cref="MinShareSeconds"/>.
        /// </summary>
        public static double TimeShare(double left, IReadOnlyList<double> costs, bool reserve)
        {
            int n = costs == null ? 0 : costs.Count;
            if (n == 0) return Math.Max(MinShareSeconds, left);
            double total = 0;
            foreach (var c in costs) total += Math.Max(0, c);
            double fraction = total > 0
                ? Math.Max(0, costs[0]) / (total + (reserve ? total / n : 0))
                : 1.0 / (n + (reserve ? 1 : 0));
            return Math.Max(MinShareSeconds, left * fraction);
        }

        // Run one solve with `budget` seconds (ignored without a time limit or a time-budget engine).
        private BatchSolveResult Solve(IReadOnlyList<int> ids, double budget)
        {
            if (_engine is ITimeBudgetEngine timed) timed.SetTimeBudget(HasTimeLimit ? budget : 0);
            var res = _engine.SolveBatch(ids);
            if (res != null)
            {
                res.SolveId = _solveCounter++;
                if (res.StoppedByTimeBudget) _stoppedByBudget++;
            }
            return res;
        }

        // Terminal consolidation: nest `ids` in <=batchSize chunks and KEEP every sheet (no more
        // re-pooling). Anything the engine still can't place is reported as unplaced. Used when the
        // pooled partial sheets are worth re-nesting together (leftover fits a batch / round cap).
        // With a time limit the chunks share all the time left by cost; chunks no time is left for are unplaced.
        private void FinalPass(IReadOnlyList<int> ids, List<Placement> committed, BatchNestPlan plan)
        {
            var chunks = new List<List<int>>();
            for (int i = 0; i < ids.Count; i += _batchSize) chunks.Add(ids.Skip(i).Take(_batchSize).ToList());
            var costs = chunks.Select(c => SolveCost(c)).ToList();
            for (int c = 0; c < chunks.Count && !Cancelled; c++)
            {
                var chunk = chunks[c];
                if (OutOfTime())
                {
                    plan.TimeLimitReached = true;
                    for (int rest = c; rest < chunks.Count; rest++) plan.Unplaced.AddRange(chunks[rest]);
                    Log(plan, $"time limit reached: {chunks.Skip(c).Sum(x => x.Count)} part(s) left unplaced");
                    break;
                }
                var res = Solve(chunk, TimeShare(TimeLeft(), costs.Skip(c).ToList(), reserve: false));
                if (res == null) { plan.Unplaced.AddRange(chunk); continue; }
                foreach (var id in chunk)
                {
                    int s = res.SheetOf != null && res.SheetOf.TryGetValue(id, out var v) ? v : -1;
                    if (s < 0) plan.Unplaced.Add(id);
                    else committed.Add(new Placement { InstanceId = id, Solve = res, LocalSheet = s });
                }
            }
        }

        private bool Cancelled => IsCancelled != null && IsCancelled();

        // Give the re-pooled last sheets of this round back to a stock-tracking engine (once per sheet).
        private void ReleaseSheets(IEnumerable<Placement> repooled)
        {
            if (!(_engine is ISheetStockEngine stock)) return;
            var seen = new HashSet<long>();
            foreach (var pl in repooled)
                if (seen.Add(((long)pl.Solve.SolveId << 32) | (uint)pl.LocalSheet))
                    stock.ReleaseSheet(pl.Solve, pl.LocalSheet);
        }
        private void Log(BatchNestPlan plan, string msg) { plan.Log.Add(msg); OnProgress?.Invoke(msg); }

        public BatchNestPlan Run(IReadOnlyList<PartDescriptor> parts)
        {
            var plan = new BatchNestPlan();
            if (parts == null || parts.Count == 0) return plan;

            var watch = Stopwatch.StartNew();
            _clock = Clock ?? (() => watch.Elapsed.TotalSeconds);
            _stoppedByBudget = 0;

            // Descriptor lookup so later rounds (which only carry Ids) can rebuild full PartDescriptors —
            // area, bounds and vertex count — for shape-aware distributors and the time split.
            var descOf = new Dictionary<int, PartDescriptor>(parts.Count);
            foreach (var p in parts) descOf[p.Id] = p;
            _descOf = descOf;

            // committed[(solveId, localSheet)] -> the eventual global sheet index (assigned lazily).
            var committed = new List<Placement>();
            var remaining = parts.Select(p => p.Id).ToList();
            int round = 0;

            // The distributor in force. Starts as the chosen one; a stalled round (2nd round on, nothing
            // committed) switches it once to full batch_size sequential chunks.
            var activeDistributor = _distributor;
            bool switchedToSequential = false;

            while (remaining.Count > 0 && !Cancelled)
            {
                // Final consolidation: the leftover fits in one batch — nest it once and KEEP every sheet
                // (this is the user's "until we're done"). Any instance the engine still can't place is
                // reported as unplaced rather than looping forever.
                if (remaining.Count <= _batchSize)
                {
                    Log(plan, $"round {round}: final pass, {remaining.Count} part(s)");
                    FinalPass(remaining, committed, plan);
                    round++;
                    break;
                }

                var descriptors = remaining.Select(id => descOf.TryGetValue(id, out var d) ? d : new PartDescriptor(id, 0.0)).ToList();
                var batches = activeDistributor.Distribute(descriptors, _batchSize);
                Log(plan, $"round {round}: {remaining.Count} part(s) -> {batches.Count} batch(es) of <= {_batchSize}");

                var nextLeftover = new List<int>();
                // Provisional placements for the re-pooled last-sheet parts. Normally discarded (those
                // parts are re-solved next round), but retained so the no-consolidation exit (B) can
                // commit the round's already-computed sheets instead of throwing them away.
                var provisional = new List<Placement>();
                int committedThisRound = 0;
                var costs = batches.Select(x => SolveCost(x)).ToList();
                bool outOfTime = false;
                for (int b = 0; b < batches.Count && !Cancelled; b++)
                {
                    var batch = batches[b];
                    if (batch == null || batch.Count == 0) continue;
                    if (OutOfTime())
                    {
                        // The time limit is reached: no further solve; the rest of the round stays without a sheet.
                        outOfTime = true;
                        for (int rest = b; rest < batches.Count; rest++)
                            if (batches[rest] != null) nextLeftover.AddRange(batches[rest]);
                        break;
                    }
                    OnProgress?.Invoke($"round {round}: batch {b + 1}/{batches.Count} ({batch.Count} parts)");
                    var res = Solve(batch, TimeShare(TimeLeft(), costs.Skip(b).ToList(), reserve: true));
                    if (res == null) { nextLeftover.AddRange(batch); continue; }

                    int last = res.LastSheet;
                    foreach (var id in batch)
                    {
                        int s = res.SheetOf != null && res.SheetOf.TryGetValue(id, out var v) ? v : -1;
                        // Re-pool the not-full LAST sheet (and anything unplaced) into the next round; KEEP
                        // every earlier (full) sheet. A batch that needed only one sheet commits nothing —
                        // its parts all funnel down, which the no-consolidation exit below catches.
                        if (s < 0 || s >= last)
                        {
                            nextLeftover.Add(id);
                            if (s >= 0) provisional.Add(new Placement { InstanceId = id, Solve = res, LocalSheet = s });
                        }
                        else { committed.Add(new Placement { InstanceId = id, Solve = res, LocalSheet = s }); committedThisRound++; }
                    }
                }

                round++;

                // Out of time, or stopped (ESC / the component's hard timeout): nothing will re-nest the
                // re-pooled last sheets any more, so KEEP them as they are (each is its own sheet from its own
                // solve, still reserved in the stock) instead of leaving their parts unplaced. Parts that never
                // got a sheet (batches not run, or not placed) are unplaced.
                if (outOfTime || OutOfTime() || Cancelled)
                {
                    if (outOfTime || OutOfTime()) plan.TimeLimitReached = true;
                    committed.AddRange(provisional);
                    var kept = new HashSet<int>(provisional.Count);
                    foreach (var pl in provisional) kept.Add(pl.InstanceId);
                    var without = nextLeftover.Where(id => !kept.Contains(id)).ToList();
                    plan.Unplaced.AddRange(without);
                    Log(plan, $"round {round}: {(plan.TimeLimitReached ? "time limit reached" : "stopped")}; keeping {provisional.Count} placement(s) on this round's last sheets, {without.Count} part(s) left unplaced");
                    break;
                }

                // B) The round committed nothing: every batch produced a single sheet, so re-pooling the
                // identical leftover would just repeat the round. Escalate instead of giving up:
                if (committedThisRound == 0)
                {
                    // Strike 1 (2nd round on): switch to full batch_size sequential chunks. Balanced batches
                    // (e.g. 33/33/33) can all stay under one sheet; full ones (36/36/27) may overflow and
                    // start committing. Retry the same leftover with the new distribution.
                    if (round >= 2 && round < _maxRounds && !switchedToSequential)
                    {
                        switchedToSequential = true;
                        activeDistributor = new SequentialMergeDistributor(_distributor);
                        Log(plan, $"round {round}: nothing committed; switching to full batch_size sequential chunks");
                        ReleaseSheets(provisional);
                        remaining = nextLeftover;
                        continue;
                    }

                    // Strike 2: still nothing committed and more than one batch — nest ALL leftover as one
                    // uncapped batch (a single solve exceeding batch_size). Keep every sheet it produces.
                    // Strike 3: whatever that solve still can't place (null / sheet -1) becomes unplaced.
                    if (switchedToSequential && batches.Count > 1)
                    {
                        Log(plan, $"round {round}: still nothing committed; nesting all {nextLeftover.Count} leftover part(s) as one batch");
                        ReleaseSheets(provisional);
                        var merged = Solve(nextLeftover, TimeShare(TimeLeft(), new[] { SolveCost(nextLeftover) }, reserve: false));
                        if (merged == null) { plan.Unplaced.AddRange(nextLeftover); break; }
                        foreach (var id in nextLeftover)
                        {
                            int s = merged.SheetOf != null && merged.SheetOf.TryGetValue(id, out var mv) ? mv : -1;
                            if (s < 0) plan.Unplaced.Add(id);
                            else committed.Add(new Placement { InstanceId = id, Solve = merged, LocalSheet = s });
                        }
                        break;
                    }

                    // No escalation available (first round, or already escalated): keep this round's sheets
                    // as-is; parts that never placed get one final direct attempt.
                    Log(plan, $"round {round}: no consolidation possible; keeping {provisional.Count} placement(s) from this round");
                    committed.AddRange(provisional);
                    var placed = new HashSet<int>(provisional.Count);
                    foreach (var pl in provisional) placed.Add(pl.InstanceId);
                    var stuck = nextLeftover.Where(id => !placed.Contains(id)).ToList();
                    if (stuck.Count > 0) FinalPass(stuck, committed, plan);
                    break;
                }

                // A) The leftover now fits in a single batch (nothing left to split), or we hit the round
                // cap: consolidate the pooled partial sheets into fresh <=batchSize batches. Here the
                // pooled parts came from DIFFERENT batches, so re-nesting them together genuinely packs
                // tighter — this is the consolidation the whole loop is building toward.
                if (nextLeftover.Count <= _batchSize || round >= _maxRounds)
                {
                    Log(plan, $"round {round}: consolidating remaining {nextLeftover.Count} part(s) in a final pass");
                    ReleaseSheets(provisional);
                    FinalPass(nextLeftover, committed, plan);
                    break;
                }

                ReleaseSheets(provisional);
                remaining = nextLeftover;
            }

            // Group committed placements into output sheets (one per distinct solve + local sheet), then
            // number them FULLEST-FIRST (by total part area) so the least-filled partial/offcut sheet is
            // always the LAST sheet — the usual nesting convention. Commit order alone doesn't guarantee
            // this: with gravity placement a solve's sheet 0 isn't always its fullest, and the final pass's
            // sheets are appended after the round sheets regardless of fill. LINQ OrderByDescending is
            // stable, so equally-full sheets keep their commit order.
            var sheetParts = new Dictionary<long, List<Placement>>();
            var sheetOrder = new List<long>();
            foreach (var pl in committed)
            {
                long key = ((long)pl.Solve.SolveId << 32) | (uint)pl.LocalSheet;
                if (!sheetParts.TryGetValue(key, out var list)) { list = new List<Placement>(); sheetParts[key] = list; sheetOrder.Add(key); }
                list.Add(pl);
            }
            double SheetArea(long key)
            {
                double a = 0;
                foreach (var pl in sheetParts[key]) a += descOf.TryGetValue(pl.InstanceId, out var d) ? d.Area : 0.0;
                return a;
            }
            var orderedSheets = sheetOrder.OrderByDescending(SheetArea).ToList();
            for (int g = 0; g < orderedSheets.Count; g++)
                foreach (var pl in sheetParts[orderedSheets[g]]) pl.GlobalSheet = g;

            // Account for EVERY part: any instance neither committed nor already flagged unplaced (e.g. parts
            // left unprocessed when a timeout/ESC cancelled the run) is reported as unplaced so nothing is
            // silently dropped from the output.
            var seen = new HashSet<int>(committed.Count + plan.Unplaced.Count);
            foreach (var pl in committed) seen.Add(pl.InstanceId);
            foreach (var u in plan.Unplaced) seen.Add(u);
            foreach (var p in parts) if (!seen.Contains(p.Id)) plan.Unplaced.Add(p.Id);

            plan.Placements = committed;
            plan.TotalSheets = orderedSheets.Count;
            plan.Rounds = round;
            plan.SolvesStoppedByTimeBudget = _stoppedByBudget;
            Log(plan, $"done: {plan.TotalSheets} sheet(s), {plan.Placements.Count} placed, {plan.Unplaced.Count} unplaced, {round} round(s)"
                + (plan.TimeLimitReached ? ", time limit reached" : "")
                + (_stoppedByBudget > 0 ? $", {_stoppedByBudget} solve(s) stopped at their time share" : ""));
            return plan;
        }
    }
}
