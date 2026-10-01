# OpenNest1

Nests parts onto sheets with the no-fit-polygon solver (no attributes). Takes curves or surfaces directly; supports multi-start Tries and a live preview.

## Example

**Files to download:**

- [⬇ opennest1.ghx](files/opennest1/opennest1.ghx)

![opennest1 — opennest1](img/opennest1/opennest1.png)

![opennest1 — opennest1_screenshot](img/opennest1/opennest1_screenshot.png)

## Inputs

| Parameter | Type | Access | Description |
| --- | --- | --- | --- |
| **Sheets** | Geometry | list | Sheets — closed planar surfaces (outer + holes) or closed curves. A single sheet is auto-copied so parts can overflow onto more. |
| **Geo** | Geometry | list | Parts to nest — closed curves or planar surfaces. |
| **Spacing** | Number | item | Gap to keep between placed parts. _Default: 1._ |
| **Placement** | Integer | item | Placement strategy index. _Default: 1._ |
| **Tolerance** | Number | item | Curve simplification tolerance. _Default: 0.1._ |
| **Rotations** | Integer | item | Number of rotation angles to try per part. _Default: 4._ |
| **Iterations** | Integer | item | Solver generations to evolve. You watch each one tighten in the preview; higher = tighter but slower. ~4–10 typical. _Default: 6._ |
| **Seed** | Integer | item | Random seed for reproducible results. _Default: 1._ |
| **Reset** | Boolean | item | Set TRUE (wire a Button) to clear the whole component instantly and drop any running solve. _Default: false._ |
| **Run** | Boolean | item | Start the nesting solve. _Default: false._ |
| **Timeout** | Number | item | Stop after this many seconds and keep the best layout so far (0 = no limit). _Default: 0._ |
| **Fit Orient** | Boolean | item | Before nesting, rotate any part whose bounding box does not fit a sheet as drawn into an orientation that does — preferring one of the **Rotations** steps, and only using an off-step angle when no step fits. Parts that already fit are never touched. _Default: true._ |

## Outputs

| Parameter | Type | Description |
| --- | --- | --- |
| **Sheets** | Curve | Sheet outlines used for nesting. |
| **Geo** | Geometry | Nested parts placed on the sheets. |
| **ID** | Integer | Polygon id number |
| **Transform** | Transform | Placement transform per part. |
| **IDS** | Integer | Sheet id number |

## Initial orientation

Controlled by the **Fit Orient** input (default **true**).

Before the solve starts, every part is checked against the sheet dimensions:

1. **It already fits as drawn** — left exactly as you drew it. Nothing happens. This is the normal case.
2. **It fits at one of the Rotations steps** (360°/n) — the part is turned onto that step, the roomiest one
   if several work. The solve still only ever sees orientations your **Rotations** input already allowed.
3. **No step fits** — only then does the pass use an off-step angle, choosing the orientation with the most
   room to spare inside the sheet. So a long bar against a tall sheet is stood upright rather than being
   reported unplaced.
4. **Nothing fits in any orientation** — the part is left as drawn and handled the way it always was.

Step 3 picks the roomiest orientation rather than the smallest rotation on purpose: the smallest rotation
that fits is, by definition, the one where the part only just stops overflowing, so it ends up touching the
sheet limit with no clearance and the nester still cannot place it.

When the pass rotates anything it says so on the Rhino command line, e.g.
`[pre-orient] 1 of 3 part group(s) rotated to fit the sheet (0 snapped to a 4-step rotation, 1 off-step): #0=-60.45deg`.
No such line means no part needed re-orienting.

This runs once, per distinct part (not per copy), and is a no-op for input that already fits. Note it
compares bounding boxes only, so it does not account for kerf or the sheet's edge setback — a part that
clears by a hair may still not place.
