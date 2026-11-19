using System;
using System.Collections.Generic;

namespace nest_lib.preorient
{
    // Concave pockets of a closed outline, for "Hull: Auto" (docs/components/geometry.md). Rhino-free, like
    // FitOrientation, so it is unit-tested directly (tests/nest_lib_batch_tests).
    //
    // A POCKET is the region between one convex-hull edge and the stretch of outline that edge bridges: an L has
    // one, a comb one per gap. Hull: Auto nests a part by its convex hull unless its LARGEST pocket is at least a
    // given fraction of the area inside its outline; such a part keeps its own outline so other parts can nest
    // into the pocket. The largest pocket counts, not their sum, so a part with only small notches is still hulled.
    // Matches NestingService's PolygonProcessing.LargestConcavePocketArea (geometry.convexHull = "auto").
    public static class ConcavePockets
    {
        private const double Eps = 1e-9;

        // Area of the largest pocket of a closed ring given as interleaved x,y (a closing duplicate of the first
        // point is ignored; either winding). 0 for a convex ring or fewer than 4 points.
        public static double LargestPocketArea(double[] xy)
        {
            int n = PointCount(xy);
            if (n < 4) return 0;

            // Work counter-clockwise.
            var px = new double[n];
            var py = new double[n];
            bool ccw = SignedArea(xy, n) >= 0;
            for (int i = 0; i < n; i++)
            {
                int k = ccw ? i : n - 1 - i;
                px[i] = xy[2 * k];
                py[i] = xy[2 * k + 1];
            }

            // Convex hull over the ring's own point indices (Andrew's monotone chain, collinear points dropped),
            // so each hull corner is known by its position in the ring.
            var idx = new int[n];
            for (int i = 0; i < n; i++) idx[i] = i;
            Array.Sort(idx, (a, b) => px[a] != px[b] ? px[a].CompareTo(px[b]) : py[a].CompareTo(py[b]));
            var hull = new List<int>(n + 1);
            foreach (int p in idx)
            {
                while (hull.Count >= 2 && Cross(px, py, hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            int lower = hull.Count + 1;
            for (int i = n - 2; i >= 0; i--)
            {
                int p = idx[i];
                while (hull.Count >= lower && Cross(px, py, hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);   // the first point again
            if (hull.Count < 3) return 0;

            hull.Sort();   // ring and hull are both CCW, so ring order is hull order
            double largest = 0;
            for (int k = 0; k < hull.Count; k++)
            {
                int a = hull[k], b = hull[(k + 1) % hull.Count];
                int span = ((b - a) % n + n) % n;
                if (span < 2) continue;   // the hull edge is a ring edge
                double twice = 0;
                for (int j = 0; j < span + 1; j++)
                {
                    int p = (a + j) % n, q = (a + (j + 1) % (span + 1)) % n;
                    twice += px[p] * py[q] - px[q] * py[p];
                }
                largest = Math.Max(largest, Math.Abs(twice) * 0.5);
            }
            return largest;
        }

        // Area inside a closed ring (interleaved x,y; closing duplicate ignored).
        public static double Area(double[] xy)
        {
            int n = PointCount(xy);
            return n < 3 ? 0 : Math.Abs(SignedArea(xy, n));
        }

        // Hull: Auto's test. True when the ring has a pocket of at least `minPocket` (a fraction, 0.05 = 5%) of the
        // area inside it, so the part keeps its own outline; false means hull it.
        public static bool HasPocketWorthKeeping(double[] xy, double minPocket)
        {
            double area = Area(xy);
            if (area <= Eps) return false;
            double pocket = LargestPocketArea(xy);
            return pocket > Eps && pocket >= minPocket * area;
        }

        private static int PointCount(double[] xy)
        {
            if (xy == null) return 0;
            int n = xy.Length / 2;
            if (n > 1 && xy[0] == xy[2 * (n - 1)] && xy[1] == xy[2 * (n - 1) + 1]) n--;   // closing duplicate
            return n;
        }

        private static double SignedArea(double[] xy, int n)
        {
            double twice = 0;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                twice += xy[2 * i] * xy[2 * j + 1] - xy[2 * j] * xy[2 * i + 1];
            }
            return twice * 0.5;
        }

        private static double Cross(double[] px, double[] py, int o, int a, int b) =>
            (px[a] - px[o]) * (py[b] - py[o]) - (py[a] - py[o]) * (px[b] - px[o]);
    }
}
