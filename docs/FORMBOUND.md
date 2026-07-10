# Formbound Bridge — design and implementation reference

*Continuous perimeter-anchored support fingers for LFAM. Written so another
engineer (or AI) can understand the system end-to-end and re-implement it.
Display name "Formbound Bridge"; internal identifiers still say "Lightning"
(`InfillPattern.LightningBridge`) for historical reasons.*

Code lives under `src/MassiveSlicer.Core/Slicing/Lightning/`:
`LightningPlan.cs` (data model), `LightningPlanner.cs` (top-down planning +
region normalization), `LightningGenerator.cs` (per-layer realization),
`MeshInsideTester.cs` (mesh-truth oracle).

---

## 1. The problem

Large-format additive (LFAM) runs a pellet extruder: stopping and restarting
extrusion is slow and oozes, so **travel moves are nearly forbidden** — a good
layer is one unbroken extrusion. The parts are printed as hollow shells
(infill replaces shells entirely; the perimeter bead IS the wall), so when a
part closes inward as it climbs — domes, closing vessels, pockets, flanges —
the upper layers' perimeter would print over empty interior air.

Cura-style "lightning infill" solves the support-material problem (sparse
trees that only grow where surfaces above need them) but its branches are
dead ends: every branch needs a travel to escape. Unusable here.

**Formbound Bridge** keeps lightning's demand-driven sparseness but makes
every support branch a *detour spliced into the perimeter loop*: the path
breaks off the perimeter, runs inward as a thin two-wall "finger" (out one
side, around the tip, back the other side one bead apart), rejoins, and
continues — one continuous extrusion, zero travels added.

## 2. The core trick: fingers as boolean slits

Do **not** splice detours into the perimeter walk by hand. Per layer:

1. Each finger tree is a set of open centerline polylines (root on the
   boundary → tip under the demand).
2. `Clipper.InflatePaths(centerlines, beadWidth/2, JoinType.Round,
   EndType.Round)` turns the whole tree into one closed **slit polygon**,
   whose boundary is exactly the hairpin out-and-back wall pair with a
   rounded tip. A tree with branches yields one slit whose boundary is the
   depth-first traversal around all branches — a detour within a detour.
3. The root end is extended one bead past the region boundary.
4. `Clipper.Difference(fillRegion, slit)` notches the perimeter.
5. The result polygons' boundaries **are** the perimeter-with-finger-detours
   paths. Emit each as a plain closed extrude loop. Continuity is inherited
   for free; finger–finger overlap, perimeter crossings, holes, islands and
   tip loops are all solved Clipper booleans instead of bespoke geometry.
6. Optional tip discs ("support pads", `LightningTipLoopRadiusMm`) are
   unioned onto the slit before the difference — the boundary loops around
   the disc.

The extruded bead is centered on the slit boundary, so a plain tip already
covers a disc of ≈ one beadWidth around the demand point.

**External fins** (`LightningExteriorOverhangs`): for *outward* flares the
same slit is instead `Union`ed onto the region — a sacrificial bump outside
the part that the boundary detours around. External fins retract at the
physical bead-on-bead limit (`max(maxStep, bead/2)` per layer) so they peel
off the wall right under the overhang instead of trailing to the bed.

## 3. Architecture: three passes

Both `PlanarSlicer`, `AngledPlanarSlicer.Slice` and
`AngledPlanarSlicer.SliceMultiPlanar` share the same shape:

- **Pass A** — compute and cache every layer's inset contours
  (`ComputeInsetContours`). The cache is reused verbatim in pass C so the
  plan and the emitted geometry can never drift apart.
- **Pass B** — `LightningPlanner.Build(fillPolysPerLayer, layerHeights,
  settings, frames?, solidAt?)` walks the whole stack **top-down** and
  produces a `LightningPlan` (one `LightningLayerPlan` per layer).
- **Pass C** — the normal per-layer loop; `BuildLayer` consumes the cached
  contours plus that layer's plan and calls
  `LightningGenerator.EmitLightning(...)` in the infill block, **bottom-up**
  in print order.

Planned top-down, printed bottom-up: inheritance with tip retraction going
*down* the stack is exactly "a finger grows at most one step per layer going
*up*", which is what makes every finger printable.

### Data model (`LightningPlan.cs`)

```
LightningPlan      { LightningLayerPlan[] Layers }         // shared DroppedTrees set
LightningLayerPlan { List<LightningTree> Trees; HashSet<int> DroppedTrees }
LightningTree      { int Id; Vector2 Anchor; bool External; List<LightningBranch> Branches }
LightningBranch    { List<Vector2> Centerline; int ParentBranch; int ParentNode }
```

- All points are **plane-local 2D** (world XY for planar; the (u,v) frame for
  angled/multi-planar).
- `Id` is a stable **lineage id** across the per-layer clones. `DroppedTrees`
  is ONE set shared by all layers: emission runs bottom-up, so when the
  generator drops a tree at some layer, the same lineage must vanish from
  every layer above — otherwise its inherited fingers print in mid-air over
  the gap.
- `Branches[0]` is the trunk (starts at `Anchor`); later branches attach to a
  `(ParentBranch, ParentNode)` junction.

## 4. Region normalization — `ToPathsD` (hard-won on real CAD)

Everything operates on a normalized region: `ToPathsD(fillPolys, beadWidth)`.
Real-world exports are frequently **double-shelled** (every surface
duplicated microns apart) with corrupted windings, and tangent cuts split
contours into half-loops. The sequence that survives all of it:

1. **Twin dedupe** — sort candidate contours largest-first; a contour whose
   *every vertex* lies on an already-kept contour's curve within 0.15 mm
   (`LiesOnCurve`, with a bbox prefilter) is the duplicate shell's copy —
   regardless of how its chain was split, reversed, or re-stitched. Largest
   first so a full twin absorbs the split pieces of its counterpart.
2. **EvenOdd (parity) union** — winding-agnostic, so corrupted orientations
   don't matter, and doubly-covered areas cancel. This is the physically
   right reading of messy slices: a hollow section whose chains split into
   two half-loops that each enclose the shared cavity gets the cavity as
   their overlap → a hole. (A NonZero union would fill it and erase its
   walls.)
3. **Sub-bead hole fuse** — parity punches a tiny hole wherever tangent-band
   junk overlaps the wall; a bead physically fuses any opening smaller than
   a couple of bead widths, so holes with area < (2·bead)² are removed.
   Material is only ever *added* by this step.

**Do not retry** (measured catastrophic on the real fuselage): mesh-triangle
dedupe (524 mm of wall lost), segment dedupe (672 mm), constructive nesting
with coverage/erosion discriminators, NonZero union, and *orphan-hole
filtering by orientation/containment* — parity-composed layers (half-loops)
make both winding and containment meaningless per-contour.

## 5. The planner — top-down demand propagation

Constants: `bead = max(BeadWidth, 0.1)`;
`maxStep(i) = min(layerHeight_i · tan(LightningOverhangDeg), bead/2)`;
`spacing = LightningBranchSpacingMm > 0 ? that : 4·bead`;
`supportRadius = maxStep(i+1) + bead/2`.

Per layer `i` from `n−2` down to `0`:

0. `region = regions[i]`, `core = erode(region, bead)` (fingers must stay a
   bead inside so slit walls never poke through the perimeter). Empty region
   or core → every tree above is orphaned (its column has no continuation —
   silently skipping would leave it floating).
   Anchor paths are the boundary classes fingers may root on
   (`LightningAnchorInterior` = holes/inner walls, notch hidden inside;
   `LightningAnchorExterior` = outer perimeter, notch visible outside).

1. **Inherit** every tree from layer `i+1`:
   - Clone; **frame-remap** all points if the stack rotates (see §7).
   - *Dangling check first*: re-anchor to the closest boundary point; if the
     anchor moved more than `max(4·bead, 3·maxStep)`, the wall it stood on is
     gone — retire the lineage (never teleport a finger).
   - `RetractLeafTips(tree, maxStep(i+1))` — leaves-first, arc-length
     retraction; emptied branches are removed and their children re-root.
     A finger of length L therefore exists on ≈ L/maxStep layers below its
     demand, forming the support column.
   - Clamp nodes into the region/core (`ClampInside`); nodes that fell
     outside are pulled to the core boundary, or the branch is trimmed if
     that needs more than this layer's lateral budget.
   - Reject trunks whose centerline now crosses a void
     (`SegmentInsideRegion` — a chord over a ring's hole is unrealizable as
     a slit) → orphan the lineage.

2. **New demand** — sample every path of `regions[i+1]` every `spacing/4`
   (skip paths with area < 4·bead² — unprintable specks). A sample is
   *unsupported* when its distance to layer `i`'s boundary curve exceeds
   `supportRadius` (support is measured from the **boundary curve**, not the
   region area — the perimeter bead is the only material), AND it is inside
   region `i` (inward-shrinking arc) or `LightningExteriorOverhangs` is on
   (outward flare), AND no existing centerline is already within
   `supportRadius`.
   - Distribute tips **evenly along each contiguous unsupported run**
     (`CircularRuns`) — greedy first-come dedupe leaves worst-case 2×spacing
     holes at the run wrap-around.
   - **Mesh-truth veto** (§8): pull a probe inside the demanding solid and
     ask the mesh; phantom parity islands fail here.
   - Interior tips are pulled onto/inside `core`; the anchor is the closest
     allowed boundary point; reject if the wall already covers it
     (anchor–tip < bead) or the segment crosses a void.
   - Each accepted tip becomes a new single-branch tree
     (`[anchor, tip]`). *Tree merging* (rooting on an existing centerline as
     a child branch) is implemented but **disabled**: a trunk cannot retract
     while any child lives, so chained branches outlive their support depth
     and reach the bed. Re-enabling needs depth-aware retraction (retract
     the longest root-to-leaf arc, not each leaf independently).

3. **Straightening** — nudge interior nodes toward the root–tip chord,
   budgeted by `maxStep(i)` so the layer above still rests within one step
   of the new position; never leave the core.

Finally, orphaned lineages are removed from **every** layer.

## 6. The generator — per-layer realization (bottom-up)

`EmitLightning(fillPolys, layerPlan, z, layer, beadWidth, tipLoopRadius,
project?)`:

1. `region = ToPathsD(fillPolys, beadWidth)`; return if empty.
2. `boundaryBand = region − erode(region, 0.6·bead)` — a thin band just
   inside the boundary used by the bite guard.
3. **CutTrees** — for each tree (skipping `DroppedTrees`):
   - `BuildTreeSlit`: inflate centerlines by bead/2 (Round/Round); skip
     degenerate branches (< 0.1·bead arc) but keep short stubs — they are
     the first layers of a growing finger and *must* print; extend the root
     past the boundary; union tip discs if `tipLoopRadius > 0`.
   - **Bite guard**: a healthy slit crosses `boundaryBand` exactly once (its
     mouth). Two crossings = punching through a nearby thin wall (narrow
     inlet channels) → drop the lineage. With tip loops, test the disc-less
     body so a big pad grazing the band doesn't false-positive.
   - Interior: `Difference(result, slit)`; **neck guard** — if outer count
     rose, the slit cut across a neck (extra island = travels) → drop.
     External: `Union(result, slit)`; if outer count *fell*, the fin bridged
     to another island → drop. Empty result → drop.
   - **Sliver-merge closing**: converging inherited fingers can leave a
     sliver of region thinner than one bead between their slits (two walls
     printed nearly on top of each other = gross over-extrusion).
     Morphologically close the cut area — `cut = region − result`, dilate +
     erode by 0.55·bead (Round) — and subtract the closed cut from *result*
     (idempotent for the notches; external fins survive). Remove lens
     fragments < bead²; adopt only if the outer topology is unchanged. Gaps
     bounded by the part's real exterior only dilate from one side and
     survive untouched.
4. **Perimeter-hold guard** — the invariant that the wall always wins:
   re-run up to 3 times: `PerimeterBreachTrees` builds a boundary band
   (region − erode(0.5·bead)) and a *tube* around the emitted walls
   (result loops re-closed as open polylines, inflated 0.9·bead,
   Round/Round); `uncovered = band − tube`. A legit finger mouth uncovers
   ≲ 2 beads of wall; any uncovered component with area > 3·bead·(0.5·bead)
   is a **breach** — the live trees whose slits intersect
   `inflate(breach, bead)` are added to `DroppedTrees` and the layer is
   re-cut. Crowded lineages merging into a wall-eating blob cannot be seen
   by any single-tree guard; this catches the aggregate.
5. **Single-bead wall recovery**: interior partitions modelled exactly one
   bead thick collapse to near-zero area under the half-bead inset and
   vanish from the region, but they are real geometry that shells mode
   draws. Any raw fill poly with `perimeter ≥ bead` and
   `area < perimeter·bead·0.25` that is not already covered by the result
   (`LiesOnCurve`, 0.6·bead) or by a previously recovered wall is emitted as
   a standalone loop.
6. **EmitLoops**: each result path becomes one closed extrude loop, started
   at the vertex nearest the running end; separate islands connect with a
   Travel exactly as shell mode does. Finger moves are tagged
   `ToolpathMove.IsLightning` for the renderer/exporter.

Because emission is bottom-up and `DroppedTrees` is shared, a drop at layer
k automatically silences that lineage on every layer above k.

## 7. Rotating frames (Multi-Planar) — the drift trap

Multi-Planar slicing rotates the cutting plane a little every layer. The
planner works in plane-local 2D, and the same *physical* point's (u,v)
coordinates drift by up to `layerH·tanθ + Δθ·lever` per layer — several mm
at 45°. Compared raw, that drift reads as phantom unsupported arcs along
whole edges, spawning a fresh finger row *every layer*; the crowded rows
merge into cut blobs that ate 40–60 mm of wall on 246/529 layers of the
first real part (Drone V52).

Fix: `Build(..., frames)` takes per-layer `(Origin, U, V)`; **everything
that crosses layers is remapped through world space** (lift with the source
frame, project into the target frame): inherited anchors + centerlines, and
demand samples. Constant-frame stacks pass `frames = null` (identity).

Two slicer-side preconditions make the pre-pass valid on rotating stacks:
the per-layer rotation clamp (`0.75·layerH/lever`) keeps consecutive planes
from crossing inside the part and bounds physical divergence under
`supportRadius`, and the march (H, θ, normal, planeD, origin, u, v) is
precomputed once and shared by pass A and pass C.

## 8. Phantom geometry and the mesh-truth oracle

A **grazing cut** over a pocket rim emits the rim curve *without the wall
that hosts it* (the wall collapsed at that tangency). 2D contour parity can
only read a lone closed curve as a **solid island** — a phantom that exists
for the 1–2 layers the tangency lasts. Each phantom then seeds a ladder of
support fingers for *dozens* of layers below geometry that doesn't exist
(finger length / maxStep ≈ 70 layers on the Drone).

Two plausible 2D fixes were tried and **disproved with measurements** —
keep these numbers, they kill entire solution families:

- *Orientation/containment orphan-hole filter in ToPathsD*: the fuselage's
  parity-composed half-loops are CW, unhosted, and REAL (dropping them lost
  187–756 mm of wall). Winding and containment are unreliable per-contour.
- *Persistence-of-solid-above veto*: real fuselage tangent-band demand
  moves **761 mm** within 3 layers (real but not "persistent"); the Drone
  phantom sits **0 mm** from real walls three layers up (phantom but
  "persistent"). The signal inverts exactly on the hard cases.

Only the mesh knows. `MeshInsideTester` answers "is this world point inside
the solid?":

- XY grid over all triangles (cell ≥ max(bboxSpan/192, 1 mm), ≤ 512²).
- Vertical-ray parity: count triangle crossings above the point (2D
  barycentric point-in-triangle in XY, then the z at that spot).
- **Crossing clustering** (0.2 mm): double-shelled exports produce twin
  surfaces microns apart → two crossings that must count as ONE surface, or
  every parity inverts. Sort crossings, merge runs closer than the
  tolerance, use cluster-count parity.
- Deterministic jitter (+0.0137, +0.0071 mm) keeps rays off shared
  triangle edges. Queries probe 0.4·layerHeight *below* the slicing plane
  (the demanding solid occupies the layer beneath; the grazing plane itself
  is ambiguous).

The planner consumes it as `solidAt(layerIndex, planeLocalPoint)`; each
slicer supplies the lambda that lifts plane-local → world for its frame. At
demand time the probe is pulled *inside* the demanding solid (2.5·bead,
falling back to 1·bead then 0.6·bead, staying `InsideRegion`) and vetoed if
the mesh says void. Phantom walls still print for their 1–2 layers (a
cosmetic stray bead); they just never grow support.

## 9. Settings, UI, integration

`SliceSettings`: `InfillPattern.LightningBridge`; `LightningOverhangDeg`
(default 30, clamp 5–80), `LightningBranchSpacingMm` (0 = auto 4·bead),
`LightningTipLoopRadiusMm` (0 = off), `LightningAnchorInterior` /
`LightningAnchorExterior` (default true), `LightningExteriorOverhangs`
(external sacrificial fins, default false).

UI shows "Formbound Bridge" (the legacy string "Lightning Bridge" still maps
to the same enum for old workspaces). Works under all three slicing methods;
Multi-Planar additionally passes `frames` (§7). Per-move `IsLightning`
tagging drives preview coloring; `HeightScale` (Multi-Planar wedge layers)
composes with everything transparently since fingers are ordinary moves.

A solid-ramp variant (**Formbound Buttress**, `InfillPattern
.FormboundButtress` — multi-bead ramp polygons with a spiral fill spliced in
at the mouth, `LightningRampMinBeads`, `LightningPreferInteriorMouths`) is
in development on the same plan/emission skeleton; `LightningPlanner
.IsFormboundPattern` gates the shared pre-pass.

## 10. Verification — tests and tooling

- `MSL_LIGHTNING_DUMP=<dir>` (env var): the generator dumps per-layer
  `FILL` / `REGION` / `RESULT` polylines (`gen_z<planeD>.txt`, tab-separated
  `TAG\tx,y;x,y;…`). Every field bug above was diagnosed by rendering these
  and computing REGION-boundary → RESULT-wall coverage ("worstGap").
- `LightningBridgeTest` — planner units (demand only under flares,
  retraction invariant, anchors on boundary), continuity (zero travels per
  island, `Moves[k].From == Moves[k−1].To`), the support invariant
  (spatial-hash: every finger midpoint within `maxStep + bead/2` of layer
  below material), double-shell dedupe, sliver merge,
  `MeshInsideTesterHandlesDoubleShelledExports`,
  `PhantomIslandDoesNotSeedFingerLadder` (synthetic 2-layer phantom + a
  persistent real ring as control).
- `MultiPlanarSlicerTest` — `FormboundBridgeGrowsFingersUnderMultiPlanar`
  (closing vessel), `AggressiveReversingStackKeepsThePerimeter` (reversing
  ±45° plane stack; per-layer coverage vs a pattern-None reference slice —
  this is the regression test for the frame-drift bug).
- `AngledFuselageLikeTest` — synthetic double-shelled hollow blimp (twin
  offsets 0.002–0.004 mm — realistic; larger offsets create unrealistic
  phantoms) with a diaphragm, run through an invariant gauntlet with
  pinned frontier bounds.
- `FuselageRealCheck` — the real NAS fuselage STL, lightning vs baseline
  coverage + fragmentation bounds. Skips when the share isn't mounted.

The test style that matters: compare the Formbound slice against a
pattern-None slice of identical settings (same layers), assert every
baseline wall midpoint has material nearby, and pin known artifacts with
frontier bounds rather than aspirational ones.

## 11. Manual control — the toolpath Edit menu (paint marks)

The Preview view has an Edit menu (pencil icon, left of the viewport) with four
tools plus Clear and Reslice. All of them write `PaintMark` records — WORLD-space
spheres `(Center, Radius, Kind)` stored in `SliceSettings.PaintMarks`, persisted
with the workspace, and therefore stable across re-slices:

- **Bridge brush / Line support** — `PaintMarkKind.Bridge`. The slicers project
  every Bridge mark onto each slicing plane
  (`ToolpathPaintFilter.ProjectBridgeMarks`) and the planner treats the projected
  points as *manual demand*: fingers grow beneath them with geometric sanity
  checks only — no spacing thinning, no mesh-oracle veto (the user explicitly
  asked), external fins allowed regardless of the setting.
- **Remove brush / Line remove** — `PaintMarkKind.Remove`.
  `ToolpathPaintFilter.ApplyRemovals` runs after each slice: extrude moves whose
  midpoint falls inside a Remove mark are deleted and each contiguous removed run
  is spliced with one travel. This is the manual override for stray geometry the
  automatic vetoes must leave alone (e.g. thin real ledges the mesh oracle
  confirms).
- Brushes: left-drag paints, Alt erases, right-click-drag horizontally resizes.
  Line tools: one click marks the whole picked contour (dabs laid along its
  length), Alt-click unmarks it.
- Realtime slicing is PAUSED while the Edit menu is open (`RealtimeSlicingPaused`
  + the pending-slice flag); collapsing the menu or pressing Reslice fires the
  deferred re-slice.
- Feedback overlay (`PaintOverlayRenderer`, GL_LINES): every mark renders as
  three orthogonal circles (cyan = Bridge, red = Remove), the bead under the
  cursor gets a brush-radius circle, and line tools highlight the contour they
  would pick before the click. All GL calls happen on the GL thread
  (`UpdatePaintOverlay` in the per-frame sync); pointer handlers only mutate
  state.
- Marks store RAW toolpath coordinates (the slicer's world space at slice time),
  picked from the rendered beads by screen-space projection — so the filter and
  planner compare like with like.

## 12. Known limits

- Junction/tangent planes still emit 1–2 layer phantom parity *walls*
  (bounded, cosmetic); only their bridging is suppressed.
- Tree merging disabled (see §5.2) — fingers under one long overhang stay
  independent hairpins instead of merging into trees.
- One or two single-bead stub fingers can survive directly under a real
  pocket rim (the mesh is genuinely solid there); they are load-bearing
  scale, not floating ladders.
