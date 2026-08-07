# Transform overhaul — what changed, and what to watch for

Branch: `feature/transform-overhaul`

Pull it when you want it; it is kept merged up to `main`. Nothing here has landed on `main`, and it
will not until people are happy with it.

The short version: move, rotate and scale were rebuilt around a real pivot. The handle sits on the
part, turns with the part, and the numbers say what the part is actually doing.

---

## What you get

**The handle is on the part.** It used to sit at whatever origin the exporting package left in the
file — frequently metres away from the geometry. Parts are now pivoted at their own bounding-box
centre on import.

**Rotation is local.** Grab a ring and the part turns about that ring, at the tilt it is drawn at.
It used to rotate about fixed world planes, which only lined up while the part was square to the
room.

**A/B/C are straighten-up buttons, not nudges.** Off-angle at 37°, one click takes you to 90°, not
127°. Already on a stop, it advances a quarter turn. Alt-click goes the other way. All three axes
square up, because the point is getting a flat face back onto the bed.

**Number fields commit on Enter or when you leave them** — not on every keystroke, so typing "90" no
longer swings the part through 9° on the way. They do arithmetic: `45+90`, `90/2`, `30x3`. First
click selects the whole value so typing replaces it; a second click drops the highlight and places
the caret, which is how you write `+90` against a value still on screen.

**Move Origin** shows the part's bounding box with 26 snap points — corners, edge midpoints, face
centres. Click one and the handle goes there. The part does not move. Esc cancels.

**Recenter** puts the handle at the centre of the part's footprint, at the height of its lowest
point — the face that meets the bed. It is measured in world space, so it does the right thing on a
part that has been tumbled.

**The bed is solid.** Drag a part down through the plate and it stops resting on it, while the
handle keeps following your pointer; let go and the handle snaps back onto the part. Typed values
and the A/B/C buttons are corrected after the fact instead. Rotate and scale are not clamped
mid-gesture — see Preferences below.

**Scale tool** with millimetres or percent of the imported size (so typing 100 in percent is a free
reset). The chain keeps the part in proportion by scaling every axis by the same *ratio* — taking a
100 down to 50 also takes a 1300 down to 650. Reset Scale next to it.

**Lay on Face and Drop to Plate** moved from the top-left cluster into the transform toolbar, to the
right of Recenter.

**Preferences → Keep on bed.** A part already sitting on the plate stays on it through a rotate or a
resize, instead of being left hanging. It only holds down parts that were *already* resting — one
you parked in mid-air on purpose is left alone. "When moving" is off by default, because dragging
upward is how you lift a part off the plate and having it snap straight back is maddening.

---

## Known issues — please don't re-report these

**Rotation numbers can appear in a neighbouring field.** Rotate 90° about X and then 30° about the
part's own Z and it reads `(90, −30, 0)` — the Z turn shows up in the Y box. This is inherent to
describing an orientation with three ordered angles; every CAD package does it, and the numbers
round-trip exactly. Clicking B twice reads `(180, 0, 180)` for the same reason. The part is correct;
only the spelling is odd.

**Cut modifiers drift under heavy rotation.** A cut plane follows its part through moves and
ordinary rotations. Tumble a part far enough and the cut will not follow sensibly. Known, accepted
for now, not worth a bug report.

**Scaling re-slices rather than stretching the toolpath.** This is deliberate. A toolpath cannot be
stretched: its bead width is baked in at slice time, so scaling the path would fatten every bead,
and layer height is a machine setting rather than a property of the shape. Resizing a part therefore
throws the path away and slices again at the new size, with your real settings. Expect the toolpath
to disappear and come back.

**A tilt re-slices, and that sends the arm back to the start of the path.** Correct behaviour — the
layer stacking direction no longer matches the part, so it genuinely needs a fresh slice.

**Fit to Cell is hidden.** The code is still there and `scale fit` works from the console; the button
was taking up room without earning it.

---

## Not saved by older files

Pivots and the imported-size baseline are stored in `.mass` from this branch onward. A workspace
saved before it will open fine, but its parts get a fresh bounding-box-centre pivot rather than
whatever you had set — there is nothing in the old file to recover.

---

## Driving it from the console

Every part of this is reachable from the console, which is usually faster than describing a problem:

| Command | What it tells you |
|---|---|
| `xform show` | position, rotation, scale, pivot, shear |
| `basis` | where each coloured handle actually points, and whether the part is world-aligned |
| `origin [show\|box\|points\|center\|snap <±x±y±z>]` | inspect or move the pivot |
| `scale [show\|mm\|pct\|chain\|reset\|fit\|x <v>]` | the whole scale tool |
| `bed` | bed height vs the part's lowest point — resting, floating or through |
| `drop` | drop to plate, reporting the lowest point before and after |
| `step <x\|y\|z> [-]` | what clicking an axis letter does |
| `move-origin [on\|off]` | the snap-point chooser |

If something misbehaves, the output of `xform show` and `bed` at the moment it happens is worth more
than a description.
