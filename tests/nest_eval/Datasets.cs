using System;
using System.Collections.Generic;

namespace NestEval
{
    // Deterministic, self-contained part sets so tests are hermetic (no external SVG) and reproducible.
    // Each part is one closed outer ring as an interleaved [x0,y0,x1,y1,...] array in model units, sized
    // to fit comfortably inside the 510x635 test sheet with room for several sheets. Coordinates are kept
    // near the origin; the engines translate each part into place.
    public static class Datasets
    {
        // The sheet the harness nests onto (matches the standalone console demo).
        public const double SheetW = 510.0;
        public const double SheetH = 635.0;

        public sealed class Dataset
        {
            public string Name;
            public List<double[]> Parts;
        }

        public static Dataset Get(string name)
        {
            switch (name)
            {
                case "rects":   return new Dataset { Name = name, Parts = Rects() };
                case "concave": return new Dataset { Name = name, Parts = Concave() };
                case "mixed":   return new Dataset { Name = name, Parts = Mixed() };
                default: throw new ArgumentException($"unknown dataset '{name}'");
            }
        }

        // 30 axis-aligned rectangles cycling through 5 sizes.
        public static List<double[]> Rects()
        {
            var parts = new List<double[]>();
            var sizes = new (double w, double h)[] { (120, 80), (90, 90), (160, 60), (70, 140), (110, 110) };
            for (int i = 0; i < 30; i++)
            {
                var (w, h) = sizes[i % sizes.Length];
                parts.Add(Rect(w, h));
            }
            return parts;
        }

        // 24 L-shaped (concave) parts cycling through 3 sizes — exercises the concave NFP path.
        public static List<double[]> Concave()
        {
            var parts = new List<double[]>();
            var sizes = new (double w, double h, double t)[] { (120, 120, 45), (90, 150, 40), (140, 90, 50) };
            for (int i = 0; i < 24; i++)
            {
                var (w, h, t) = sizes[i % sizes.Length];
                parts.Add(LShape(w, h, t));
            }
            return parts;
        }

        // A mix of rectangles and L-shapes.
        public static List<double[]> Mixed()
        {
            var parts = new List<double[]>();
            var r = Rects();
            var c = Concave();
            int n = Math.Min(r.Count, c.Count);
            for (int i = 0; i < n; i++) { parts.Add(r[i]); parts.Add(c[i]); }
            return parts;
        }

        // Counter-clockwise rectangle at the origin.
        public static double[] Rect(double w, double h) => new double[] { 0, 0, w, 0, w, h, 0, h };

        // Counter-clockwise L-shape: full w x h footprint with the top-right rectangle removed, leaving
        // an arm of thickness t along the left and bottom edges.
        //   (0,0) -> (w,0) -> (w,t) -> (t,t) -> (t,h) -> (0,h)
        public static double[] LShape(double w, double h, double t) =>
            new double[] { 0, 0, w, 0, w, t, t, t, t, h, 0, h };
    }
}
