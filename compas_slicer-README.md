# compas_slicer reference (local only)

The full source is cloned to `reference/compas_slicer/` (gitignored). Re-clone:

```powershell
git clone --depth 1 https://github.com/compas-dev/compas_slicer.git reference/compas_slicer
```

- **Upstream:** https://github.com/compas-dev/compas_slicer
- **License:** MIT (Copyright ETH Zurich / compas-dev)
- **Docs:** https://compas.dev/compas_slicer/latest/examples/02_curved_slicing/

## MassiveSLICER mapping

| compas_slicer | MassiveSLICER |
|---|---|
| `CompoundTarget` | `BoundaryTarget.cs` |
| `assign_interpolation_distance_to_mesh_vertices` | `InterpolationField.cs` |
| `ScalarFieldContours` | `MeshGraph.CollectScalarCrossings` |
| `InterpolationSlicer` | `CurvedSlicer.cs` |
| `GradientEvaluation.find_critical_points` | `ScalarFieldGradient.cs` |
| `region_split.MeshSplitter` | `MeshRegionSplitter.cs` |
| `topological_sorting.MeshDirectedGraph` | `SplitMeshGraph.cs` |

## Algorithm (curved / sweep)

For vertex `v`, geodesic distances `d_low(v)` and `d_high(v)` from LOW/HIGH boundary seed sets.
Interpolation parameter `t ∈ [0,1]`:

```
f_t(v) = (1 - t) * d_low(v) - t * d_high(v)
```

Zero isocontours of `f_t` are print layers. Layer count from average boundary separation / layer height.

Geodesics in MassiveSLICER use Dijkstra on the welded mesh graph (same as `GeodesicSlicer`). compas defaults to CGAL heat geodesics.