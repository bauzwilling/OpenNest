using System.Linq;
using nest_lib.preorient;
using Xunit;

namespace nest_lib_batch_tests
{
    // Hull: Auto's pocket measure (nest_lib.preorient.ConcavePockets), with the same cases as NestingService's
    // PolygonProcessingTests / GeometryPreprocessorTests so both give the same answer.
    public class ConcavePocketsTests
    {
        private static double[] Xy(params double[] xy) => xy;

        private static double[] Reversed(double[] xy)
        {
            int n = xy.Length / 2;
            var r = new double[xy.Length];
            for (int i = 0; i < n; i++) { r[2 * i] = xy[2 * (n - 1 - i)]; r[2 * i + 1] = xy[2 * (n - 1 - i) + 1]; }
            return r;
        }

        // 100x100 L missing the triangle (100,40)-(40,40)-(40,100) of its hull: pocket 1800 of 6400 (28%).
        private static readonly double[] L = Xy(0, 0, 100, 0, 100, 40, 40, 40, 40, 100, 0, 100);

        // 100x100 square with a 10x10 notch in its bottom edge: pocket 100 of 9900 (1%).
        private static readonly double[] Notched = Xy(0, 0, 40, 0, 40, 10, 50, 10, 50, 0, 100, 0, 100, 100, 0, 100);

        [Fact]
        public void AConvexRing_HasNoPocket()
        {
            Assert.Equal(0, ConcavePockets.LargestPocketArea(Xy(0, 0, 50, 0, 100, 0, 100, 100, 0, 100)), 9);   // collinear point
            Assert.Equal(0, ConcavePockets.LargestPocketArea(Xy(0, 0, 1, 0, 0, 1)), 9);
        }

        [Fact]
        public void AnL_HasItsMissingCornerAsPocket_InEitherWinding_WithOrWithoutAClosingPoint()
        {
            Assert.Equal(1800, ConcavePockets.LargestPocketArea(L), 6);
            Assert.Equal(1800, ConcavePockets.LargestPocketArea(Reversed(L)), 6);
            Assert.Equal(1800, ConcavePockets.LargestPocketArea(L.Concat(new double[] { 0, 0 }).ToArray()), 6);
            Assert.Equal(6400, ConcavePockets.Area(L), 6);
        }

        [Fact]
        public void TheLargestPocketCounts_NotTheirSum()
        {
            // 100x100 with a 10x10 notch in the bottom edge and a 30x20 notch in the top edge.
            var ring = Xy(0, 0, 20, 0, 20, 10, 30, 10, 30, 0, 100, 0, 100, 100, 70, 100, 70, 80, 40, 80, 40, 100, 0, 100);
            Assert.Equal(600, ConcavePockets.LargestPocketArea(ring), 6);
        }

        [Fact]
        public void AtTheFivePercentDefault_TheLKeepsItsOutline_AndTheNotchedSquareIsHulled()
        {
            Assert.True(ConcavePockets.HasPocketWorthKeeping(L, 0.05));
            Assert.False(ConcavePockets.HasPocketWorthKeeping(Notched, 0.05));
            // The threshold decides: at 30% the L is hulled, at 1% the notch is kept.
            Assert.False(ConcavePockets.HasPocketWorthKeeping(L, 0.30));
            Assert.True(ConcavePockets.HasPocketWorthKeeping(Notched, 0.01));
            // A convex ring is always hulled (its hull is itself), even at 0%.
            Assert.False(ConcavePockets.HasPocketWorthKeeping(Xy(0, 0, 10, 0, 10, 10, 0, 10), 0));
        }
    }
}
