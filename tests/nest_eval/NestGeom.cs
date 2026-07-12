using System;
using System.Collections.Generic;

namespace NestEval
{
    // Flatten a list of plain polygon rings into the int[]/double[] arrays both C ABIs expect, plus
    // apply the shared placement transform to resolve a part into world/sheet-local XY. This mirrors
    // opennest_console's NestFlatten (outer rings only, quantity 1) but as public types so the eval
    // harness owns its own geometry surface with no coupling to the console Exe.
    public static class NestGeom
    {
        // A placement returned by either engine, already resolved to XY of the original ring.
        public sealed class Placement
        {
            public int PartIndex;    // index into the original parts list
            public int SheetId;      // engine sheet/bin id, -1 = unplaced
            public double[] Xy;      // placed, closed ring (interleaved [x0,y0,...])
        }

        // Concatenate rings (each = interleaved [x0,y0,...]) into per-ring vertex counts + flat XY.
        public static void Concat(IReadOnlyList<double[]> rings, out int[] vertexCounts, out double[] xy)
        {
            vertexCounts = new int[rings.Count];
            int total = 0;
            for (int i = 0; i < rings.Count; i++)
            {
                vertexCounts[i] = rings[i].Length / 2;
                total += rings[i].Length;
            }
            xy = new double[total];
            int cur = 0;
            foreach (var r in rings)
            {
                Array.Copy(r, 0, xy, cur, r.Length);
                cur += r.Length;
            }
        }

        // final = Rotate(point, angle, about (0,0)) + (tx, ty). Angle in RADIANS (callers convert).
        public static double[] PlaceRing(double[] ring, double angleRad, double tx, double ty)
        {
            double c = Math.Cos(angleRad), s = Math.Sin(angleRad);
            var outXy = new double[ring.Length];
            for (int k = 0; k < ring.Length / 2; k++)
            {
                double x = ring[2 * k], y = ring[2 * k + 1];
                outXy[2 * k] = x * c - y * s + tx;
                outXy[2 * k + 1] = x * s + y * c + ty;
            }
            return outXy;
        }
    }
}
