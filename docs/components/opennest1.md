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

## Outputs

| Parameter | Type | Description |
| --- | --- | --- |
| **Sheets** | Curve | Sheet outlines used for nesting. |
| **Geo** | Geometry | Nested parts placed on the sheets. |
| **ID** | Integer | Polygon id number |
| **Transform** | Transform | Placement transform per part. |
| **IDS** | Integer | Sheet id number |

## Initial orientation

Before the solve starts, every part is checked against the sheet dimensions. A part whose bounding box
already fits a sheet is left exactly as you drew it. A part whose bounding box is too big for every sheet
as drawn is **rotated into the orientation with the most room to spare**, and the solve starts from there
— so a long bar drawn horizontally against a tall sheet is stood upright instead of being reported
unplaced. If no orientation fits any sheet, the part is left as drawn and handled the way it always was.

It picks the roomiest orientation rather than the smallest rotation on purpose: the smallest rotation that
fits is, by definition, the one where the part only just stops overflowing, so it ends up touching the
sheet limit with no clearance and the nester still cannot place it.

When the pass rotates anything it says so on the Rhino command line
(`[pre-orient] 1 of 3 part group(s) rotated to fit the sheet: #0=-60.45deg`). If you see no such line, no
part needed re-orienting.

This runs once, per distinct part (not per copy), and is a no-op for input that already fits.
