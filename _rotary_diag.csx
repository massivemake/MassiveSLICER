using MassiveSlicer.Core.IO;
using MassiveSlicer.Viewport.Loading;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;
Directory.SetCurrentDirectory(@"\\192.168.0.191\MassiveFILES\Research\LFAM\MassiveSLICER V2");
foreach (var rel in new[] {
    "reference/MassiveCONNECT-V2/MassiveCONNECT/webcells/LFAM3/rotary_bed_bottom.glb",
    "assets/cells/LFAM3/rotary_bed_bottom.glb" })
{
    var path = AssetPaths.Resolve(rel);
    Console.WriteLine($"=== {rel}");
    Console.WriteLine($"path={path} exists={File.Exists(path)}");
    try {
        var load = GltfLoader.Load(path);
        var native = GltfLoader.LoadNativeMeters(path);
        long Count(SceneNode r) { long t=0; foreach(var n in r.SelfAndDescendants()) if(n.PendingMesh!=null) t+=SceneTriangleStats.TriangleCount(n.PendingMesh); return t; }
        (Vector3 min, Vector3 max) B(SceneNode r) {
            var min=new Vector3(float.MaxValue); var max=new Vector3(float.MinValue);
            foreach(var n in r.SelfAndDescendants()) {
                if(n.PendingMesh is not {} m) continue;
                var w=n.WorldTransform; var (bmin,bmax)=m.LocalBounds;
                foreach(var p in new[]{new Vector3(bmin.X,bmin.Y,bmin.Z), new Vector3(bmax.X,bmax.Y,bmax.Z)}) {
                    var ww=new Vector3(p.X*w.M11+p.Y*w.M21+p.Z*w.M31+w.M41,p.X*w.M12+p.Y*w.M22+p.Z*w.M32+w.M42,p.X*w.M13+p.Y*w.M23+p.Z*w.M33+w.M43);
                    min=Vector3.ComponentMin(min,ww); max=Vector3.ComponentMax(max,ww);
                }
            }
            return (min,max);
        }
        var (lmin,lmax)=B(load); var (nmin,nmax)=B(native);
        Console.WriteLine($"Load: tris={Count(load)} ext={(lmax-lmin).Length:F2} rootScale={load.LocalTransform.M11:F4}");
        Console.WriteLine($"Native: tris={Count(native)} ext={(nmax-nmin).Length:F2} rootScale={native.LocalTransform.M11:F4}");
    } catch (Exception ex) { Console.WriteLine($"ERR: {ex.Message}"); }
}
