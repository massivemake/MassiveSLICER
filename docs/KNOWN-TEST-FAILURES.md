# Known test failures (baseline)

`dotnet test src/MassiveSlicer.Tests` → **15 failures / 553 tests** on a clean `main`
(verified 2026-07-27 by running the suite on an unmodified checkout).

**These are not your regression.** Compare your failures against this list. If the set matches,
you introduced nothing. Do **not** stash-and-rerun to derive a baseline — that leaves stale
binaries behind (see `CLAUDE.md`) and costs a full cycle.

Fast set-difference check:

```bash
dotnet test src/MassiveSlicer.Tests 2>&1 | grep -E "^\s+Failed " | sed 's/\[.*//' | sort > /tmp/now.txt
```

Then diff `/tmp/now.txt` against the list below; anything extra is yours.

## The 15

**Environment / path-dependent (9)** — these depend on the process working directory, and
`MeshoptDecodeTest` calls `Directory.SetCurrentDirectory`, so the count can wobble 14↔15
between runs depending on test order. Not code defects.

- `CellLoaderTest.Lfam1_bed_and_tool_match_controller_config_dat`
- `Lfam3MillingConfigTest.Lfam3_json_has_milling_bridge_config`
- `MaterialPresetsLoaderTest.Load_finds_presets_when_cwd_is_exe_dir_with_partial_assets_folder`
- `MeshoptDecodeTest.Meshopt_reference_glb_loads_via_runtime_decode` (×2 theory cases)
- `PathNormalizationTest.Normalize_strips_UNC_extended_prefix`
- `WorkspaceCellPathTest.Matches_accepts_same_cell_by_filename_when_paths_differ`
- `WorkspaceCellPathTest.Resolve_finds_cell_by_filename_when_absolute_path_differs`
- `WorkspaceCellPathTest.Resolve_prefers_discovered_install_over_network_absolute_path`

**Open bugs / WIP (6)** — real, tracked, not yet fixed.

- `LiveApplyReproTest.Repro_bounded_cut_on_real_mesh_with_live_reported_parameters`
- `LiveApplyReproTest.Repro_infinite_cut_on_real_mesh_with_live_reported_parameters`
  — the live "Cut Modifier Apply does nothing" report; repro tests were committed
  deliberately failing.
- `FormboundCavityTest.DeckOverTheVoidIsFullySupportedAndPicksUpWithoutTravels`
- `FuselageRealCheck.RealFuselageMetrics`
- `OutlinerCanDeleteTest.Cell_infrastructure_outliner_items_are_not_deletable`
- `SlicingModeTest.Surface_mode_ignores_infill_and_keeps_tool_vertical_by_default`

## Keeping this honest

When you fix one, delete its line here in the same commit. When the suite grows a new
expected failure, add it with a one-line reason — an undocumented failure is one every
teammate will re-investigate.
