using System;
using System.Collections.Generic;
using System.Linq;
using nest_lib.batch;
using Xunit;

namespace nest_lib_batch_tests
{
    // A time limit for the whole batch run (docs/batch_nesting.md, "Timeout"): each solve gets a share of the
    // time left in proportion to its parts' vertex count (one average batch held back inside a round), the final
    // pass gets all of it, and once the time is up no further solve runs: the round's not-full last sheets are
    // kept as they are and parts without a sheet are unplaced. The clock here is fake, so every share is exact.
    public class TimeLimitTests
    {
        // Fills sheets of `perSheet` parts in order. Each solve takes `work` seconds of the fake clock, or its
        // budget when the budget is shorter and `honoursBudget` is set (then it reports StoppedByTimeBudget).
        private sealed class TimedEngine : IBatchNestEngine, ITimeBudgetEngine
        {
            private readonly int _perSheet;
            private readonly double _work;
            private readonly bool _honoursBudget;
            private double _budget;
            public double Now;
            public readonly List<double> Budgets = new List<double>();
            public readonly List<int> SolveSizes = new List<int>();

            public TimedEngine(int perSheet, double work, bool honoursBudget = true)
            {
                _perSheet = perSheet; _work = work; _honoursBudget = honoursBudget;
            }

            public void SetTimeBudget(double seconds) => _budget = seconds;

            public BatchSolveResult SolveBatch(IReadOnlyList<int> ids)
            {
                Budgets.Add(_budget);
                SolveSizes.Add(ids.Count);
                bool stopped = _honoursBudget && _budget > 0 && _work > _budget;
                Now += stopped ? _budget : _work;
                var sheetOf = new Dictionary<int, int>();
                for (int i = 0; i < ids.Count; i++) sheetOf[ids[i]] = i / _perSheet;
                return new BatchSolveResult
                {
                    SheetCount = (ids.Count + _perSheet - 1) / _perSheet, SheetOf = sheetOf, StoppedByTimeBudget = stopped,
                };
            }
        }

        private static List<PartDescriptor> Parts(int n, Func<int, int> vertices) =>
            Enumerable.Range(0, n).Select(i => new PartDescriptor(i, 1.0, 0, 0, vertices(i))).ToList();

        private static BatchNestOrchestrator Orchestrator(TimedEngine engine, double limit, int batchSize = 25, int maxRounds = 12) =>
            new BatchNestOrchestrator(engine, new SequentialDistributor(), batchSize, maxRounds)
            {
                TimeLimitSeconds = limit,
                Clock = () => engine.Now,
            };

        private static int KeptSheets(BatchNestPlan plan) =>
            plan.Placements.Select(p => ((long)p.Solve.SolveId << 32) | (uint)p.LocalSheet).Distinct().Count();

        [Fact]
        public void TimeShare_IsInProportionToCost_WithOneAverageSolveHeldBack()
        {
            // 779-part job shape: a light, a heavy and a light batch.
            Assert.Equal(100.0 * 300 / (1450 + 1450 / 3.0), BatchNestOrchestrator.TimeShare(100, new double[] { 300, 850, 300 }, true), 9);
            // Equal costs: left / (n + 1) inside a round, left / n without the reserve.
            Assert.Equal(25.0, BatchNestOrchestrator.TimeShare(100, new double[] { 7, 7, 7 }, true), 9);
            Assert.Equal(50.0, BatchNestOrchestrator.TimeShare(100, new double[] { 7, 7 }, false), 9);
            // The last solve without the reserve gets everything left.
            Assert.Equal(42.0, BatchNestOrchestrator.TimeShare(42, new double[] { 5 }, false), 9);
            // Never below the minimum share.
            Assert.Equal(BatchNestOrchestrator.MinShareSeconds, BatchNestOrchestrator.TimeShare(0.2, new double[] { 1, 1 }, true), 9);
        }

        [Fact]
        public void SolveCost_CountsVertices_AndAPartWithoutThemAsOne()
        {
            var parts = new[] { new PartDescriptor(0, 1, 0, 0, 12), new PartDescriptor(1, 1, 0, 0, 30), new PartDescriptor(2, 1.0) };
            Assert.Equal(43.0, BatchNestOrchestrator.SolveCost(parts), 9);
        }

        [Fact]
        public void Batches_GetTheirShareByVertexCount_AndTheFinalPassGetsTheRest()
        {
            // 75 parts in 3 batches of 25: 10, 40 and 10 vertices per part. Sheets of 10 -> each batch keeps 2
            // sheets and re-pools 5 parts; the 15 left go to the final pass. The solves take no time.
            var engine = new TimedEngine(perSheet: 10, work: 0);
            var plan = Orchestrator(engine, limit: 100).Run(Parts(75, i => i >= 25 && i < 50 ? 40 : 10));

            Assert.Equal(new[] { 25, 25, 25, 15 }, engine.SolveSizes);
            Assert.Equal(100.0 * 250 / (1500 + 500), engine.Budgets[0], 9);    // 12.5
            Assert.Equal(100.0 * 1000 / (1250 + 625), engine.Budgets[1], 9);   // 53.3: the heavy batch
            Assert.Equal(100.0 * 250 / (250 + 250), engine.Budgets[2], 9);     // 50
            Assert.Equal(100.0, engine.Budgets[3], 9);                         // final pass: all that is left
            Assert.Empty(plan.Unplaced);
            Assert.False(plan.TimeLimitReached);
            Assert.Equal(0, plan.SolvesStoppedByTimeBudget);
        }

        [Fact]
        public void SolvesThatReachTheirShare_AreCounted()
        {
            // Every solve would take 30 s; each stops at its share and the run ends inside the limit.
            var engine = new TimedEngine(perSheet: 10, work: 30);
            var plan = Orchestrator(engine, limit: 60).Run(Parts(75, _ => 10));

            Assert.Equal(4, plan.SolvesStoppedByTimeBudget);
            Assert.True(engine.Now <= 60 + 1e-9, $"the run took {engine.Now} s of 60");
            Assert.Empty(plan.Unplaced);
        }

        [Fact]
        public void WithoutATimeLimit_NoBudgetIsSet_AndTheRunIsUnchanged()
        {
            var engine = new TimedEngine(perSheet: 10, work: 5);
            var plan = Orchestrator(engine, limit: 0).Run(Parts(230, _ => 10));

            Assert.All(engine.Budgets, b => Assert.Equal(0.0, b));
            Assert.Empty(plan.Unplaced);
            Assert.Equal(230, plan.Placements.Count);
            Assert.False(plan.TimeLimitReached);
        }

        [Fact]
        public void OutOfTimeInARound_KeepsTheLastSheets_AndLeavesBatchesNotRunUnplaced()
        {
            // An engine that ignores its budget: 3 s per solve against a 5 s limit. Batches 1 and 2 run (clock 6),
            // batches 3 and 4 never do. The not-full last sheets of batches 1 and 2 (5 parts each) are kept.
            var engine = new TimedEngine(perSheet: 10, work: 3, honoursBudget: false);
            var plan = Orchestrator(engine, limit: 5).Run(Parts(100, _ => 10));

            Assert.Equal(2, engine.SolveSizes.Count);
            Assert.True(plan.TimeLimitReached);
            Assert.Equal(50, plan.Placements.Count);          // 2 x (two full sheets + the last sheet of 5)
            Assert.Equal(6, KeptSheets(plan));
            Assert.Equal(50, plan.Unplaced.Count);
            Assert.Equal(Enumerable.Range(50, 50), plan.Unplaced.OrderBy(i => i));
        }

        [Fact]
        public void OutOfTimeInTheFinalPass_LeavesTheChunksNotRunUnplaced()
        {
            // Sheets of 13 -> each batch of 25 keeps one sheet and re-pools 12; with one round the 48 left are
            // nested in a final pass of two chunks (25, 23). The round takes 12 s of 13.5; the first chunk takes
            // 3 s more, so the second is never run.
            var engine = new TimedEngine(perSheet: 13, work: 3, honoursBudget: false);
            var plan = Orchestrator(engine, limit: 13.5, maxRounds: 1).Run(Parts(100, _ => 10));

            Assert.Equal(new[] { 25, 25, 25, 25, 25 }, engine.SolveSizes);
            Assert.True(plan.TimeLimitReached);
            Assert.Equal(23, plan.Unplaced.Count);
            Assert.Equal(100, plan.Placements.Count + plan.Unplaced.Count);
        }

        [Fact]
        public void AStoppedRun_KeepsTheLastSheetsItComputed()
        {
            var engine = new TimedEngine(perSheet: 10, work: 1);
            var orch = Orchestrator(engine, limit: 0);
            orch.IsCancelled = () => engine.SolveSizes.Count >= 1;   // ESC during the first batch
            var plan = orch.Run(Parts(100, _ => 10));

            Assert.Single(engine.SolveSizes);
            Assert.False(plan.TimeLimitReached);
            Assert.Equal(25, plan.Placements.Count);          // its two full sheets and its last sheet of 5
            Assert.Equal(75, plan.Unplaced.Count);
        }
    }
}
