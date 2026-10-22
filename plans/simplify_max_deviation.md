# Plan: "Max deviation" Simplify mode (containing, deviation-bounded)

Status: **phases 1 and 2 implemented in 2.89.7.0** (`nest_geo.contain_simplify`); phase 3 (push-out merge of
convex vertices) not started. Deviation from the design below: GH2 has no overridable component context menu in
its current SDK, so in GH2 the mode is the Simplify input's 2nd value (`[eps, 1]`) instead of a right-click
toggle; GH1 has the toggle. Geometry (Rhino) takes `[eps, hull, 1]` in both. The GH1 -> GH2 upgrader does not
carry the toggle over. Applies to the Geometry components (GH1 + GH2, Curves / Surfaces / Rhino), which all
build their nesting boundary through `nest_geo.curve_to_polyline_unfillet`.

## Goal

A Simplify mode where the value is a **maximum deviation ε** (model units), with two guarantees for every part:

1. **Containment** — the nesting polygon contains the original material (outer loops grow, holes shrink), so
   simplification can never cause an overlap, even at Offset 0 with Exact NFP.
2. **Tightness** — every point of the nesting polygon is within ε of the original curve.

Vertex placement provides (1); ε provides (2) and sets the trade-off: bigger ε = fewer vertices, faster, looser;
smaller ε = more vertices, slower, tighter.

## Current state (2.89.6) and what it does not guarantee

`Simplify > 0` = divide curved segments every x units, points ON the curve, uncapped. Then:

- Chords sag **into** convex material by about x² / (8r) (r = 9250, x = 200 → 0.54). Not contained.
- Near-straight joins merge when the turn is < 1.5 × model angle tolerance **and** the merged edge stays within
  10 × model tolerance of the replaced vertices (`merge_colinear_joins`). Division points are never merged. This
  bounds the merge error, but the merge can still move an edge slightly into the material.
- Short fillets: convex → sharp corner (contained); concave → corner removes material (not contained).
- Hull is built from on-curve points, so it is undersized along arcs. It is applied per loop before grouping, so
  holes are hulled too, which makes the holes bigger (the unsafe direction).

## Design

### 1. UI: a mode, not a new meaning for x > 0

Keep the Simplify value and add a component menu toggle: **Distance** (default, today) / **Max deviation
(contains original)**. Persist it with the component (GH1 `Write`/`Read`, GH2 `IWriter`/`IReader`). A menu
toggle adds no fixed input, so the Attributes port indices and the upgraders are untouched. Existing files keep
Distance.

### 2. Pipeline position: a second pass after grouping

Containment direction depends on the material side, which is known only after `identify_groups` /
`hard_coded_input` has paired outers with holes. Mirror `offset_nesting_boundary`:

1. Grouping as today (on-curve polylines are good enough to classify loops).
2. `contain_simplify(eps)`: for each loop, rebuild `Item2` from the original curve `Item4`; `j > 0` = hole.
   Orientation is normalised in grouping, so the material is on a known side of every loop.
3. Hull (outer loops only), then Offset, as today.

### 3. Segment → polygon points

Classify every curved piece as **convex** (bulges away from the material, i.e. the material is on its inside)
or **concave**. Rule: **tangent vertices on convex pieces, on-curve chords on concave pieces.** Both keep the
material inside, with an outward error ≤ ε.

- **Lines:** their end points.
- **Arcs** (radius r, sweep φ):
  - Convex: θ = 2·acos(r / (r + ε)), capped at 90°; n = ceil(φ / θ); θ' = φ / n. Emit the arc start, then n
    vertices at angles (k + ½)·θ' at distance r / cos(θ'/2) from the centre, then the arc end. The start and end
    lie on the first and last tangents, so joins to neighbouring segments (smooth or kinked) stay outside the
    material.
  - Concave: θ = 2·acos(1 − ε / r); n = ceil(φ / θ); on-arc points.
  - Fillets need no special case: a small convex fillet gives n = 1, whose vertex IS the sharp corner; a small
    concave fillet gives one chord. The separate unfillet branch (`arc.Length > x`, `EndAngleDegrees <= 270`)
    goes away, which also fixes concave fillets losing material.
- **Freeform:** split at curvature-side changes (inflections) and kinks, so each piece is purely convex or
  concave. Convex piece: intersect the end tangents → vertex V (a convex piece turning < 180° lies inside the
  triangle start–V–end). If dist(V, curve) > ε or the turn > 90°, split at the mid-parameter and recurse.
  Concave piece: recurse on the chord until max(curve-to-chord distance) ≤ ε.
- **Pre-pass:** `Curve.Simplify(All, 10·tol, …)` can move the curve inward by up to 10·tol. In this mode, skip
  it, or push convex vertices out by that tolerance too.

### 4. Merging under the ε budget

The 2.89.6 merge (angle **and** 10·tol deviation, division points protected) is the interim rule. In this mode
the rule becomes containment-aware, and ε (not 10·tol) is the budget:

- Track each polygon edge's outward deviation `d_e` from the original curve (known from construction: the arc
  formulas, or the recursion's measured distance).
- **Concave (reflex) vertex V** between P and N: removing it moves the edge outward, into empty space.
  Allowed if `max(d_PV, d_VN) + dist(V, PN) ≤ ε`.
- **Convex vertex:** removing it moves the edge into the material, so it is **never dropped** in phase 1.
  Phase 3 option: a "push-out merge" that replaces P–V–N by the line parallel to PN through V, intersected with
  the neighbouring edges (P', N'), when the new vertices stay within ε.
- No protected-points list is needed: construction already emits the minimum count for ε, and the budget
  governs everything else.

### 5. Hull

Compute the hull from the containing polygon (a polygon that contains the curve has a hull that contains the
curve's hull), for **outer loops only**. Holes keep their (shrunk) containing polygon.

### 6. Offset

Unchanged and additive: Offset is extra clearance on top of a polygon that is already safe.

## Validation

Use the RhinoCode probe approach: run a read-only script in a running Rhino that loads a copy of the built DLL
with `Assembly.LoadFile` and calls the new pass on test curves. For each loop, check:

- **Containment:** sample the original curve densely; every sample is inside (outer) or outside (hole) the
  polygon, within a small numeric tolerance.
- **Deviation:** every polygon vertex and edge midpoint is within ε of the original curve.

Test cases:

- the `no_simplifying.dxf` part (r = 9250 arcs, 7.67° each; ε = 0.1 → about 15 pieces per arc)
- a part with concave fillets
- a freeform blob with inflections
- a part with a hole (direction flip)
- a full circle (one closed segment, so at least 4 vertices at ≤ 90°)
- a very small ε on a large radius (many vertices, uncapped; document rather than cap)

## Phasing

1. Lines + arcs, convex/concave rule, concave-only merging under ε, hull on outers only, menu toggle, docs.
   Covers the DXF-style line/arc parts.
2. Freeform segments (inflection split + recursion).
3. Optional push-out merge for convex vertices.

## Docs to update with the code

Component pages `docs/components/geometry.md`, `geometry_srf.md` and `geometry_rhino.md` (Simplify row + mode);
tooltips in GH1 `component_geo_grasshopper*.cs` and GH2 `GeometryComponent.cs` / `InputComponents.cs`.
