using System.Collections.Generic;
using System.Linq;
using nest_lib.batch;
using Xunit;

namespace nest_lib_batch_tests
{
    // Limited sheet stock across batches (docs/sheet_selection.md §6): the engine reserves every sheet a
    // solve opens and the orchestrator hands back (ISheetStockEngine.ReleaseSheet) each not-full last sheet
    // whose parts it re-pools. So a limited stock is never exceeded, and every sheet the plan keeps stays
    // reserved.
    public class SheetStockTests
    {
        // Fills sheets of `perSheet` parts in order while stock lasts; parts beyond the stock are unplaced.
        private sealed class StockEngine : IBatchNestEngine, ISheetStockEngine
        {
            private readonly int _perSheet;
            public int Stock;                 // sheets left (reserved on solve, returned on release)
            public int Released;
            public readonly HashSet<long> ReleasedSheets = new HashSet<long>();

            public StockEngine(int perSheet, int stock) { _perSheet = perSheet; Stock = stock; }

            public BatchSolveResult SolveBatch(IReadOnlyList<int> instanceIds)
            {
                var sheetOf = new Dictionary<int, int>();
                int sheets = 0;
                for (int i = 0; i < instanceIds.Count; i++)
                {
                    int s = i / _perSheet;
                    if (s >= sheets)
                    {
                        if (Stock == 0) { sheetOf[instanceIds[i]] = -1; continue; }
                        Stock--;   // reserve the sheet this solve opens
                        sheets++;
                    }
                    sheetOf[instanceIds[i]] = s;
                }
                return new BatchSolveResult { SheetCount = sheets, SheetOf = sheetOf };
            }

            public void ReleaseSheet(BatchSolveResult solve, int localSheet)
            {
                Assert.True(ReleasedSheets.Add(((long)solve.SolveId << 32) | (uint)localSheet), "a sheet was released twice");
                Stock++;
                Released++;
            }
        }

        private static List<PartDescriptor> Parts(int n) =>
            Enumerable.Range(0, n).Select(i => new PartDescriptor(i, 1.0)).ToList();

        private static int KeptSheets(BatchNestPlan plan) =>
            plan.Placements.Select(p => ((long)p.Solve.SolveId << 32) | (uint)p.LocalSheet).Distinct().Count();

        [Fact]
        public void PlentyOfStock_KeptSheetsStayReserved_AndRepooledTailsAreReleased()
        {
            var engine = new StockEngine(perSheet: 10, stock: 1000);
            var plan = new BatchNestOrchestrator(engine, new SequentialDistributor(), batchSize: 25).Run(Parts(230));

            Assert.Empty(plan.Unplaced);
            Assert.True(engine.Released > 0, "batches of 25 on sheets of 10 leave partial last sheets to re-pool");
            // Every sheet still reserved is one the plan kept.
            Assert.Equal(KeptSheets(plan), 1000 - engine.Stock);
        }

        [Fact]
        public void LimitedStock_IsNeverExceeded()
        {
            var engine = new StockEngine(perSheet: 10, stock: 7);
            var plan = new BatchNestOrchestrator(engine, new SequentialDistributor(), batchSize: 25).Run(Parts(230));

            int kept = KeptSheets(plan);
            Assert.True(kept <= 7, $"kept {kept} sheets, stock is 7");
            Assert.Equal(kept, 7 - engine.Stock);
            Assert.Equal(230, plan.Placements.Count + plan.Unplaced.Count);
            Assert.True(plan.Unplaced.Count >= 230 - 70, "7 sheets of 10 hold at most 70 parts");
        }

        // An engine without ISheetStockEngine is never asked to release anything (unchanged behavior).
        private sealed class PlainEngine : IBatchNestEngine
        {
            public BatchSolveResult SolveBatch(IReadOnlyList<int> ids)
            {
                var sheetOf = new Dictionary<int, int>();
                for (int i = 0; i < ids.Count; i++) sheetOf[ids[i]] = i / 10;
                return new BatchSolveResult { SheetCount = (ids.Count + 9) / 10, SheetOf = sheetOf };
            }
        }

        [Fact]
        public void EngineWithoutStock_RunsAsBefore()
        {
            var plan = new BatchNestOrchestrator(new PlainEngine(), new SequentialDistributor(), batchSize: 25).Run(Parts(230));
            Assert.Empty(plan.Unplaced);
            Assert.Equal(230, plan.Placements.Count);
        }
    }
}
