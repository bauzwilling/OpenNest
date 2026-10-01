using System;
using System.Collections.Generic;
using nest_lib.preorient;
using Xunit;

namespace nest_lib_batch_tests
{
    // Unit tests for the pre-orientation core: pick the ROOMIEST rotation about Z that makes a part's
    // bounding box fit inside some sheet's bounding box, leave a part that already fits untouched, and
    // leave a part that fits in no orientation exactly as drawn.
    public class FitOrientationTests
    {
        // ---- helpers --------------------------------------------------------------------------------

        // Axis-aligned rectangle w x h with its lower-left at (ox, oy).
        private static double[] Rect(double w, double h, double ox = 0, double oy = 0)
            => new[] { ox, oy, ox + w, oy, ox + w, oy + h, ox, oy + h };

        private static double[] Rotate(double[] xy, double degrees)
        {
            double a = degrees * Math.PI / 180.0, c = Math.Cos(a), s = Math.Sin(a);
            var outXy = new double[xy.Length];
            for (int i = 0; i + 1 < xy.Length; i += 2)
            {
                outXy[i] = xy[i] * c - xy[i + 1] * s;
                outXy[i + 1] = xy[i] * s + xy[i + 1] * c;
            }
            return outXy;
        }

        private static void AssertFitsAfter(double[] part, double[] sheets, double angle)
        {
            Assert.True(FitOrientation.Fits(part, angle, sheets),
                        "the returned angle must actually make the part fit");
        }

        // The pass promises the ROOMIEST fitting rotation: a dense independent sweep must not find an
        // orientation with materially more clearance.
        private static void AssertNoRoomierAngle(double[] part, double[] sheets, double angle)
        {
            double ours = FitOrientation.Clearance(part, angle, sheets);
            double best = double.NegativeInfinity, bestDeg = 0;
            for (double d = -90; d < 90; d += 0.005)
            {
                double c = FitOrientation.Clearance(part, d * Math.PI / 180.0, sheets);
                if (c > best) { best = c; bestDeg = d; }
            }
            Assert.True(ours >= best - 1e-3,
                        $"returned {angle * 180.0 / Math.PI:F4} deg with {ours:F4} clearance, but " +
                        $"{bestDeg:F4} deg gives {best:F4}");
        }

        // ---- rule 1: already fits -> untouched -------------------------------------------------------

        [Fact]
        public void PartThatAlreadyFits_IsLeftAsDrawn()
        {
            double angle;
            var r = FitOrientation.Solve(Rect(100, 50), new[] { 1000.0, 500.0 }, out angle);
            Assert.Equal(FitResult.AlreadyFits, r);
            Assert.Equal(0.0, angle);
        }

        [Fact]
        public void PartExactlyTheSheetSize_StillCountsAsFitting()
        {
            double angle;
            var r = FitOrientation.Solve(Rect(1000, 500), new[] { 1000.0, 500.0 }, out angle);
            Assert.Equal(FitResult.AlreadyFits, r);
            Assert.Equal(0.0, angle);
        }

        [Fact]
        public void PartPositionIsIrrelevant_OnlyItsExtentsMatter()
        {
            double angle;
            var far = Rect(100, 50, 1e6, -4e5);
            Assert.Equal(FitResult.AlreadyFits, FitOrientation.Solve(far, new[] { 1000.0, 500.0 }, out angle));
        }

        // ---- rule 2: rotate into the roomiest orientation that fits ----------------------------------

        [Fact]
        public void TooWideButNotTooTall_IsStoodUpright()
        {
            // A 900 x 100 bar into a 200 x 1000 sheet: it has to end up near-vertical.
            var part = Rect(900, 100);
            var sheets = new[] { 200.0, 1000.0 };

            double angle;
            Assert.Equal(FitResult.Reoriented, FitOrientation.Solve(part, sheets, out angle));
            AssertFitsAfter(part, sheets, angle);
            Assert.True(Math.Abs(angle) * 180.0 / Math.PI > 80.0, "expected a near-upright rotation");
            AssertNoRoomierAngle(part, sheets, angle);
        }

        [Fact]
        public void SkewPart_IsRotatedBackToAFittingOrientation()
        {
            // A 900 x 100 bar drawn at 30 deg spans ~830 x 537 - too tall for the sheet.
            var part = Rotate(Rect(900, 100), 30.0);
            var sheets = new[] { 950.0, 150.0 };

            double angle;
            Assert.Equal(FitResult.Reoriented, FitOrientation.Solve(part, sheets, out angle));
            AssertFitsAfter(part, sheets, angle);
            AssertNoRoomierAngle(part, sheets, angle);
        }

        [Fact]
        public void FitsAnyOneOfSeveralSheets_NotJustTheFirst()
        {
            var part = Rect(900, 100);
            // the first sheet can never take it in any orientation; the second can, upright.
            var sheets = new[] { 150.0, 150.0, 200.0, 1000.0 };

            double angle;
            Assert.Equal(FitResult.Reoriented, FitOrientation.Solve(part, sheets, out angle));
            AssertFitsAfter(part, sheets, angle);
        }

        [Fact]
        public void NonRectangularPart_IsHandledThroughItsConvexHull()
        {
            // An L-shape whose bbox is 900 x 400; the sheet is 500 x 1000, so it must go upright.
            var part = new[] { 0.0, 0.0, 900.0, 0.0, 900.0, 100.0, 100.0, 100.0, 100.0, 400.0, 0.0, 400.0 };
            var sheets = new[] { 500.0, 1000.0 };

            double angle;
            Assert.Equal(FitResult.Reoriented, FitOrientation.Solve(part, sheets, out angle));
            AssertFitsAfter(part, sheets, angle);
            AssertNoRoomierAngle(part, sheets, angle);
        }

        // ---- the regression this rule exists for ------------------------------------------------------

        // A real part (nest_orient_test.dxf) against a real 2500 x 1250 sheet. The earlier
        // "smallest rotation that fits" rule returned -54.8402 deg, where the part is EXACTLY 1250.000
        // tall: it fits on paper, the nester cannot place it with any kerf, rhino_example then hands the
        // unplaced part Transform.Identity, and the user sees no change whatsoever. The roomiest
        // orientation has ~82 mm to spare and actually nests.
        [Fact]
        public void RealPart_IsGivenRealClearance_NotAKnifeEdgeFit()
        {
            var part = new[]
            {
                5548.5544, -3598.7999,
                5163.6645, -4277.7731,
                4591.2039, -2919.8268,
                5933.4444, -2919.8268,
            };
            var sheets = new[] { 2500.0, 1250.0 };

            double w0, h0;
            FitOrientation.Extents(part, 0.0, out w0, out h0);
            Assert.True(h0 > 1250.0, "precondition: the part does not fit as drawn");

            double angle;
            Assert.Equal(FitResult.Reoriented, FitOrientation.Solve(part, sheets, out angle));
            AssertFitsAfter(part, sheets, angle);
            AssertNoRoomierAngle(part, sheets, angle);

            double clearance = FitOrientation.Clearance(part, angle, sheets);
            Assert.True(clearance > 50.0,
                        $"expected real headroom, got {clearance:F4} mm at {angle * 180.0 / Math.PI:F4} deg");

            // and specifically NOT the old knife-edge answer
            Assert.True(Math.Abs(angle * 180.0 / Math.PI + 54.8402) > 1.0,
                        "returned the old zero-clearance orientation");
        }

        // ---- the solver's own rotation steps come first -----------------------------------------------

        [Fact]
        public void AFittingRotationStep_IsPreferredOverACustomAngle()
        {
            // 900 x 100 bar, sheet 200 x 1000: a plain 90 deg works, so the pass must use 90 and NOT the
            // roomiest off-step angle. Snapping keeps the engine's rotation set (base + k*360/n) unchanged.
            var part = Rect(900, 100);
            var sheets = new[] { 200.0, 1000.0 };

            double angle;
            Assert.Equal(FitResult.SnappedToStep, FitOrientation.Solve(part, sheets, out angle, 4));
            Assert.Equal(90.0, angle * 180.0 / Math.PI, 9);
            AssertFitsAfter(part, sheets, angle);
        }

        [Fact]
        public void TheSmallestFittingStepWinsATie()
        {
            // 90 and 270 have identical bounding boxes, so both fit; the smaller rotation must win.
            var part = Rect(900, 100);
            double angle;
            Assert.Equal(FitResult.SnappedToStep,
                         FitOrientation.Solve(part, new[] { 200.0, 1000.0 }, out angle, 4));
            Assert.Equal(90.0, angle * 180.0 / Math.PI, 9);
        }

        [Fact]
        public void WhenNoStepFits_ItFallsBackToTheRoomiestCustomAngle()
        {
            // The real DXF part: at 4 steps neither 0/90/180/270 fits a 2500 x 1250 sheet, so the pass
            // has to leave the step set and find an off-step angle.
            var part = new[]
            {
                5548.5544, -3598.7999,
                5163.6645, -4277.7731,
                4591.2039, -2919.8268,
                5933.4444, -2919.8268,
            };
            var sheets = new[] { 2500.0, 1250.0 };

            foreach (int steps in new[] { 1, 2, 4 })
                Assert.False(FitOrientation.Fits(part, Math.PI / 2, sheets),
                             "precondition: no quarter-turn step fits");

            double angle;
            Assert.Equal(FitResult.Reoriented, FitOrientation.Solve(part, sheets, out angle, 4));
            AssertFitsAfter(part, sheets, angle);
            AssertNoRoomierAngle(part, sheets, angle);
        }

        [Fact]
        public void ManySteps_FindOneWhereFewStepsCannot()
        {
            // The same part with a fine step set: 3600 steps (0.1 deg) contains a fitting orientation, so
            // the pass snaps instead of going off-step.
            var part = new[]
            {
                5548.5544, -3598.7999,
                5163.6645, -4277.7731,
                4591.2039, -2919.8268,
                5933.4444, -2919.8268,
            };
            var sheets = new[] { 2500.0, 1250.0 };

            double angle;
            Assert.Equal(FitResult.SnappedToStep, FitOrientation.Solve(part, sheets, out angle, 3600));
            AssertFitsAfter(part, sheets, angle);

            // and it is a genuine multiple of the step
            double stepDeg = 360.0 / 3600.0;
            double k = angle * 180.0 / Math.PI / stepDeg;
            Assert.Equal(Math.Round(k), k, 6);
        }

        [Fact]
        public void APartThatAlreadyFits_IsStillUntouchedWhateverTheStepCount()
        {
            double angle;
            foreach (int steps in new[] { 0, 1, 4, 3600 })
            {
                Assert.Equal(FitResult.AlreadyFits,
                             FitOrientation.Solve(Rect(100, 50), new[] { 1000.0, 500.0 }, out angle, steps));
                Assert.Equal(0.0, angle);
            }
        }

        [Fact]
        public void StepSearchNeverReturnsAnAngleThatDoesNotFit()
        {
            var rnd = new Random(4242);
            for (int t = 0; t < 300; t++)
            {
                int n = 3 + rnd.Next(10);
                var xy = new double[n * 2];
                for (int i = 0; i < n * 2; i++) xy[i] = rnd.NextDouble() * 900.0 - 450.0;
                var sheets = new[] { 150.0 + rnd.NextDouble() * 700.0, 150.0 + rnd.NextDouble() * 700.0 };
                int steps = new[] { 2, 3, 4, 8, 16 }[rnd.Next(5)];

                double angle;
                var r = FitOrientation.Solve(xy, sheets, out angle, steps);
                if (r == FitResult.AlreadyFits || r == FitResult.NoFit) { Assert.Equal(0.0, angle); continue; }

                Assert.True(FitOrientation.Fits(xy, angle, sheets), "returned an angle that does not fit");

                if (r == FitResult.SnappedToStep)
                {
                    double k = angle / (2.0 * Math.PI / steps);
                    Assert.Equal(Math.Round(k), k, 9);
                }
                else
                {
                    // off-step is only allowed when NO step fits
                    for (int k = 1; k < steps; k++)
                        Assert.False(FitOrientation.Fits(xy, k * 2.0 * Math.PI / steps, sheets),
                                     $"step {k} fits, so an off-step angle should not have been used");
                }
            }
        }

        // ---- rule 3: nothing fits -> leave it alone --------------------------------------------------

        [Fact]
        public void PartThatFitsNoOrientation_IsLeftAsDrawn()
        {
            double angle;
            var r = FitOrientation.Solve(Rect(5000, 4000), new[] { 1000.0, 500.0 }, out angle);
            Assert.Equal(FitResult.NoFit, r);
            Assert.Equal(0.0, angle);
        }

        [Fact]
        public void JustTooLongInEveryDirection_IsLeftAsDrawn()
        {
            var part = Rect(1200, 1200);
            double angle;
            Assert.Equal(FitResult.NoFit, FitOrientation.Solve(part, new[] { 1000.0, 1000.0 }, out angle));
            Assert.Equal(0.0, angle);
        }

        [Fact]
        public void NoSheets_IsANoOp()
        {
            double angle;
            Assert.Equal(FitResult.AlreadyFits, FitOrientation.Solve(Rect(5000, 4000), new double[0], out angle));
            Assert.Equal(0.0, angle);
        }

        [Fact]
        public void DegenerateOutline_IsANoOp()
        {
            double angle;
            Assert.Equal(FitResult.AlreadyFits,
                         FitOrientation.Solve(new[] { 0.0, 0.0, 1.0, 1.0 }, new[] { 10.0, 10.0 }, out angle));
            Assert.Equal(0.0, angle);
        }

        // ---- properties, against an independent dense sweep ------------------------------------------

        [Fact]
        public void WheneverSomeOrientationFits_ThePassFindsTheRoomiestOne()
        {
            var rnd = new Random(1234);
            for (int t = 0; t < 400; t++)
            {
                int n = 3 + rnd.Next(12);
                var xy = new double[n * 2];
                for (int i = 0; i < n; i++)
                {
                    xy[2 * i] = rnd.NextDouble() * 1000.0 - 500.0;
                    xy[2 * i + 1] = rnd.NextDouble() * 1000.0 - 500.0;
                }
                var sheets = new[] { 100.0 + rnd.NextDouble() * 900.0, 100.0 + rnd.NextDouble() * 900.0 };

                bool anyFits = false;
                for (int d = 0; d < 1800 && !anyFits; d++)
                    anyFits = FitOrientation.Fits(xy, d * 0.1 * Math.PI / 180.0, sheets);

                double angle;
                var r = FitOrientation.Solve(xy, sheets, out angle);
                if (anyFits)
                {
                    Assert.NotEqual(FitResult.NoFit, r);
                    Assert.True(FitOrientation.Fits(xy, angle, sheets),
                                $"reported {r} at {angle * 180.0 / Math.PI} deg but it does not fit");
                    // Roominess is only promised when we actually chose an orientation. A part that
                    // already fits is left exactly as drawn BY DESIGN, even if some other angle would
                    // give it more room — rotating parts that already fit is not this pass's job.
                    if (r == FitResult.Reoriented) AssertNoRoomierAngle(xy, sheets, angle);
                }
                else
                {
                    Assert.Equal(FitResult.NoFit, r);
                    Assert.Equal(0.0, angle);
                }
            }
        }

        [Fact]
        public void ReorientedAlwaysMeansStrictlyBetterThanAsDrawn()
        {
            var rnd = new Random(99);
            for (int t = 0; t < 300; t++)
            {
                int n = 3 + rnd.Next(10);
                var xy = new double[n * 2];
                for (int i = 0; i < n * 2; i++) xy[i] = rnd.NextDouble() * 800.0 - 400.0;
                var sheets = new[] { 200.0 + rnd.NextDouble() * 600.0, 200.0 + rnd.NextDouble() * 600.0 };

                double angle;
                if (FitOrientation.Solve(xy, sheets, out angle) != FitResult.Reoriented) continue;

                Assert.True(FitOrientation.Fits(xy, angle, sheets));
                Assert.False(FitOrientation.Fits(xy, 0.0, sheets));
                Assert.True(FitOrientation.Clearance(xy, angle, sheets) > FitOrientation.Clearance(xy, 0.0, sheets));
            }
        }

        // ---- convex hull ------------------------------------------------------------------------------

        [Fact]
        public void Hull_DropsInteriorPointsAndPreservesExtents()
        {
            var xy = new[] { 0.0, 0.0, 10.0, 0.0, 10.0, 10.0, 0.0, 10.0, 5.0, 5.0 };
            var hull = FitOrientation.Hull(xy);
            Assert.Equal(4, hull.Length / 2);

            foreach (double deg in new[] { 0.0, 17.0, 45.0, -80.0 })
            {
                double w0, h0, w1, h1;
                FitOrientation.Extents(xy, deg * Math.PI / 180.0, out w0, out h0);
                FitOrientation.Extents(hull, deg * Math.PI / 180.0, out w1, out h1);
                Assert.Equal(w0, w1, 9);
                Assert.Equal(h0, h1, 9);
            }
        }

        [Fact]
        public void Hull_MatchesTheOutlineExtentsOnRandomInput()
        {
            var rnd = new Random(77);
            for (int t = 0; t < 200; t++)
            {
                int n = 3 + rnd.Next(30);
                var xy = new double[n * 2];
                for (int i = 0; i < n * 2; i++) xy[i] = rnd.NextDouble() * 200.0 - 100.0;
                var hull = FitOrientation.Hull(xy);

                for (int d = 0; d < 180; d += 13)
                {
                    double w0, h0, w1, h1;
                    FitOrientation.Extents(xy, d * Math.PI / 180.0, out w0, out h0);
                    FitOrientation.Extents(hull, d * Math.PI / 180.0, out w1, out h1);
                    Assert.Equal(w0, w1, 9);
                    Assert.Equal(h0, h1, 9);
                }
            }
        }

        [Fact]
        public void Hull_SurvivesCollinearAndDuplicatePoints()
        {
            var xy = new[] { 0.0, 0.0, 5.0, 0.0, 10.0, 0.0, 10.0, 0.0, 0.0, 0.0 };
            var hull = FitOrientation.Hull(xy);
            double w, h;
            FitOrientation.Extents(hull, 0.0, out w, out h);
            Assert.Equal(10.0, w, 9);
            Assert.Equal(0.0, h, 9);
        }
    }
}
