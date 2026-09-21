using System;
using System.Collections.Generic;
using Rhino.Geometry;
using nest_lib.preorient;

namespace nest_rhino_lib
{
    /// <summary>
    /// PRE-ORIENTATION PASS. Runs once, before a solve, over every part group: if the part's axis-aligned
    /// bounding box does NOT fit inside any sheet's bounding box AS DRAWN, the part is rotated about Z
    /// until it does, and that rotation is folded into the part's to_xy frame (its "initial orientation").
    ///
    /// Rules, in order:
    ///   1. The part already fits a sheet as drawn  -> angle 0, geometry untouched (the overwhelmingly
    ///      common case, so this pass is a no-op for normal input).
    ///   2. Some orientation fits                   -> the ROOMIEST one (most clearance inside the sheet);
    ///      ties go to the smaller rotation. NOT the smallest rotation that fits: that is by construction
    ///      the angle where the part only just stops overflowing, so it lands on ~zero clearance, the
    ///      nester still cannot place it, and rhino_example gives an unplaced part Transform.Identity —
    ///      the rotation is discarded and the user sees no change at all.
    ///   3. No orientation fits at all              -> angle 0, left exactly as drawn. The solver reports
    ///      it unplaced as before; a part that cannot be rescued is never rotated.
    ///
    /// Why it matters: the initial orientation is what the placer starts from, and a part whose start
    /// orientation overflows the sheet can be dropped before the GA / relaxer ever gets to try a rotation
    /// (and with Rotations = 1 it is dropped for good). Starting it in a fitting orientation removes that.
    ///
    /// The test is AABB vs AABB on purpose: it is the "does the part fit the sheet dimension" question the
    /// pass exists to answer, and it matches what the callers can guarantee (the NFP engine nests into the
    /// sheet outline, the physics engine strip-packs into the sheet BBOX). A concave sheet may still reject
    /// a part whose box fits - that stays the solver's call, exactly as before.
    ///
    /// This is the Rhino adapter only; the geometry lives in (and is unit-tested through)
    /// <see cref="nest_lib.preorient.FitOrientation"/>.
    /// </summary>
    public static class part_preorient
    {
        public static double[] FitAngles(nest_geo geo, nest_sheets sheets)
        {
            int ignored;
            return FitAngles(geo, sheets, out ignored);
        }

        /// <summary>
        /// One rotation angle (RADIANS, CCW about Z) per group of <paramref name="geo"/>.boundary_sorted,
        /// to be applied to that group's nesting frame. 0 = leave the part as drawn.
        /// <paramref name="rotatedCount"/> reports how many groups were actually re-oriented.
        /// </summary>
        public static double[] FitAngles(nest_geo geo, nest_sheets sheets, out int rotatedCount)
        {
            rotatedCount = 0;
            int nGroups = (geo == null || geo.boundary_sorted == null) ? 0 : geo.boundary_sorted.Count;
            double[] angles = new double[nGroups];
            if (nGroups == 0) return angles;

            double[] sheetWh = SheetEnvelopes(sheets);
            if (sheetWh.Length == 0) return angles;      // no sheets to fit into -> nothing to decide

            for (int i = 0; i < nGroups; i++)
            {
                var rings = geo.boundary_sorted[i];
                if (rings == null || rings.Count == 0) continue;
                Polyline outer = rings[0].Item2;
                if (outer == null || outer.Count < 3) continue;

                double angle;
                if (FitOrientation.Solve(Flatten(outer), sheetWh, out angle) == FitResult.Reoriented)
                {
                    angles[i] = angle;
                    rotatedCount++;
                }
            }
            return angles;
        }

        /// <summary>
        /// The frame that maps a part group onto WorldXY (its first outer vertex becomes the origin), with
        /// the pre-orientation rotation <paramref name="angle"/> folded in. Every solver builds its to_xy
        /// through here so the pass applies identically across engines.
        /// </summary>
        public static Transform ToXy(Polyline outer, double angle)
        {
            Plane plane = new Plane(outer[0], Vector3d.XAxis, Vector3d.YAxis);
            Transform to_xy = Transform.PlaneToPlane(plane, Plane.WorldXY);
            if (angle != 0.0)
                to_xy = Transform.Rotation(angle, Vector3d.ZAxis, Point3d.Origin) * to_xy;
            return to_xy;
        }

        /// <summary>Per-group to_xy frames for a whole nest_geo, pre-orientation applied.</summary>
        public static Transform[] BuildToXy(nest_geo geo, nest_sheets sheets, out int rotatedCount)
        {
            double[] angles = FitAngles(geo, sheets, out rotatedCount);
            Transform[] to_xy = new Transform[angles.Length];
            for (int i = 0; i < angles.Length; i++)
                to_xy[i] = ToXy(geo.boundary_sorted[i][0].Item2, angles[i]);
            return to_xy;
        }

        /// <summary>
        /// Bounding-box size (width, height) of each part group AS THE SOLVER WILL SEE IT — i.e. after the
        /// pre-orientation rotation, which for most parts is nest_geo.GroupBounds() unchanged. Anything
        /// that reasons about a part's dimensions (batch distribution, fit heuristics) should read them
        /// from here rather than from the as-drawn box, or it reasons about an orientation the solver no
        /// longer uses.
        /// </summary>
        public static List<Tuple<double, double>> GroupExtents(nest_geo geo, nest_sheets sheets)
        {
            int nGroups = (geo == null || geo.boundary_sorted == null) ? 0 : geo.boundary_sorted.Count;
            var result = new List<Tuple<double, double>>(nGroups);
            if (nGroups == 0) return result;

            double[] angles = FitAngles(geo, sheets);
            for (int i = 0; i < nGroups; i++)
            {
                var rings = geo.boundary_sorted[i];
                if (rings == null || rings.Count == 0 || rings[0].Item2 == null)
                {
                    result.Add(Tuple.Create(0.0, 0.0));
                    continue;
                }
                double w, h;
                FitOrientation.Extents(Flatten(rings[0].Item2), angles[i], out w, out h);
                result.Add(Tuple.Create(w, h));
            }
            return result;
        }

        // ---- internals ---------------------------------------------------------------------------

        // (width, height) of every live sheet's bounding box, interleaved. nest_sheets.sheets is a
        // fixed-length array padded with nulls, so stop at the first empty slot exactly like the solvers'
        // own flatten loops.
        private static double[] SheetEnvelopes(nest_sheets sheets)
        {
            var list = new List<double>();
            if (sheets == null || sheets.sheets == null) return list.ToArray();
            for (int s = 0; s < sheets.sheets.Length; s++)
            {
                if (sheets.sheets[s] == null || sheets.sheets[s].Length == 0) break;
                Polyline outer = sheets.sheets[s][0];
                if (outer == null || outer.Count < 3) continue;
                BoundingBox bb = outer.BoundingBox;
                double w = bb.Max.X - bb.Min.X, h = bb.Max.Y - bb.Min.Y;
                if (w > 0 && h > 0) { list.Add(w); list.Add(h); }
            }
            return list.ToArray();
        }

        private static double[] Flatten(Polyline pl)
        {
            var xy = new double[pl.Count * 2];
            for (int i = 0; i < pl.Count; i++) { xy[2 * i] = pl[i].X; xy[2 * i + 1] = pl[i].Y; }
            return xy;
        }
    }
}
