# OpenNest2

Nests parts onto sheets with the no-fit-polygon genetic solver, keeping each part's attributes. Feed it the Sheets and Geometry components.

## Example

**Files to download:**

- [⬇ opennest2.ghx](files/opennest2/opennest2.ghx)

![opennest2 — opennest2](img/opennest2/opennest2.png)

![opennest2 — opennest2_screenshot](img/opennest2/opennest2_screenshot.png)

## Inputs

| Parameter | Type | Access | Description |
| --- | --- | --- | --- |
| **Sheets** | Generic | item | From OpenNest tab, use component Sheets. |
| **Geometry** | Generic | item | From OpenNest tab, use component Geometry. |
| **Options** | Text | list | Optional list of "key value" option strings — wire the **NestOptions** component here. Each line overrides the matching on-canvas option row below; unwired keys keep their on-canvas value. |
| **Iterations** | Integer | item | GA generations to evolve. Each generation evaluates the whole population and keeps the best, so the result improves over generations. ~10-40 typical (a live orange preview tightens as it runs; press ESC to stop and keep the best). Pair with the 'population' option (default 10). _Default: 10._ |
| **Run** | Boolean | item | Wire a Boolean Toggle. TRUE = solve now and re-solve when an input changes (background thread, live preview); FALSE = hold the last result. ESC also stops a running solve. _Default: false._ |

## Outputs

| Parameter | Type | Description |
| --- | --- | --- |
| **Sheets** | Curve | Polylines representing sheets. |
| **Borders** | Curve | Placed part outline curves. |
| **All Geo** (All Geometry) | Geometry | All placed geometry, grouped per sheet. |
| **Transforms** | Transform | Move/rotate transform placing each part. |
| **Sheet Id** | Integer | Sheet index each part landed on. |
| **Sheet Txt** | Curve | Sheet-number labels as text curves. |
| **Attributes** | Geometry | Attribute geometry carried with each part. |

## Options

On-canvas settings shown on the component body (zoom in on the component to reveal the widgets, or right-click). They tune *how* the genetic solver packs; the three input ports above are *what* it packs.

| Option | Type | Default | Range / Choices | Description |
| --- | --- | --- | --- | --- |
| **Rotations** | Number | 4 | 1 – 3600 | Orientations each part may try (360°/n). Default 4 = faster; raise it (e.g. 8) for tighter packing. |
| **Packing** | Choice | Gravity | Box · Gravity · Squeeze · Bottom Left | Placement strategy. **Bottom Left** packs each part into the lowest-then-leftmost feasible spot. |
| **Seed** | Number | 30 | 0 – 100000 | Random seed (same seed = same result). |
| **Mutation** | Number | 10 | 0 – 100 | GA mutation rate. |
| **Population** | Number | 10 | 1 – 100000 | GA population size — candidates evaluated per generation. |
| **All Rotations** | Choice | On | Off · On | Try every orientation per part for the tightest packing. Capped at 8 orientations so a large **Rotations** value can't hang the solver. |
| **Element Holes** | Choice | Fill | Off · Fill | Nest smaller parts **inside** larger parts' holes. |
| **Fit Orientation** | Choice | On | Off · On | Pre-rotate any part whose bounding box does not fit a sheet as drawn into an orientation that does — preferring a **Rotations** step, using an off-step angle only when no step fits. Parts that already fit are never touched. |
| **Sheet Select** | Choice | List Order | List Order · Min Area · Min Price | How each new sheet is chosen when the Sheets component has several sheet sets. **List Order** = as laid out (unchanged). **Min Area** = the set that is filled best, for the least total sheet area. **Min Price** = the set with the lowest price per placed part area, for the lowest total price (needs a **Price** on every sheet set). The last sheet's scrap counts; each set's **Quantity** limits it. Also on OpenNest2 Batch. See [sheet selection](../sheet_selection.md). |
| **Sheet Font** | Text | `MecSoft_Font-1 1` | — | Sheet-number label: font name + text size. |

## Initial orientation

Controlled by the **Fit Orientation** option (default **On**).

Before the solve starts, every part is checked against the sheet dimensions:

1. **It already fits as drawn** — left exactly as you drew it. Nothing happens. This is the normal case.
2. **It fits at one of the Rotations steps** (360°/n) — the part is turned onto that step, the roomiest one
   if several work. The solve still only ever sees orientations your **Rotations** setting already allowed.
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
