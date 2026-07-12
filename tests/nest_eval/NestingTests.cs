using System;
using Xunit;
using Xunit.Abstractions;

// Native engines run their own thread pools; keep test execution serial so two solves never contend.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace NestEval
{
    // One test per (engine, dataset). Each test:
    //   1. runs the engine on the dataset,
    //   2. GATES correctness (no overlap, in-bounds, all parts placed) with asserts, and
    //   3. records the quality metrics (utilization, sheets, wall) to eval-results/.
    // A pass means "correct"; the utilization column in eval-results/summary.md tells you whether
    // a change made packing better or worse.
    [Collection("nest-eval")]
    public sealed class NestingTests
    {
        private const int Seed = 100;
        private readonly EvalReport _report;
        private readonly ITestOutputHelper _out;

        public NestingTests(EvalReport report, ITestOutputHelper output)
        {
            _report = report;
            _out = output;
        }

        [Theory]
        [InlineData("rects")]
        [InlineData("concave")]
        [InlineData("mixed")]
        public void Nfp(string dataset)
        {
            var ds = Datasets.Get(dataset);
            // ~10 generations keeps a single test well under a couple of seconds while giving the GA
            // enough room to place everything on the available sheets.
            var run = NestRunner.RunNfp(ds.Parts, generations: 10, seed: Seed);
            Gate("nfp", dataset, run);
        }

        [Theory]
        [InlineData("rects")]
        [InlineData("concave")]
        [InlineData("mixed")]
        public void Collision(string dataset)
        {
            var ds = Datasets.Get(dataset);
            var run = NestRunner.RunCollision(ds.Parts, iterBudget: 400, seed: Seed);
            Gate("collision", dataset, run);
        }

        private void Gate(string engine, string dataset, NestRunner.EngineRun run)
        {
            Assert.False(run.DllMissing,
                $"{engine} native DLL not found next to the test assembly (build/copy nfp_nest.dll / nest_physics.dll)");

            var m = Evaluator.Evaluate(dataset, Seed, run, Datasets.SheetW, Datasets.SheetH);
            _report.Add(m);
            _out.WriteLine($"{engine}/{dataset}: placed {m.Placed}/{m.Total}, sheets {m.Sheets}, " +
                           $"util {m.Utilization:P1}, overlap {m.OverlapArea:0.###}, oob {m.OutOfBoundsArea:0.###}, " +
                           $"wall {m.WallMs}ms");

            // --- correctness gates ---
            // Overlap and out-of-bounds must be ~0. Tolerance = 0.1% of total part area absorbs
            // integer-scale rounding while still catching a real (part-sized) overlap.
            double tol = Math.Max(1.0, 0.001 * m.PartArea);
            Assert.True(m.OverlapArea <= tol,
                $"{engine}/{dataset}: parts overlap by {m.OverlapArea:0.###} (tol {tol:0.###})");
            Assert.True(m.OutOfBoundsArea <= tol,
                $"{engine}/{dataset}: {m.OutOfBoundsArea:0.###} of part area lies outside the sheet (tol {tol:0.###})");
            Assert.True(m.AllPlaced,
                $"{engine}/{dataset}: only {m.Placed}/{m.Total} parts placed on {NestRunner.MaxSheets} sheets");
        }
    }
}
