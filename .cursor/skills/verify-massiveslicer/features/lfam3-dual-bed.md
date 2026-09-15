# LFAM 3 dual beds (heated vs rotary)

Shop LFAM 3 has two print surfaces. Use the shop cell JSON, not invented transforms.

## Canonical sources

- MassiveSLICER `memory.md` (cell / bed / outliner notes)
- Hermes `Z:\AI\Hermes\memories\MEMORY.md` — **shop cell list is `bin/Release/.../lfam3.json`, not git assets alone**
- MassiveAI: http://192.168.0.126:9090/ai.html (Hermes)

## Shop Release heatedBed (authoritative)

Path: `src/MassiveSlicer.App/bin/Release/net8.0-windows/assets/cells/LFAM3/lfam3.json`

```json
"heatedBed": {
  "name": "HEATED-BED",
  "modelPath": "assets/cells/LFAM3/lfam3_HeatedBed.glb",
  "krlBaseIndex": 6,
  "basePos": [1065.37, 1515.7982, -873.757],
  "baseAbc": [-0.087, 0.11306581, 0.093820065]
}
```

Mesh: `lfam3_HeatedBed.glb` beside that Release assets folder (~40KB).

## Drift trap (do not verify as correct product)

Checked-in `assets/cells/LFAM3/lfam3.json` / `src/assets/...` copies historically **omit** `heatedBed` and only have `bed.modelPath` = `LFAM3Bed.glb` with `hidden: true`. That GLB is the **print-area / bed-grid** mesh. Loading it as `HeatedBed` (wrong scale/orientation) is a product regression — map and recipes must expect `cell.heatedBed` + `lfam3_HeatedBed.glb` at shop transform.

Rotary meshes stay `rotary_bed_bottom.glb` / `rotary_bed_top.glb` with their own basePos/baseAbc.

## BASE behavior

- BASE 6 (heated): rectangular print-area overlay; heated mesh solid at heatedBed transform; rotary ghosted
- Rotary BASE: polar overlay; rotary solid; heated ghosted
- Preview / Arctic: print-area bed grid still draws when Bed grid is on (do not skip Arctic)

## Drive

1. `doctor`; launch from local Apps copy (`C:\Users\MassiveMAKE\Apps\MassiveSlicer.App\`), cwd = repo or PR worktree
2. `command cell` → LFAM 3
3. `robotset KrlBaseSelectedIndex` to Base 6 (selected index often `2` when options are 1,2,6) — **not** raw `KrlBaseIndex`
4. `viewmode Preview`; `viewset ShowBedGrid true`
5. Confirm outliner / visible-debug shows **Heated Bed** from `lfam3_HeatedBed.glb`; screenshot under `artifacts/lfam3-dual-bed/`

Hard bans: `sync` / `move-*`.

## Do not conflate with Lab hydronic memory

Agent/Hermes durable memory for **MassiveLAB 26-014** (physical 5′×6′ aluminum + copper serpentine heated bed) is plumbing/electrical, not slicer scene placement. Overlay size **1800×1800 mm** and `heatedBed` basePos/baseAbc come from **Release cell JSON + MassiveSLICER memory.md**, not from that Lab project.
