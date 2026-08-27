# Layer-transition start/stops on receding walls

**Status:** open, unfixed. Diagnosed 2026-07-31. No decision made on which fix to take.

**Not to be confused with** [`START_STOP_CALIBRATION.md`](START_STOP_CALIBRATION.md). That effort tunes how *clean* a start/stop is (wipe, z-hop, resume wait, travel speed) for island-to-island travels. This note is about a class of start/stops at **layer transitions** that may not need to exist at all. Same physical phenomenon, opposite question: that doc asks "how do we make these good", this one asks "why are these here".

---

## What you see

On a wall whose top recedes, the toolpath climbs continuously for a stretch and then, for a band of ~20–30 layers, stops and does a small lift-over-drop hook at each layer change instead of stitching through. The hooks render **purple**.

Purple is not arbitrary. It is the **z-hop retraction travel** colour:

- `ToolpathRenderer.cs:694` — `var color = move.IsZHop ? _retractionColor : _travelColor;`
- `AppPreferences.cs:132` — `ToolpathRetractionColor = "#FF9C27B0"` (material purple)
- plain travels are red `#FFD92E2E`, extrusions white, wipes orange `#FFFF8800`

So each purple hook is a genuine **stop extruding → travel → resume**.

## The rule that decides it

One comparison, in `PlanarSlicer.cs:230`:

```csharp
// XY distance from where layer N ended to where layer N+1 starts
if (xyDist > settings.BeadWidth)
    layer.Moves.Insert(0, new ToolpathMove(endPos, startPos, MoveKind.Travel)
        { IsLayerChange = true });          // stop extruding
else if (...)
    layer.Moves.Insert(0, new ToolpathMove(endPos, startPos, MoveKind.Extrude)
        { IsLayerStitch = true });          // keep printing through the seam
```

The threshold is **not a stitch setting** — it is `BeadWidth`, borrowed. Widening the bead therefore also loosens how far the seam may wander before the toolpath breaks, which is not an obvious coupling.

The hook shape is then applied by `MovementPostProcessor.ExpandZHop` (`MovementPostProcessor.cs:141`), which mechanically explodes **every** travel into three segments — up `ZHopMm`, across, down. It does not decide anything; it only decorates a travel the slicer already emitted.

## Measurements

Settings for all figures below: `BeadWidth = 6`, `LayerHeight = 3`, `ZHopMm = 3`, `WipeLengthMm = 12`, `SeamMode = Zig-zag`, `SlicingMode = Surface`.

| part | layers | extrude | travel | runs |
|---|---|---|---|---|
| `SimpleBendyWallV1` (straight top, bendy plan) | 185 | 9804 | **0** | 185 |
| `RecedeWallV1` (cut, wave-topped) | 175 | 12048 | **90** | 175 |

90 travels = **30 layer transitions × 3 z-hop segments**.

Two things worth drawing out:

1. **A bendy wall with a deliberately wandering zig-zag seam produced zero breaks.** The zig-zag seam alone is not sufficient.
2. **The same recede wall sliced with 1 enabled support and with 8 enabled supports both produced exactly 90 travels.** Supports do not participate in this decision at all.

`tpcheck` on the recede wall: `174/174 layer step-ups land within 6 mm XY · 0 dragged · VERDICT: continuous within every layer AND from each layer up to the next.` **The part prints.** This is a quality question, not a correctness one.

## Whose problem is it

Neither of the obvious candidates:

- **Not a Structural Support problem.** The support arm is spliced *into* a layer's run; this break is at the layer-to-layer transition, decided before supports are applied. Proven by the 1-support vs 8-support slice above.
- **Not inherent to receding walls.** The mechanism is generic — any geometry whose seam endpoint moves more than one bead width between consecutive layers trips it. A recede is simply a reliable way to make that happen, because the contour steps inward as the top falls away.

It is a **general `PlanarSlicer` layer-stitch behaviour**, fixable independently of any other feature.

## Options, with trade-offs

1. **`ZHopMm = 0`** — no code change. Removes the lifted hook; each break becomes a flat travel across the gap. The stop itself, and its wipe, remain. Cheapest thing to try and it costs nothing to reverse.
2. **Give the stitch threshold its own setting** instead of borrowing `BeadWidth`, defaulted to today's value so nothing changes until tuned. Small change. But it is a blanket loosening — every transition gets it, not just the ones on a recede.
3. **Fix seam placement** so the transition distance stays under the threshold at those layers. Attacks the cause and deposits nothing extra, but argues with `SeamMode = Zig-zag`, which deliberately moves the seam every layer.

⚠️ **Cost of option 2 that is easy to miss:** on a *receding* wall the next layer starts further **inboard**, so a longer stitch does not bridge over air — it lays a bead back across the top surface of the layer below. The failure mode is **over-deposition, a raised ridge** that subsequent layers then sit on, not a sagging bridge.

## Do this measurement before choosing

The deciding fact is **how far past 6 mm those 30 transitions actually are**:

- 7–10 mm → a modest tolerance bump (option 2) fixes it with negligible over-deposit.
- 30–40 mm → no tolerance is ever safe, because the stitch would drag a long bead across the top face. Option 3 or nothing.

`tpcheck` already computes this distance per transition (`ConsoleCommandRegistry`, the layer-to-layer loop) and reports only aggregates. Printing the distribution is a small, read-only diagnostic change with no effect on geometry.

## Related

- [`START_STOP_CALIBRATION.md`](START_STOP_CALIBRATION.md) — tuning the quality of a start/stop once you have one.
- `MovementPostProcessor.WipeSkipShortTravels` already suppresses the wipe for travels shorter than twice the layer height; worth checking whether these transitions qualify, since a 12 mm wipe at each of 30 breaks is a lot of deposited material.
