# LFAM 1 cell calibration — 2026-07-30

Session notes for branch `fix/lfam1-cell-calibration`. Written for whoever reviews this
before it lands.

## The problem

With the real nozzle physically touching the real bed corner, MassiveSlicer drew the arm
**161 mm away horizontally and 10.4 mm high**. Nobody had noticed because you'd only ever
see it by parking the tip on the corner and looking — exports were unaffected, so prints
landed where the slicer showed them.

## What was measured

`cal-check` (new, this branch) compares where the app *draws* the nozzle tip against where
the controller says it is, both in BASE-frame mm. It needs no touch-off and no known
reference point — it's comparing two independent forward-kinematics answers driven by the
same joint angles.

74 samples taken passively during a live print: 86° of A1, 2.1 m of rail travel, TCP
spanning ~1.4 m, wrist C from 45° to 119°.

```
errX mean -130.90  sd 0.94
errY mean  -92.18  sd 1.25
errZ mean  +10.40  sd 0.68
correlation with E1 / A1 / TCP position: all <= 0.28 (noise)
```

Constant across the whole workspace ⇒ a pure frame offset. Not the rail, not the tool TCP,
not the robot GLB's joint kinematics. The drawn arm simply doesn't sit where the cell says
ROBROOT is.

## The fix

New optional `robot.modelOffset`, applied to the robot scene node **only**:

```json
"robot": { "modelOffset": { "x": 131.03, "y": 91.86, "z": -10.1 } }
```

Two readers, both robot scene placement — `CellSceneLoader` (initial) and `ViewportView`'s
`_robotHomePos` (required, or the rail rewrites the transform every frame and undoes it).
`BedCellConfig.BaseMarkerWorld` and `KrlExporter` stay anchored to
`robot.worldPosition + bed.baseData`.

### Why not just move the bed

That was tried first and rejected. `bed.origin.z` doubles as the plane export measures print
heights from (`SceneRenderer.BedZ` → `SliceBedWorldZ`, `exported_Z = world.Z - origin.z`).
Raising the bed 10 mm to fix the height would make every already-saved workspace command its
first layer 10 mm lower — the nozzle into the plate, not a shifted part. It would also have
moved the bed 161 mm, requiring every part in every existing workspace to be re-placed.

### Export safety

Structural, not incidental:

- `ModelWorldPosition` has exactly two readers, both robot scene placement
- `KrlExporter` is untouched
- `lfam1.json`'s `bed`, `worldPosition`, `tools` and `robotRail` blocks all compare **equal
  to main**

No exported coordinate can move. **Existing `.mass` workspaces are unaffected** — the bed
didn't move, so parts sit where they always did and export identically.

One behaviour does change, in the correcting direction: reachability validation and IK
dragging use the *drawn* robot position, so they now solve against the corrected arm instead
of one 161 mm from reality. Old workspaces may show different reachability results; the new
ones are the trustworthy ones.

## Verification

Live, against a running print, all three axes:

```
ERROR (app - controller) = ( 0.3, -0.1,  0.9) mm
                           ( 0.5, -0.8,  0.0) mm
                           ( 0.9, -0.9,  0.0) mm
                           (-0.9, -0.1,  0.8) mm
```

A later 12-sample set gave X +1.0, Y −0.8, Z 0.0 mean. Occasional larger single-axis
readings are sampling skew between the render state and the controller poll while the arm
moves at speed — identifiable because the app-side value jumps while its neighbours don't.

Two independent physical measurements, neither involving the software:

| | tape | controller |
|---|---|---|
| touch-off | nozzle on the bed corner | BASE `(-12.4, 0.4, 0.4)` ⇒ bed corner *is* BASE zero |
| print height | ~10.5″ (266.7 mm) | 271.5 mm |
| print height | just under 13.5″ (~342 mm) | 343.5 mm |

Observed layer height that run: 9.0 mm.

Suite: 545 passed, 8 failed — the same 8 as main, no regressions.

## How to re-verify

Build the branch, open LFAM 1, sync the robot, run `cal-check`.

**Check the first two lines before anything else:**

```
[cal-check] cell 'LFAM 1'  robroot=(0.0, 0.0, 500.0)  modelOffset=(131.03, 91.86, -10.10)
[cal-check] cells dir: <path>
```

If `modelOffset` says `none`, you are loading a different copy of the cell JSON than the one
you just pulled and nothing else you see means anything. Resolution order is
`MASSIVE_SLICER_CELLS` → the NAS → your build output (see `CellPaths`). On Windows on the
shop LAN with no env var set you will get **the NAS copy**.

Then jog anywhere and the error should read under ~1 mm on all three axes.

## Two things found along the way, not fixed here

**`bed.origin` / `bed.baseData` were clobbered on 2026-07-13** by `2774455` — five lines
inside the TreeSupport landing, described in its body as "LFAM1 bed BASE align". That's why
`CellLoaderTest.Lfam1_bed_and_tool_match_controller_config_dat` has been red since: it
asserts `baseData` equals the controller's `config.dat` value `(1496.36047, -577.892273)`.
Still red, unchanged by this branch. Harmless for prints — with `origin == baseData` the
value cancels out of export — but the field no longer means what it says.

**The NAS cell copy is stale and appears to have no readers.**
`\\192.168.0.191\MassiveFILES\Research\LFAM\MassiveSLICER V2\assets\cells` forked from git
around 2026-06-28. Its file mtimes are recent because the app writes back to whatever copy
it loaded (Save View, dev-mode bed moves) — don't read "newer timestamp" as "newer data".
macOS can't resolve the UNC path at all, so Mac users always fall through to their build's
cells. It's a live grenade for any Windows machine on the LAN without an override.

## Session log

1. Pulled the app's state over the local bridge, read `lfam1.json` and the live controller pose, built `cal-check`, fixed a stale-readout bug on rail moves, and measured the gap at 161 mm.
2. Diffed both clones against `origin/main` (both clean), found the NAS copy differs, traced the regression to `2774455`, and matched the cut-plane reading to the Z error.
3. Established the env-var override's origin, that the NAS is a diverging live copy rather than a stale one, and that no bad cell data was ever pushed.
4. Laid out the four data sources — live arm data comes off the controller identically for everyone; only the cell JSON differs.
5. Corrected that the cells-directory line is ~4th not 1st and only logs at startup; offered the env-var check instead.
6. Read a Mac slicing station's log: macOS can't resolve the NAS UNC path, so every Mac uses its own build's cells, and theirs matched.
7. Confirmed the LFAM 1 slicing station is on current git, wrote a first fix by moving the bed, verified 161 mm → under 1 mm.
8. Branched off main 508 and committed in two parts; a red unit test revealed `baseData` should hold the controller's real value.
9. Verified the branch is off clean main with nothing committed to the 2D-support work; noted local main is 3 commits behind origin, dry-run merge clean.
10. Found the bed-move approach unsafe for height and proposed a render-only fix instead.
11. Rewrote the explanation in plain language.
12. Retracted an unverified "robot copied from LFAM 2" root-cause guess and gave the real per-axis numbers.
13. Confirmed the bed-top-is-BASE-zero model against the code comment that defines it.
14. Built `robot.modelOffset`, reverted the bed move, verified.
15. Cross-checked a tape reading against the controller — which caught that Z had been anchored on one sample over 73; corrected it and Z went to 0.0.
16. Made `cal-check` echo the cells directory and `modelOffset` so a reviewer can't unknowingly test the wrong config.
17. Rewrote four commits into two, splitting `ViewportView` by hunk, and proved the result byte-identical to before the cleanup.
18. Took 12 fresh samples (X +1.0, Y −0.8, Z 0.0); confirmed no cell defines Live I/O signals, so that panel adds nothing beyond the pose feed.
19. Matched a second tape reading to the controller and the app, closing the chain from tape to controller to screen.
