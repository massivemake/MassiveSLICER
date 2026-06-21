using MassiveSlicer.Core.IO;
using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.Viewport.Scene;
Directory.SetCurrentDirectory(@"\\192.168.0.191\MassiveFILES\Research\LFAM\MassiveSLICER V2");
foreach (var rel in new[] { "assets/cells/LFAM3/rotary_bed_bottom.glb", "assets/cells/LFAM3/rotary_bed_top.glb" }) {
  var r = GltfLoader.Load(AssetPaths.Resolve(rel));
  var (m,t) = SceneTriangleStats.Count(r);
  Console.WriteLine($"{System.IO.Path.GetFileName(rel)}: {m} meshes, {t:N0} tris");
}
