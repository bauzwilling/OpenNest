using System.Collections.Generic;
using Xunit;

namespace NestEval
{
    // Guards the guard: proves the Evaluator actually detects overlap / out-of-bounds, so a passing
    // engine test means "no overlap" rather than "the detector is broken". Pure geometry, no engine.
    public sealed class EvaluatorTests
    {
        private static NestRunner.EngineRun Run(params NestGeom.Placement[] placements) =>
            new NestRunner.EngineRun { Engine = "synthetic", Placements = new List<NestGeom.Placement>(placements) };

        [Fact]
        public void DetectsOverlap()
        {
            // Two 100x100 squares on sheet 0 overlapping in a 50x50 corner => overlap area 2500.
            var a = new NestGeom.Placement { PartIndex = 0, SheetId = 0, Xy = new double[] { 0, 0, 100, 0, 100, 100, 0, 100 } };
            var b = new NestGeom.Placement { PartIndex = 1, SheetId = 0, Xy = new double[] { 50, 50, 150, 50, 150, 150, 50, 150 } };
            var m = Evaluator.Evaluate("t", 0, Run(a, b), 500, 500);
            Assert.Equal(2500.0, m.OverlapArea, 1);   // within 0.1 units^2
        }

        [Fact]
        public void DetectsOutOfBounds()
        {
            // A 100x100 square straddling the right edge of a 500x500 sheet: x in [460,560], so the
            // strip outside x=500 is 60 wide x 100 tall => oob 6000.
            var a = new NestGeom.Placement { PartIndex = 0, SheetId = 0, Xy = new double[] { 460, 100, 560, 100, 560, 200, 460, 200 } };
            var m = Evaluator.Evaluate("t", 0, Run(a), 500, 500);
            Assert.Equal(6000.0, m.OutOfBoundsArea, 1);
        }

        [Fact]
        public void CleanLayoutIsZero()
        {
            // Two squares on the same sheet, not touching, both inside => overlap 0, oob 0.
            var a = new NestGeom.Placement { PartIndex = 0, SheetId = 0, Xy = new double[] { 0, 0, 100, 0, 100, 100, 0, 100 } };
            var b = new NestGeom.Placement { PartIndex = 1, SheetId = 0, Xy = new double[] { 200, 0, 300, 0, 300, 100, 200, 100 } };
            var m = Evaluator.Evaluate("t", 0, Run(a, b), 500, 500);
            Assert.Equal(0.0, m.OverlapArea, 1);
            Assert.Equal(0.0, m.OutOfBoundsArea, 1);
            Assert.Equal(2, m.Placed);
        }
    }
}
