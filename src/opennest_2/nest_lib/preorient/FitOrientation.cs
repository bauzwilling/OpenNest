using System;
using System.Collections.Generic;

namespace nest_lib.preorient
{
    /// <summary>What the pre-orientation pass decided for one part.</summary>
    public enum FitResult
    {
        /// <summary>The part's bounding box already fits a sheet as drawn — angle 0, nothing to do.</summary>
        AlreadyFits = 0,
        /// <summary>
        /// The part fits at one of the solver's OWN rotation steps (360/Rotations), and the returned angle
        /// is that step. Preferred over a custom angle: the step set the engine explores is unchanged, so
        /// the solve still only ever sees orientations it would have tried anyway.
        /// </summary>
        SnappedToStep = 1,
        /// <summary>
        /// No rotation step fits, so the returned angle is a CUSTOM one found by the continuous search.
        /// This shifts the engine's whole rotation set by that angle.
        /// </summary>
        Reoriented = 2,
        /// <summary>No orientation fits any sheet — angle 0, the part is left exactly as drawn.</summary>
        NoFit = 3,
    }

    /// <summary>
    /// The geometry core of the pre-orientation pass: given a part outline and the sheet dimensions, find
    /// the orientation about Z with the MOST ROOM TO SPARE inside some sheet's bounding box.
    /// Deliberately Rhino-free (plain interleaved x,y arrays) so it is unit-testable on its own; the Rhino
    /// adapter that feeds it nest_geo / nest_sheets is nest_rhino_lib.part_preorient.
    ///
    /// ROOMIEST, not smallest-rotation. Smallest-rotation sounds friendlier - it keeps the drawing
    /// recognisable - but it is the wrong objective and it broke the feature in practice. The smallest
    /// fitting rotation is BY CONSTRUCTION the angle at which the part first stops overflowing, i.e. it
    /// touches the sheet limit with ~zero clearance. The nester then cannot place it (kerf, spacing, the
    /// sheet's own edge setback), the part comes back unplaced, and rhino_example hands an unplaced part
    /// Transform.Identity - so the rotation is discarded and the user sees NO change at all. Maximising
    /// clearance instead puts the part in the middle of its feasible band, where it actually nests. Ties
    /// (equal clearance) still go to the smaller rotation.
    ///
    /// Feasibility is decided ANALYTICALLY, not by a stepped sweep. A sweep silently misses a fit that
    /// only exists in a sliver between two samples, and no step is small enough to rule that out:
    ///
    ///   * Work on the convex hull — its bounding box is the outline's bounding box at every angle, and it
    ///     is a handful of points instead of thousands.
    ///   * Rotating by t, the box is w(t) = hull width along (cos t, -sin t) and h(t) = width along
    ///     (sin t, cos t). Each is the projection span of one ANTIPODAL VERTEX PAIR, and that pair only
    ///     changes at the angles where the rotated axis crosses a hull-edge normal. Between two such
    ///     breakpoints both w and h are single sinusoids R*cos(t - psi).
    ///   * The feasible set {w(t) <= W and h(t) <= H, for some sheet} is therefore a union of arcs whose
    ///     endpoints all solve R*cos(t - psi) = W (or = H) inside one cell - closed form. Those endpoints
    ///     plus the cell boundaries are the CRITICAL ANGLES, and feasibility is constant between two
    ///     consecutive ones. So no feasible arc, however thin, can be missed.
    ///
    /// Only the choice of angle WITHIN a feasible arc is sampled (ArcSamples points, then a ternary
    /// refinement). Sampling there can at worst pick a slightly less roomy angle in the same arc - it can
    /// never miss a fit or return an angle that does not fit, both of which are checked exactly.
    ///
    /// THE SOLVER'S OWN ROTATION STEPS COME FIRST. All of the above is the FALLBACK. Given a step count
    /// (the "Rotations" option, n => steps of 360/n), the pass first asks whether the part fits at any of
    /// those steps and, if so, snaps to the roomiest one. That matters because the engine explores
    /// orientations as base + k*360/n: snapping to a step leaves that set unchanged, so the nest is the
    /// one the user's Rotations setting already described. A custom angle rotates the whole set to
    /// somewhere the user never asked for, which is worth doing to rescue a part that would otherwise go
    /// unplaced, but not worth doing when a plain 90 degrees would have served.
    /// </summary>
    public static class FitOrientation
    {
        // AABB fit slack, in model units. Absolute and tiny: it absorbs the rounding of the rotated
        // extents (and of the candidate angles, which sit exactly ON the feasible boundary), it never
        // buys a part real room it does not have.
        public const double FitTolerance = 1e-6;

        // Samples per feasible arc when choosing the roomiest angle inside it (then ternary-refined).
        // Only affects WHICH angle in an arc is picked, never whether the arc is found.
        public const int ArcSamples = 64;

        // Two clearances within this (model units) count as equal, and the smaller rotation wins.
        private const double ClearanceTie = 1e-9;

        /// <summary>
        /// Decide the initial orientation for one part.
        /// </summary>
        /// <param name="xy">Outline vertices, interleaved x,y (world position is irrelevant — extents are
        /// translation-invariant). A closing duplicate vertex is harmless.</param>
        /// <param name="sheetWh">Sheet bounding-box sizes, interleaved width,height. The part need only fit
        /// ONE of them.</param>
        /// <param name="angle">Rotation to apply, in RADIANS CCW about Z. 0 unless the result is
        /// <see cref="FitResult.SnappedToStep"/> or <see cref="FitResult.Reoriented"/>.</param>
        public static FitResult Solve(double[] xy, double[] sheetWh, out double angle)
        {
            return Solve(xy, sheetWh, out angle, 0);
        }

        /// <summary>
        /// Decide the initial orientation for one part, preferring the solver's own rotation steps.
        /// </summary>
        /// <param name="rotationSteps">The "Rotations" setting: the part may be tried at k*360/n degrees.
        /// A fitting step always wins over a custom angle. 0 or 1 = no step set, go straight to the
        /// continuous search.</param>
        public static FitResult Solve(double[] xy, double[] sheetWh, out double angle, int rotationSteps)
        {
            angle = 0.0;
            if (xy == null || xy.Length < 6) return FitResult.AlreadyFits;           // degenerate: not our call
            if (sheetWh == null || sheetWh.Length < 2) return FitResult.AlreadyFits; // no sheets to fit into

            double[] hull = Hull(xy);
            if (Fits(hull, 0.0, sheetWh)) return FitResult.AlreadyFits;

            // 1) The engine's OWN rotation steps, roomiest first. Preferred over any custom angle: it
            //    leaves the set of orientations the solve explores exactly as the Rotations setting
            //    describes it. Ascending k, replaced only on strictly more clearance, so the smallest
            //    fitting step wins a tie (90 over 270, which have identical bounding boxes).
            if (rotationSteps > 1)
            {
                bool gotStep = false;
                double bestStep = 0.0, bestStepClear = double.NegativeInfinity;
                for (int k = 1; k < rotationSteps; k++)
                {
                    double t = k * 2.0 * Math.PI / rotationSteps;
                    if (!Fits(hull, t, sheetWh)) continue;
                    double c = Clearance(hull, t, sheetWh);
                    if (!gotStep || c > bestStepClear) { gotStep = true; bestStepClear = c; bestStep = t; }
                }
                if (gotStep) { angle = bestStep; return FitResult.SnappedToStep; }
            }

            // 2) No step fits — fall back to the continuous search for the roomiest angle anywhere.

            List<double> criticals = CriticalAngles(hull, sheetWh);
            if (criticals.Count == 0) return FitResult.NoFit;

            bool found = false;
            double bestAngle = 0.0, bestClear = double.NegativeInfinity;

            for (int i = 0; i < criticals.Count; i++)
            {
                double lo = criticals[i];
                double hi = (i + 1 < criticals.Count) ? criticals[i + 1] : criticals[0] + Math.PI;

                // A critical angle can itself be the only feasible point of a degenerate (zero-width) arc,
                // so it is always a candidate in its own right.
                Consider(hull, sheetWh, lo, ref found, ref bestAngle, ref bestClear);
                if (hi - lo < 1e-12) continue;

                // Feasibility is constant strictly between two consecutive criticals, so one midpoint test
                // classifies the whole arc.
                if (!Fits(hull, 0.5 * (lo + hi), sheetWh)) continue;

                // Feasible arc: find its roomiest angle. Sample it, then ternary-refine around the best.
                int bestK = 0;
                double bestArc = double.NegativeInfinity;
                for (int k = 0; k <= ArcSamples; k++)
                {
                    double t = lo + (hi - lo) * k / ArcSamples;
                    double c = Clearance(hull, t, sheetWh);
                    if (c > bestArc) { bestArc = c; bestK = k; }
                    Consider(hull, sheetWh, t, ref found, ref bestAngle, ref bestClear);
                }

                double step = (hi - lo) / ArcSamples;
                double a = lo + Math.Max(0, bestK - 1) * step;
                double b = lo + Math.Min(ArcSamples, bestK + 1) * step;
                for (int it = 0; it < 60 && b - a > 1e-12; it++)
                {
                    double m1 = a + (b - a) / 3.0, m2 = b - (b - a) / 3.0;
                    if (Clearance(hull, m1, sheetWh) < Clearance(hull, m2, sheetWh)) a = m1; else b = m2;
                }
                Consider(hull, sheetWh, 0.5 * (a + b), ref found, ref bestAngle, ref bestClear);
            }

            if (!found) return FitResult.NoFit;
            angle = Fold(bestAngle);
            return FitResult.Reoriented;
        }

        // Keep the roomiest angle; on a tie (equal clearance) keep the smaller rotation.
        private static void Consider(double[] hull, double[] sheetWh, double t,
                                     ref bool found, ref double bestAngle, ref double bestClear)
        {
            if (!Fits(hull, t, sheetWh)) return;
            double c = Clearance(hull, t, sheetWh);
            bool better = !found
                          || c > bestClear + ClearanceTie
                          || (c > bestClear - ClearanceTie && Math.Abs(Fold(t)) < Math.Abs(Fold(bestAngle)));
            if (!better) return;
            found = true;
            if (c > bestClear) bestClear = c;
            bestAngle = t;
        }

        /// <summary>
        /// How much room the part has to spare at this angle: the largest, over the sheets, of the smaller
        /// of that sheet's two per-axis gaps. Negative when the part overflows every sheet. This is what
        /// the pass maximises - a part sitting on exactly 0 clearance fits on paper but will not nest.
        /// </summary>
        public static double Clearance(double[] xy, double angle, double[] sheetWh)
        {
            double w, h;
            Extents(xy, angle, out w, out h);
            double best = double.NegativeInfinity;
            for (int s = 0; s + 1 < sheetWh.Length; s += 2)
            {
                double m = Math.Min(sheetWh[s] - w, sheetWh[s + 1] - h);
                if (m > best) best = m;
            }
            return best;
        }

        /// <summary>True when the outline, rotated by <paramref name="angle"/> radians, has a bounding box
        /// that fits inside at least one of the (width,height) pairs in <paramref name="sheetWh"/>.</summary>
        public static bool Fits(double[] xy, double angle, double[] sheetWh)
        {
            double w, h;
            Extents(xy, angle, out w, out h);
            for (int s = 0; s + 1 < sheetWh.Length; s += 2)
            {
                // The candidate angles sit exactly ON the feasible boundary, so the slack also carries a
                // relative term: on a 10 m sheet an absolute 1e-6 is below the noise of the rotation itself.
                double tw = FitTolerance + 1e-11 * sheetWh[s];
                double th = FitTolerance + 1e-11 * sheetWh[s + 1];
                if (w <= sheetWh[s] + tw && h <= sheetWh[s + 1] + th) return true;
            }
            return false;
        }

        /// <summary>Bounding-box size of the outline after rotating it by <paramref name="angle"/> radians.</summary>
        public static void Extents(double[] xy, double angle, out double w, out double h)
        {
            double c = Math.Cos(angle), s = Math.Sin(angle);
            double minx = double.MaxValue, maxx = double.MinValue;
            double miny = double.MaxValue, maxy = double.MinValue;
            for (int i = 0; i + 1 < xy.Length; i += 2)
            {
                double x = xy[i] * c - xy[i + 1] * s;
                double y = xy[i] * s + xy[i + 1] * c;
                if (x < minx) minx = x;
                if (x > maxx) maxx = x;
                if (y < miny) miny = y;
                if (y > maxy) maxy = y;
            }
            w = maxx - minx;
            h = maxy - miny;
        }

        // ---- the analytic candidate set ---------------------------------------------------------------

        // Every angle at which the feasible set can begin or end, folded into [-pi/2, pi/2) (the AABB has
        // a pi period, so that range already holds every distinct orientation), then SORTED and de-duped so
        // two consecutive entries bound an arc of CONSTANT feasibility. A candidate that turns out to lie
        // outside the cell that produced it is harmless noise: it only adds a redundant split point.
        private static List<double> CriticalAngles(double[] hull, double[] sheetWh)
        {
            var result = new List<double>();
            List<double> breaks = Breakpoints(hull);
            if (breaks.Count == 0) { result.Add(0.0); return result; }

            for (int b = 0; b < breaks.Count; b++)
            {
                double lo = breaks[b];
                double hi = (b + 1 < breaks.Count) ? breaks[b + 1] : breaks[0] + Math.PI;
                result.Add(lo);
                if (hi - lo < 1e-12) continue;

                // The antipodal pair supporting w and the one supporting h are fixed across the cell, so
                // read them off at its midpoint: w(t) = Rw*cos(t - psiW), h(t) = Rh*cos(t - psiH).
                double mid = 0.5 * (lo + hi);
                double Rw, psiW, Rh, psiH;
                CellSinusoids(hull, mid, out Rw, out psiW, out Rh, out psiH);

                for (int s = 0; s + 1 < sheetWh.Length; s += 2)
                {
                    AddEqualityAngles(result, Rw, psiW, sheetWh[s]);        // w(t) == sheet width
                    AddEqualityAngles(result, Rh, psiH, sheetWh[s + 1]);    // h(t) == sheet height
                }
            }

            for (int i = 0; i < result.Count; i++) result[i] = Fold(result[i]);
            result.Sort();

            var dedup = new List<double>(result.Count);
            for (int i = 0; i < result.Count; i++)
                if (i == 0 || result[i] - dedup[dedup.Count - 1] > 1e-12) dedup.Add(result[i]);
            return dedup;
        }

        // Angles where R*cos(t - psi) == limit. None when the span never reaches the limit (R <= limit:
        // that constraint is satisfied everywhere, so it contributes no boundary).
        private static void AddEqualityAngles(List<double> into, double r, double psi, double limit)
        {
            if (r <= limit || r <= 0.0) return;
            double ratio = limit / r;
            if (ratio < -1.0) ratio = -1.0; else if (ratio > 1.0) ratio = 1.0;
            double ac = Math.Acos(ratio);
            into.Add(psi + ac);
            into.Add(psi - ac);
        }

        // The two antipodal pairs supporting the box at angle t, expressed as sinusoids in t.
        private static void CellSinusoids(double[] hull, double t,
                                          out double rw, out double psiW, out double rh, out double psiH)
        {
            double c = Math.Cos(t), s = Math.Sin(t);
            int xMax = 0, xMin = 0, yMax = 0, yMin = 0;
            double vxMax = double.MinValue, vxMin = double.MaxValue;
            double vyMax = double.MinValue, vyMin = double.MaxValue;
            for (int i = 0; i + 1 < hull.Length; i += 2)
            {
                double x = hull[i] * c - hull[i + 1] * s;
                double y = hull[i] * s + hull[i + 1] * c;
                if (x > vxMax) { vxMax = x; xMax = i; }
                if (x < vxMin) { vxMin = x; xMin = i; }
                if (y > vyMax) { vyMax = y; yMax = i; }
                if (y < vyMin) { vyMin = y; yMin = i; }
            }

            // w(t) = dx*cos t - dy*sin t  ->  A = dx, B = -dy
            double dxw = hull[xMax] - hull[xMin], dyw = hull[xMax + 1] - hull[xMin + 1];
            rw = Math.Sqrt(dxw * dxw + dyw * dyw);
            psiW = Math.Atan2(-dyw, dxw);

            // h(t) = dx*sin t + dy*cos t  ->  A = dy, B = dx
            double dxh = hull[yMax] - hull[yMin], dyh = hull[yMax + 1] - hull[yMin + 1];
            rh = Math.Sqrt(dxh * dxh + dyh * dyh);
            psiH = Math.Atan2(dxh, dyh);
        }

        // The angles at which a supporting vertex changes hands: one rotated axis lines up with a hull-edge
        // normal. Sorted, de-duplicated, folded into [-pi/2, pi/2); consecutive entries bound one cell.
        private static List<double> Breakpoints(double[] hull)
        {
            int n = hull.Length / 2;
            var list = new List<double>(2 * n);
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                double ex = hull[2 * j] - hull[2 * i], ey = hull[2 * j + 1] - hull[2 * i + 1];
                if (Math.Abs(ex) < 1e-12 && Math.Abs(ey) < 1e-12) continue;
                list.Add(Fold(Math.Atan2(ex, ey)));    // (cos t, -sin t) perpendicular to this edge
                list.Add(Fold(Math.Atan2(-ey, ex)));   // (sin t,  cos t) perpendicular to this edge
            }
            list.Sort();

            var dedup = new List<double>(list.Count);
            for (int i = 0; i < list.Count; i++)
                if (i == 0 || list[i] - dedup[dedup.Count - 1] > 1e-12) dedup.Add(list[i]);
            return dedup;
        }

        // Fold an angle into [-pi/2, pi/2). The bounding box repeats every pi, so this loses nothing.
        private static double Fold(double t)
        {
            double f = t - Math.PI * Math.Floor((t + Math.PI / 2) / Math.PI);
            if (f >= Math.PI / 2) f -= Math.PI;
            if (f < -Math.PI / 2) f += Math.PI;
            return f;
        }

        // ---- convex hull --------------------------------------------------------------------------------

        /// <summary>
        /// Monotone-chain convex hull (CCW, no repeated last point), interleaved x,y in and out.
        /// Self-contained on purpose: this pass runs before anything else is set up and must not care
        /// about ring orientation, closing duplicates or collinear runs.
        /// </summary>
        public static double[] Hull(double[] xy)
        {
            int n = xy.Length / 2;
            if (n < 3) return xy;

            var idx = new int[n];
            for (int i = 0; i < n; i++) idx[i] = i;
            Array.Sort(idx, (a, b) => xy[2 * a] == xy[2 * b]
                                        ? xy[2 * a + 1].CompareTo(xy[2 * b + 1])
                                        : xy[2 * a].CompareTo(xy[2 * b]));

            var lower = new List<int>(n);
            for (int i = 0; i < n; i++)
            {
                while (lower.Count >= 2 && Cross(xy, lower[lower.Count - 2], lower[lower.Count - 1], idx[i]) <= 0)
                    lower.RemoveAt(lower.Count - 1);
                lower.Add(idx[i]);
            }

            var upper = new List<int>(n);
            for (int i = n - 1; i >= 0; i--)
            {
                while (upper.Count >= 2 && Cross(xy, upper[upper.Count - 2], upper[upper.Count - 1], idx[i]) <= 0)
                    upper.RemoveAt(upper.Count - 1);
                upper.Add(idx[i]);
            }

            lower.RemoveAt(lower.Count - 1);   // each chain's last point starts the other
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            if (lower.Count < 3) return xy;    // collinear / degenerate: the raw points bound it just as well

            var hull = new double[lower.Count * 2];
            for (int i = 0; i < lower.Count; i++)
            {
                hull[2 * i] = xy[2 * lower[i]];
                hull[2 * i + 1] = xy[2 * lower[i] + 1];
            }
            return hull;
        }

        private static double Cross(double[] xy, int o, int a, int b)
        {
            return (xy[2 * a] - xy[2 * o]) * (xy[2 * b + 1] - xy[2 * o + 1])
                 - (xy[2 * a + 1] - xy[2 * o + 1]) * (xy[2 * b] - xy[2 * o]);
        }
    }
}
