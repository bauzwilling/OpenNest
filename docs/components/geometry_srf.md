# Geometry (Surfaces)

Builds nesting parts from planar surfaces instead of curves; holes are read from the surface boundaries.

## Example

**Files to download:**

- [⬇ geometry_surface.ghx](files/geometry_srf/geometry_surface.ghx)

![geometry_srf — geometry_surface](img/geometry_srf/geometry_surface.png)

![geometry_srf — geometry_surface_screenshot](img/geometry_srf/geometry_surface_screenshot.png)

## Inputs

| Parameter | Type | Access | Description |
| --- | --- | --- | --- |
| **Surfaces** | Brep | list | Planar boundary surfaces.<br> If you want to nest multiple sheets partition surfaces into the data-tree. |
| **Simplify** | Number | item | segment divisions <br>0 takes only ends <br>x>0 divides curved segments every x units: one point per x of curve length, with no upper cap (smaller x = more vertices, tighter fit, slower nesting); near-straight joins are merged only while the merged edge stays within 10x the model tolerance (see [Geometry: Simplify > 0 in detail](geometry.md#simplify-0-in-detail-distance-mode-default)) <br>x<0 max 3 points per sub-segment)<br>Right-click > **Simplify: Max deviation**: x>0 becomes the maximum deviation and the nesting outline contains the part (see [Geometry: Max deviation mode](geometry.md#max-deviation-mode)) _Default: -100._ |
| **Hull** | Boolean | item | Use the convex hull of each simplified outline. _Default: false._ |
| **Copies** | Integer | list | Number of copies per part. |
| **Offset** | Number | item | Clearance offset for NESTING only (model units; 0 = OFF, fast).<br>Parts: outer grows / holes shrink so placed parts keep this gap.<br>The ORIGINAL curves are still what get placed/output.<br>Keeps every vertex of the nesting outline (only exactly colinear points are merged). _Default: 0._ |
| **Rotations** | Integer | list | OPTIONAL per-part rotation constraint (one value per part, repeats like Copies).<br>Empty / 0 = part inherits the solver's global Rotations setting (default).<br>N > 0 = THIS part may only use N orientations (360/N degree steps).<br>1 = fixed, no rotation (e.g. grain direction).<br>Lets rectangular parts stay at 4 orientations while freeform parts rotate freely in ONE nest. |
| **Attributes** | Geometry | list | Additional geometry: points, lines, surfaces, meshes... <br>Use data-tree, one list of additional geometry per branch.. |

## Outputs

| Parameter | Type | Description |
| --- | --- | --- |
| **Geometry** | Generic | Nesting parts ready for the solver. |
| **Borders** | Curve | Outline curves of each part. |
