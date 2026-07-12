using System;
using System.Collections.Generic;
using System.Diagnostics;
using NestPhysics;
using NfpNest;

namespace NestEval
{
    // Drives each engine on a list of plain polygon rings and returns the resolved placements
    // (one per instance, in SHEET-LOCAL coordinates: each part's ring already lives in its own
    // sheet's [0,W]x[0,H] frame, so parts sharing a SheetId can be compared directly). This mirrors
    // opennest_console's RunNfp / RunCollision but returns data instead of writing an SVG.
    public static class NestRunner
    {
        public const int MaxSheets = 6;

        public sealed class EngineRun
        {
            public string Engine;
            public List<NestGeom.Placement> Placements;
            public int SheetsReported;   // n_sheets the engine reported opening
            public long WallMs;
            public double Fitness;        // nfp only (0 for collision)
            public bool DllMissing;
        }

        // ---- OpenNest2 (nfp_nest.dll): Boost.Polygon NFP + genetic algorithm ----
        public static EngineRun RunNfp(IReadOnlyList<double[]> parts, int generations, int seed)
        {
            int n = parts.Count;
            NestGeom.Concat(parts, out int[] pvc, out double[] pxy);
            var pqty = new int[n];
            for (int i = 0; i < n; i++) pqty[i] = 1;
            var phc = new int[n];
            var phvc = new int[1];
            var phxy = new double[1];

            // Several identical sheets stacked at the origin so the GA can spill across bins.
            var sheetList = new List<double[]>();
            for (int s = 0; s < MaxSheets; s++) sheetList.Add(Rect(Datasets.SheetW, Datasets.SheetH));
            NestGeom.Concat(sheetList, out int[] svc, out double[] sxy);
            int sheetCount = sheetList.Count;
            var shc = new int[sheetCount];
            var shvc = new int[1];
            var shxy = new double[1];

            var pr = new NfpParams
            {
                placementType = 1, rotations = 4, mutationRate = 10, populationSize = 10,
                seed = seed, curveTolerance = 0.72, clipperScale = 1e7, spacing = 2.0,
                sheetSpacing = 0, rotationLimit = 360, useHoles = 0, exploreConcave = 0,
                clipByHull = 0, clipByRects = 1, simplify = 0, mode = 1, generations = generations,
                numSeeds = 0, useParallel = 1, timeBudgetSecs = 0, maxSheets = 0, edgeSamples = -1,
                compactionPasses = 0, tryAllRotations = 0, exactNfp = 0
            };

            int instanceCount = n;
            var tx = new double[instanceCount];
            var ty = new double[instanceCount];
            var ang = new double[instanceCount];
            var sid = new int[instanceCount];
            var pidx = new int[instanceCount];

            var run = new EngineRun { Engine = "nfp", Placements = new List<NestGeom.Placement>() };
            try
            {
                NfpNestWrapper.nfp_cancel_reset();
                var sw = Stopwatch.StartNew();
                int placedCount = NfpNestWrapper.nfp_nest(
                    n, pvc, pxy, pqty, null, phc, phvc, phxy,
                    sheetCount, svc, sxy, shc, shvc, shxy,
                    ref pr, tx, ty, ang, sid, pidx, out int nSheets, out double fitness);
                sw.Stop();
                if (placedCount < 0) throw new InvalidOperationException($"nfp_nest error code {placedCount}");

                run.WallMs = sw.ElapsedMilliseconds;
                run.SheetsReported = nSheets;
                run.Fitness = fitness;
                for (int k = 0; k < instanceCount; k++)
                {
                    int src = pidx[k] >= 0 && pidx[k] < n ? pidx[k] : 0;
                    double angRad = ang[k] * Math.PI / 180.0;   // nfp angle is DEGREES
                    run.Placements.Add(new NestGeom.Placement
                    {
                        PartIndex = src,
                        SheetId = sid[k],
                        Xy = NestGeom.PlaceRing(parts[src], angRad, tx[k], ty[k])
                    });
                }
            }
            catch (DllNotFoundException) { run.DllMissing = true; }
            return run;
        }

        // ---- OpenNestCollision (nest_physics.dll): penetration-depth overlap relaxation ----
        public static EngineRun RunCollision(IReadOnlyList<double[]> parts, long iterBudget, int seed)
        {
            int n = parts.Count;
            NestGeom.Concat(parts, out int[] pvc, out double[] pxy);

            var sheetRing = Rect(Datasets.SheetW, Datasets.SheetH);
            NestGeom.Concat(new List<double[]> { sheetRing }, out int[] sovc, out double[] soxy);
            int sheetCount = 1;
            var shc = new int[sheetCount];
            var hvc = new int[1];
            var hxy = new double[1];
            var phc = new int[n];
            var phvc = new int[1];
            var phxy = new double[1];

            var np = new NpParams
            {
                num_rotations = 4, spacing = 0.0, simplify_tolerance = 0.0, seed = seed,
                time_budget_secs = 0, iter_budget = iterBudget, iter_mode = 1, max_sheets = MaxSheets,
                n_starts = 1, part_holes_mode = 0, pole_max = 0, final_compact = 1, fit_mode = 0
            };

            var tx = new double[n];
            var ty = new double[n];
            var ang = new double[n];
            var sid = new int[n];

            var run = new EngineRun { Engine = "collision", Placements = new List<NestGeom.Placement>() };
            try
            {
                NestPhysicsWrapper.np_cancel_reset();
                var sw = Stopwatch.StartNew();
                int rc = NestPhysicsWrapper.np_nest(
                    n, pvc, pxy, null, sheetCount, sovc, soxy, shc, hvc, hxy,
                    phc, phvc, phxy, ref np, tx, ty, ang, sid, out int nSheets);
                sw.Stop();
                if (rc != 0) throw new InvalidOperationException($"np_nest error code {rc}");

                run.WallMs = sw.ElapsedMilliseconds;
                run.SheetsReported = nSheets;
                for (int i = 0; i < n; i++)
                {
                    run.Placements.Add(new NestGeom.Placement
                    {
                        PartIndex = i,
                        SheetId = sid[i],
                        Xy = NestGeom.PlaceRing(parts[i], ang[i], tx[i], ty[i])   // np angle is RADIANS
                    });
                }
            }
            catch (DllNotFoundException) { run.DllMissing = true; }
            return run;
        }

        private static double[] Rect(double w, double h) => new double[] { 0, 0, w, 0, w, h, 0, h };
    }
}
