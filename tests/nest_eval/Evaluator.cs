using System;
using System.Collections.Generic;
using ClipperLib;

namespace NestEval
{
    // Quality + correctness metrics for one nest result. Areas are computed exactly with the bundled
    // Clipper (integer arithmetic), so overlap and out-of-bounds are trustworthy correctness signals.
    public sealed class NestMetrics
    {
        public string Engine;
        public string Dataset;
        public int    Seed;
        public int    Placed;
        public int    Total;
        public int    Sheets;            // distinct sheets actually used by placed parts
        public double PartArea;          // total |area| of placed parts
        public double UsedSheetArea;     // Sheets * SheetW * SheetH
        public double Utilization;       // PartArea / UsedSheetArea  (0..1)
        public double OverlapArea;       // pairwise intersection area of placed parts on the same sheet
        public double OutOfBoundsArea;   // placed-part area lying outside its sheet rectangle
        public long   WallMs;
        public double Fitness;

        public bool AllPlaced => Placed == Total;
    }

    public static class Evaluator
    {
        // Fixed-point scale for Clipper (coords ~1e3 -> ~1e7 integers, areas well within Int64/Int128).
        private const double S = 1e4;

        public static NestMetrics Evaluate(string dataset, int seed, NestRunner.EngineRun run,
                                            double sheetW, double sheetH)
        {
            var m = new NestMetrics
            {
                Engine = run.Engine,
                Dataset = dataset,
                Seed = seed,
                Total = run.Placements.Count,
                WallMs = run.WallMs,
                Fitness = run.Fitness,
            };

            // Group placed parts by sheet.
            var bySheet = new Dictionary<int, List<double[]>>();
            foreach (var p in run.Placements)
            {
                if (p.SheetId < 0) continue;        // unplaced
                m.Placed++;
                m.PartArea += Math.Abs(SignedArea(p.Xy));
                if (!bySheet.TryGetValue(p.SheetId, out var list)) { list = new List<double[]>(); bySheet[p.SheetId] = list; }
                list.Add(p.Xy);
            }

            m.Sheets = bySheet.Count;
            m.UsedSheetArea = m.Sheets * sheetW * sheetH;
            m.Utilization = m.UsedSheetArea > 0 ? m.PartArea / m.UsedSheetArea : 0.0;

            var sheetRect = ToPath(new double[] { 0, 0, sheetW, 0, sheetW, sheetH, 0, sheetH });

            foreach (var kv in bySheet)
            {
                var parts = kv.Value;
                var paths = new List<List<IntPoint>>(parts.Count);
                foreach (var ring in parts) paths.Add(ToPath(ring));

                // Pairwise overlap on this sheet.
                for (int i = 0; i < paths.Count; i++)
                    for (int j = i + 1; j < paths.Count; j++)
                        m.OverlapArea += IntersectionArea(paths[i], paths[j]);

                // Out-of-bounds: part area minus the part's area that lies inside the sheet rect.
                foreach (var pth in paths)
                {
                    double partArea = Math.Abs(AreaOf(pth));
                    double inside = IntersectionArea(pth, sheetRect);
                    double outside = partArea - inside;
                    if (outside > 0) m.OutOfBoundsArea += outside;
                }
            }

            return m;
        }

        // Intersection area of two closed polygons, in model units^2.
        private static double IntersectionArea(List<IntPoint> a, List<IntPoint> b)
        {
            var c = new Clipper();
            c.AddPath(a, PolyType.ptSubject, true);
            c.AddPath(b, PolyType.ptClip, true);
            var sol = new List<List<IntPoint>>();
            c.Execute(ClipType.ctIntersection, sol, PolyFillType.pftNonZero, PolyFillType.pftNonZero);
            double area = 0;
            foreach (var p in sol) area += Math.Abs(Clipper.Area(p));
            return area / (S * S);
        }

        private static double AreaOf(List<IntPoint> p) => Clipper.Area(p) / (S * S);

        private static List<IntPoint> ToPath(double[] ring)
        {
            int nv = ring.Length / 2;
            var path = new List<IntPoint>(nv);
            for (int k = 0; k < nv; k++)
                path.Add(new IntPoint((long)Math.Round(ring[2 * k] * S), (long)Math.Round(ring[2 * k + 1] * S)));
            return path;
        }

        // Shoelace signed area in model units^2 (double-precision; for the utilization readout).
        private static double SignedArea(double[] ring)
        {
            double a = 0;
            int nv = ring.Length / 2;
            for (int i = 0; i < nv; i++)
            {
                int j = (i + 1) % nv;
                a += ring[2 * i] * ring[2 * j + 1] - ring[2 * j] * ring[2 * i + 1];
            }
            return a * 0.5;
        }
    }
}
