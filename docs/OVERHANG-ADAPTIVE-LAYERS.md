# Overhang-driven adaptive layer height — design

> Branch `feature/overhang-adaptive-layers`, cut off `main` @ `3abf863` ("main 544").
> **Designed 2026-08-06, NOT built.** Four decisions are still open — see the end.
> Companion to the flow fix that landed as main 544 (`LayerHeightFlowPostProcessor`).

---

## What we want

Jeff's requirement, in his words: *"We need the beads to kind of overlap with at least 50 %
sticking to the bead/layer under."* And: more overhang → more layers at less height; straight
sections → back to full-height layers and fewer of them.

Today's adaptive layer height cannot express that, because it is solving a different problem.

## Why the current criterion doesn't do it

`AdaptiveLayerHeights` implements the Wasserfall / Bubnik **surface-deviation (stairstepping)**
metric from OrcaSlicer / PrusaSlicer. It bounds how far the stair steps deviate from the true
surface — a **surface-finish** measure. It has no knowledge of bead width and no concept of how
much of a bead is supported by the one beneath it.

Surface finish and bead adhesion are different constraints, and on a 6 mm LFAM bead the adhesion
one is far more demanding.

### Measured failure that motivated this

Dragon column (`25-102`), PPGF on the HF head, bead 6 mm nominal (≈6.5 mm as printed),
nominal layer 3 mm, `AdaptiveQuality` 0.489, `MinLayerHeight` 1.0.

The print failed at **layer 29**. Measured overlap against the layer below:

| layer | wall angle | layer height chosen | overlap |
|---|---|---|---|
| L29 | 21.0° | 2.039 mm | 44 % |
| L30 | — | 1.839 mm | 41 % |
| L31 | — | 1.768 mm | 34 % |
| L32 | — | 1.752 mm | **30 %** |
| L33 | — | 1.753 mm | **30 %** |
| L34 | 17.8° | 1.784 mm | 34 % |

**21 of 618 layers were under the 50 % target.** It is a *run* of progressively worse layers, not
a single bad one — L29 is where it let go, not where it was worst.

The algorithm behaved correctly: allowed deviation was `1 + 0.489 × (3 − 1) = 1.98 mm`, which at a
21° wall yields ≈2.1 mm, and it chose 2.039. It answered the question it was asked.

### The interim workaround (works, but blunt)

Setting `AdaptiveQuality` to **0** collapses the allowed deviation to `MinLayerHeight`, so every
shallow region drops to the 1.0 mm floor. Re-measured: **0 of 353 layers below 50 % overlap**,
worst 59–61 %, the old failure zone at 99 %. Confirmed on the machine.

What it costs:

- **51 layers pinned at the 1.0 mm floor** (Zrel 24–206 mm) — no headroom left on that knob.
- Those layers command **12.65 % RPM**, far below the 50 % point the flow was calibrated at.
- It thins **every** shallow region, including ones adhesion never needed — pure wasted time.
- `AdaptiveQuality` is being abused as an overhang proxy instead of meaning surface finish.

---

## Proposed criterion

For a surface whose unit normal makes the usual decomposition, `FaceZ` **already stores what we
need**:

- `NCos` = |n·ẑ| = cos(α)
- `NSin` = √(nx² + ny²) = sin(α)

where **α is the wall angle from horizontal**. Therefore `tan(α) = NSin / NCos`, already in hand.

The horizontal step between consecutive layers is `Δr = h / tan(α)`. To keep at least
`targetOverlap` of a bead supported, we need `Δr ≤ (1 − targetOverlap) × beadWidth`, so:

```
h  ≤  (1 − targetOverlap) × beadWidth × (NSin / NCos)
```

At 50 % overlap on a 6 mm bead:

| wall angle from horizontal | layer height |
|---|---|
| 10° | 0.53 → clamped to min |
| 15° | 0.80 → clamped to min |
| **18.4°** | **1.00 — the limit at MinLayerHeight 1.0** |
| 20° | 1.09 |
| 25° | 1.40 |
| 30° | 1.73 |
| 40° | 2.52 |
| **45°** | **3.00 (nominal)** |
| 50°+ | 3.00 (max) |
| vertical wall | 3.00 (max) |

**Anything steeper than 45° gets full-height layers automatically.** The straight column returns to
3 mm with no quality knob involved. Only genuinely shallow surfaces thin, and only as far as the
overhang demands — so this should produce **fewer layers than quality 0 while giving a stronger
guarantee**.

### It independently predicts the observed failure

The angle at which 50 % overlap becomes impossible at `MinLayerHeight` 1.0 is **18.4°**.
The worst measured layer on the failed print (L34) was at **17.8°** — just past the line.
At 7.38 mm effective (squished) bead that cutoff moves down to 15.2°.

---

## Implementation sketch

One extra term in `AdaptiveLayerHeights.LayerHeightFromSlope`, min'd against the existing result:

```csharp
private static float LayerHeightFromSlope(in FaceZ face, float maxDev, float overlapStepMm)
{
    float vojtech = face.NCos > 1e-5f
        ? 1.44f * maxDev * MathF.Sqrt(face.NSin / face.NCos)
        : float.MaxValue;
    float h = MathF.Min(maxDev / 0.184f, vojtech);

    // Bead-overlap limit: a layer may not step sideways further than the operator's
    // allowed fraction of a bead, or the new bead has nothing underneath it.
    if (overlapStepMm > 0f)
    {
        float hOverlap = face.NCos > 1e-5f
            ? overlapStepMm * face.NSin / face.NCos
            : float.MaxValue;          // vertical wall: no overlap constraint
        h = MathF.Min(h, hOverlap);
    }
    return h;
}
```

with `overlapStepMm = (1 − targetOverlapFraction) × beadWidth`, computed once by the caller.

Also needed:

- New `SliceSettings.MinBeadOverlapPercent` (default 50; 0 = disabled).
- Thread `beadWidth` and the overlap setting into `AdaptiveLayerHeights.ComputeZPositions`
  (it does not currently receive bead width) and on into `NextLayerHeight`.
- A console command reporting, per layer, the **binding constraint** — deviation, overlap,
  min clamp, or max clamp. Standing rule: every new feature gets a console command.
- Tests, control-tested by disabling the new term.

`NextLayerHeight`'s two-pass facet scan needs no structural change — both passes already call
`LayerHeightFromSlope`.

---

## How to verify it (and one trap)

⚠️ **Measure overlap point-to-SEGMENT, never point-to-point.** Layers carry only ~270 vertices, so
polyline points are ~12 mm apart; a nearest-*point* search measures discretization, not layer
offset. During this session that produced a bogus constant ~6.14 mm step regardless of layer
height and an overlap figure of 18 % where the truth was 44 %. **The tell was a step that did not
scale with layer height.**

Working method: dump the toolpath with the `tpdump` console command, then for each layer take the
midpoints of its extrude moves and compute the minimum distance to any *segment* of the previous
layer. `overlap% = 100 × (beadWidth − step) / beadWidth`.

Regression targets from the real part:

- Old failing slice: 21 of 618 layers under 50 %; worst 30 % at L32/L33.
- Quality 0 workaround: 0 of 353 under 50 %; worst 59–61 %; 51 layers pinned at the 1.0 mm floor.
- New criterion should reach 0 under 50 % with **noticeably fewer** floor-pinned layers than
  quality 0, and full 3 mm layers on the straight shaft.

---

## Open decisions (ask Jeff before building)

**1. Relationship to the existing surface-deviation criterion**
 - **(recommended)** Combine — take the thinner. Deviation keeps controlling finish via
   `AdaptiveQuality`; overlap becomes a hard floor beneath it that quality cannot override.
 - Replace deviation entirely — simplest, fewest layers, but no way to thin for surface quality.
 - Separate mode dropdown (Finish / Overlap / Both) — most flexible, most UI.

**2. When geometry is too shallow to hit the target even at `MinLayerHeight`**
 - **(recommended)** Warn + highlight in the viewport, slice anyway — same pattern as the
   RPM-over-limit highlight.
 - Block export like the RPM gate — would have blocked the Dragon column, which then printed fine.
 - Auto-lower `MinLayerHeight` — guarantees overlap but drives flow into the unreliable low-RPM
   region; trades an adhesion failure for a starvation one.

**3. Should the criterion know about the RPM floor?**
 Thin layers mean low commanded flow; the 1.0 mm layers sit at 12.65 % RPM against a calibration
 taken at 50 %.
 - **(recommended)** Warn only, let geometry win.
 - Ignore for v1, revisit after the two-point purge quantifies the falloff.
 - Clamp layer height to hold RPM above a floor — silently breaks the overlap guarantee.

**4. Which bead width feeds the calculation?**
 - **(recommended)** Nominal (6 mm) — the measured 7.38 mm squish becomes safety margin.
 - A separately calibrated effective bead width — fewer thin layers, but the guarantee then rests
   on a number that moves with temperature and layer height.

---

## Related

- `LayerHeightFlowPostProcessor` (landed main 544) — makes flow follow real layer thickness.
  Without it, thinning layers over-extrudes them; the two features are complements.
- Flow / RPM calibration model, the pendant-override trap, and the Caracol URM architecture are
  documented in the session memory notes.
- Still open on the Dragon column: ~583 degenerate zero-length contours (harmless now that z-hop
  is off, but a preflight rule would catch the class), and the ~12 % global flow lean implied by
  a 5.3 mm bead measured at 3 mm layers.
