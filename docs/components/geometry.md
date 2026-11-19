# Geometry

Prepares parts for nesting: separates outlines (with holes) from attached attributes; optional simplify, convex hull and copies.

## Example

**Files to download:**

- [⬇ geometry.ghx](files/geometry/geometry.ghx)

![geometry — geometry](img/geometry/geometry.png)

![geometry — geometry_simplification](img/geometry/geometry_simplification.png)

![geometry — geometry_screenshot](img/geometry/geometry_screenshot.png)

![geometry — geometry_simplification_screenshot](img/geometry/geometry_simplification_screenshot.png)

## Inputs

| Parameter | Type | Access | Description |
| --- | --- | --- | --- |
| **Outlines** | Geometry | tree | Closed curves OR planar surfaces (cast per type internally).<br>If geometries have holes, create a data-tree first. <br>Place each element into indivdual branch. <br>Otherwise the algoritm will try to order polylines by checking possible holes. |
| **Simplify** | Number | item | segment divisions <br>0 = KEEP ALL vertices (no simplification - best for nesting fine outlines) <br>x>0 divides curved segments every x units: one point per x of curve length, with no upper cap (smaller x = more vertices, tighter fit, slower nesting); near-straight joins are merged only while the merged edge stays within 10x the model tolerance <br>x<0 max 3 points per sub-segment (merges colinear within 10deg)<br>Right-click > **Simplify: Max deviation**: x>0 becomes the maximum deviation and the nesting outline contains the part (see [Max deviation mode](#max-deviation-mode)) _Default: -100._ |
| **Hull** | Boolean | item | Replace each outline with its convex hull.<br>Right-click > **Hull: Auto**: only parts *without* a concave pocket of at least **Auto min pocket (%)** of their area (set in the same menu, default 5) are hulled; the others keep their outline so parts can nest into the pocket (see [Hull: Auto](#hull-auto)). _Default: true, with Hull: Auto on (new components since 2.89.11)._ |
| **Copies** | Integer | list | Number of copies per part |
| **Offset** | Number | item | Clearance offset for NESTING only (model units; 0 = OFF, fast).<br>Parts: outer grows / holes shrink so placed parts keep this gap.<br>The ORIGINAL curves are still what get placed/output.<br>Keeps every vertex of the nesting outline (only exactly colinear points are merged). _Default: 0._ |
| **Rotations** | Integer | list | OPTIONAL per-part rotation constraint (one value per part, repeats like Copies).<br>Empty / 0 = part inherits the solver's global Rotations setting (default).<br>N > 0 = THIS part may only use N orientations (360/N degree steps).<br>1 = fixed, no rotation (e.g. grain direction).<br>Lets rectangular parts stay at 4 orientations while freeform parts rotate freely in ONE nest.<br>Works with both OpenNest2 and OpenNestCollision. |
| **Attributes** | Geometry | tree | Additional geometry: points, lines, surfaces, meshes... <br>Use data-tree, one list of additional geometry per branch.. |

## Outputs

| Parameter | Type | Description |
| --- | --- | --- |
| **Geometry** | Generic | Prepared parts ready for nesting |
| **Borders** | Curve | Outline border curves per part |

## Simplify > 0 in detail (Distance mode, default)

Simplify only shapes the internal **nesting outline** (what the solver collides on, shown on **Borders**). The
placed/output geometry is always the original curve.

- **Division by distance, no cap.** Every curved segment gets one point per x of its length (rounded down, at
  least 2 divisions per segment), with no upper limit. Smaller x = more vertices, tighter fit, slower nesting.
- **Corners are kept.** Each segment is divided on its own, so the corners between segments are always vertices,
  including on curves made of only 1–3 segments (triangles, D-shapes).
- **Chords sit slightly inside convex arcs.** The points lie ON the curve, so each chord cuts into a convex arc
  by about x² / (8 · radius). For example, x = 200 on a radius-9250 arc gives about 0.54. Set **Offset** at
  least that large if parts must never touch the true outline.
- **Near-straight joins.** A join is merged only when it turns less than 1.5 × the model angle tolerance
  **and** the merged edge stays within 10 × the model absolute tolerance of the points it replaces. Long lines
  meeting slightly off straight therefore keep their corner instead of being pulled inward. Division points are
  never merged.
- **Hull and Offset keep the divisions.** Hull is built from the divided outline, and Offset only drops exactly
  colinear points, so the vertex count you set with Simplify carries through both.

## Max deviation mode

Right-click the component and tick **Simplify: Max deviation (contains original)**. The component then shows
*Max deviation* under its name, and the setting is saved with the file. Simplify x > 0 now means **ε, the
maximum deviation** from the original curve, in model units. Simplify 0 and negative values behave as before.

Two guarantees for every part:

1. **Containment.** The nesting outline contains the material: outer loops are never smaller than the original,
   holes are never larger. Simplifying can therefore never cause an overlap, even at Offset 0.
2. **Tightness.** Every vertex of the nesting outline is within ε of the original curve.

How the outline is built:

- **Convex parts** (bending around the material): vertices sit on the curve's tangents, just outside it. A
  small convex fillet becomes the sharp corner.
- **Concave parts** (bending away from the material): vertices sit on the curve, so the chords stay outside the
  material. A small concave fillet becomes a single chord.
- **Arcs** are divided in closed form, so the point count follows the curvature: a flat, large-radius arc gets
  few points and a tight fillet gets more. For a radius-9250 arc and ε = 0.1, that is about one point every 86.
- **Freeform curves** are split where they change bending direction and refined until each piece is within ε.
- **Merging** only removes a vertex when that moves the outline away from the material and the result stays
  within ε. Vertices on the material side are never removed.
- **Hull** (outer loops only) is taken from this containing outline, so it contains the part too. Holes keep
  their own outline.
- **Offset** adds clearance on top, as usual.

Bigger ε = fewer vertices, faster nesting, looser fit. Smaller ε = more vertices, slower nesting, tighter fit.

## Hull: Auto

A convex hull nests fast (few vertices) but wastes a part's concave pockets: nothing can go into the dent of an
L or a U. With **Hull = True** and right-click > **Hull: Auto** checked, the component decides per part:

- A **pocket** is the region between one hull edge and the stretch of outline it bridges (an L has one, a comb
  one per gap).
- A part whose **largest** pocket is at least **Auto min pocket (%)** of the area inside its outline keeps its
  outline; every other part is hulled as with Hull alone. The largest pocket counts, not their sum, so a part
  with only small notches is still hulled.

| Part | Largest pocket | At 5% (default) |
|---|---|---|
| Convex (rectangle, disc) | 0 | hulled (its hull is its outline, so nothing changes) |
| 100×100 with a 10×10 edge notch | 1% | hulled |
| 100×100 L missing a 60×60 corner triangle | 28% | keeps its outline |

The pockets are measured on the outer loop after Simplify (in Max deviation mode, on the containing outline).
Holes do not count; a hulled part's holes are treated as with Hull alone. The component message shows
`Hull auto 5%` and a remark says how many parts were hulled and how many kept their outline. The threshold is a
share of each part's own area, so on a large part a pocket under 5% can still hold a small part: lower it for
jobs with small parts. Hull = False ignores the setting.

**Default.** A new Geometry or Geometry (Surfaces) component starts with **Hull = True** and **Hull: Auto** on, and a
new Geometry (Rhino) with Simplify `[0, 2]`. Both settings are saved with the component, so files saved before
2.89.11.0 keep what they had (Auto off, and their own Hull value). Parts that keep their concave outline nest on
their full outline rather than a few hull vertices, so a job of many concave, curved parts nests slower than with
Auto off; uncheck it when speed matters more than using the pockets.

!!! note
    Grasshopper 2 Geometry and Geometry (Surfaces) have no Hull: Auto yet: their Hull input is on/off only.
    Geometry (Rhino), on both Grasshopper 1 and 2, takes it in its Simplify list: hull `2` = Auto, and an optional
    fourth value is the pocket %: `[divisions, 2, mode, 5]`.

!!! note
    Grasshopper 2: there is no right-click toggle. Pass the mode as a second Simplify value: `[ε, 1]`.
    Geometry (Rhino) takes it as the third value: `[ε, hull, 1]`. Upgrading a GH1 file to GH2 does not carry
    the toggle over; add the second value by hand.
