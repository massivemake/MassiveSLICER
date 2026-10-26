# Motion and extruder speed on a toolpath

Changing print speed, travel speed, wipe speed, extrusion RPM, bead width, layer height, flow, or the first-layer speed and RPM updates the speed and extrusion RPM of the toolpath that is already on the timeline. That includes an imported SRC and a sliced path. The timeline readout, the Speed and RPM tags, the RPM colours, the print-time estimate, and the next Save or Send all use the new numbers.

The program does not rebuild the path for a speed or RPM edit. Per-move scales stay as they are, and the panel speed and RPM multiply those scales. That is the same product KRL export writes to `$VEL.CP` and `$ANOUT[4]`. A print SRC whose header names a layer height is split into layers each time the bead climbs by that height, so Adaptive Speed and a first-layer speed apply per layer. A file with no layer height stays one layer, and a first-layer speed then covers the whole file.

Adaptive Speed is the exception. Its scales are a layer's mm/s divided by the print speed, so changing the print speed or the adaptive window rewrites those scales on the path that is already drawn. It does not upload a new path, drop the stored joint solutions, or start a reachability solve.

## How to get to it (user POV)

Preview or SDF, with a toolpath on the playback timeline. In the right panel, MOTION holds print speed, travel speed, and Adaptive Speed. MOVEMENT holds the first-layer speed and RPM. WIPE holds wipe speed. PRINT TOOLPATH holds extrusion speed, the extrusion offset, bead width, and layer height. The timeline shows `mm/s · RPM %` for the move under the playhead. Speed and RPM view tags beside the path show the same rates.

## Driving it with the bridge

```bash
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "scrub"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "addset PrintSpeed 80"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "scrub"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "addset ExtrusionSpeedOffset 5"
python3 .agents/skills/verify-massiveslicer/scripts/drive.py cmd "scrub"
```

Pass: the second `scrub` logs a new `mm/s` on the line after the move count. The extrusion-offset `scrub` logs a new `RPM %`. Neither command starts a reachability pass. Put the print speed back when finished.

`addset` writes the live panel. Do not point it at a mill feed or spindle.

## Gotchas

- The open app keeps the binary it launched with until quit and a rebuild.
- Speed-view colours are the spread of per-move scales. A uniform print-speed change leaves a flat path the same colour. The mm/s number still changes. Adaptive Speed rewrites one scale per layer, and the colours follow. An import applies that before the path is drawn when Adaptive Speed is on.
- Print time and weight refresh about half a second after the last edit. On a multi-million-move path that walk is the slow part, and it does not solve joints.
- The SRC parser does not keep the file's `$VEL.CP` or `$ANOUT[4]`. Imported moves use scale 1, so the panel is the speed and RPM.
- Save and Send already wrote the panel speed and RPM. This change is the on-screen number and the adaptive scales catching up with that.
