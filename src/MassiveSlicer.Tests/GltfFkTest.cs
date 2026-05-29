using System.Numerics;
using MassiveSlicer.Core.Kinematics;
using SharpGLTF.Schema2;
using Xunit.Abstractions;

namespace MassiveSlicer.Tests;

/// <summary>
/// Loads LFAM2Robot.glb, extracts rest-pose joint transforms, applies the
/// same FK algorithm as RobotFkController, and compares joint_6 world position
/// with the OPW DH FK to diagnose the coordinate-frame mismatch.
/// </summary>
public class GltfFkTest(ITestOutputHelper output)
{
    // Matches Lfam2Joints.All -- (offset_deg, sign, axis)
    private static readonly (float OffsetDeg, float Sign, int Axis)[] JointCfg =
    [
        (  0f, -1f, 1),  // A1 Y
        (+90f, -1f, 0),  // A2 X
        (-90f, -1f, 0),  // A3 X
        (  0f, -1f, 1),  // A4 Y
        (  0f, -1f, 0),  // A5 X
        (  0f, -1f, 1),  // A6 Y
    ];

    // GltfToScene = Rx(+90deg) * Scale(1000) -- same as GltfLoader.GltfToScene
    // Applied at the GLTF root before joint chain.
    // In row-vector convention: new.x = 1000*old.x, new.y = -1000*old.z, new.z = 1000*old.y
    private static Matrix4x4 GltfToScene()
    {
        var rx = Matrix4x4.CreateRotationX(MathF.PI / 2f);
        var sc = Matrix4x4.CreateScale(1000f);
        return rx * sc;
    }

    [Fact]
    public void GltfFK_vs_OpwFK_AtHome()
    {
        // -- Locate the GLB file --------------------------------------------------
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "../../../../..", "assets", "LFAM2Robot.glb"),
            Path.Combine(Directory.GetCurrentDirectory(), "assets", "LFAM2Robot.glb"),
        ];

        var glbPath = candidates.FirstOrDefault(File.Exists);
        if (glbPath is null)
        {
            output.WriteLine("SKIP: LFAM2Robot.glb not found. Tried:");
            foreach (var c in candidates) output.WriteLine("  " + c);
            return;
        }

        output.WriteLine($"Loaded: {glbPath}");

        // -- Load GLTF and extract joint rest poses -------------------------------
        var model  = ModelRoot.Load(glbPath);
        var scene  = model.DefaultScene;

        var joints     = new Node?[6];
        var restPoses  = new Matrix4x4[6];

        // Walk the scene tree to find joint_1 ... joint_6
        void Walk(Node n)
        {
            var name = n.Name ?? "";
            for (int i = 0; i < 6; i++)
                if (name == $"joint_{i + 1}") { joints[i] = n; restPoses[i] = n.LocalMatrix; }
            foreach (var child in n.VisualChildren) Walk(child);
        }
        if (scene != null)
            foreach (var root in scene.VisualChildren) Walk(root);

        for (int i = 0; i < 6; i++)
            output.WriteLine($"joint_{i+1}: {(joints[i] != null ? "found" : "MISSING")}  rest={Fmt(restPoses[i])}");

        output.WriteLine("");

        // -- Compute GLTF FK at home angles [0, -90, 90, 0, 15, 0] ---------------
        float[] homeKrl = [0f, -90f, 90f, 0f, 15f, 0f];
        var flange6Home = ComputeGltfFlange(restPoses, homeKrl);
        output.WriteLine($"GLTF FK flange (scene) at home: {Fmt(flange6Home)}");
        output.WriteLine($"GLTF FK flange (ROBROOT) at home (subtract robot Z=1000): {Fmt(flange6Home - new Vector3(0,0,1000))}");
        output.WriteLine("");

        // OPW DH FK at home for comparison
        var opwFk = KukaIkSolver.ForwardKinematics(homeKrl);
        var opwFlangeHome = new Vector3(opwFk.M41, opwFk.M42, opwFk.M43);
        output.WriteLine($"OPW DH FK flange (ROBROOT) at home: {Fmt(opwFlangeHome)}");
        output.WriteLine("");

        // -- Compute GLTF FK at solved bed-center angles --------------------------
        float[] bedAngles = [4.53f, -3.30f, 128.99f, 0f, 54.31f, -175.47f];
        var flange6Bed = ComputeGltfFlange(restPoses, bedAngles);
        output.WriteLine($"GLTF FK flange (scene) at bed-center angles: {Fmt(flange6Bed)}");
        output.WriteLine($"GLTF FK flange (ROBROOT) at bed-center angles: {Fmt(flange6Bed - new Vector3(0,0,1000))}");

        // OPW DH FK at same angles
        var opwFkBed = KukaIkSolver.ForwardKinematics(bedAngles);
        var opwFlangeBed = new Vector3(opwFkBed.M41, opwFkBed.M42, opwFkBed.M43);
        output.WriteLine($"OPW DH FK flange (ROBROOT) at bed-center angles: {Fmt(opwFlangeBed)}");
        output.WriteLine("");

        output.WriteLine("OPW IK target (nozzle ROBROOT): (2933.83, 122.64, -870.00)");
        output.WriteLine("OPW IK target flange ROBROOT:   (3611.74, 285.80, -555.68)");
    }

    [Fact]
    public void GltfFK_PrintJointOrigins_AtRest()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "../../../../..", "assets", "LFAM2Robot.glb"),
            Path.Combine(Directory.GetCurrentDirectory(), "assets", "LFAM2Robot.glb"),
        ];
        var glbPath = candidates.FirstOrDefault(File.Exists);
        if (glbPath is null) { output.WriteLine("SKIP: GLB not found"); return; }

        var model = ModelRoot.Load(glbPath);
        var restPoses = new Matrix4x4[6];

        void Walk(Node n)
        {
            for (int i = 0; i < 6; i++)
                if (n.Name == $"joint_{i+1}") restPoses[i] = n.LocalMatrix;
            foreach (var c in n.VisualChildren) Walk(c);
        }
        foreach (var root in model.DefaultScene!.VisualChildren) Walk(root);

        // Print joint origin positions in GLTF Y-up metres (at rest, all KRL=0)
        output.WriteLine("=== Joint origins in GLTF Y-up space (metres) at KRL=0 ===");
        var chain = Matrix4x4.Identity;
        for (int i = 0; i < 6; i++)
        {
            chain = restPoses[i] * chain;   // row-vector: local * parent
            var pos = chain.Translation;
            output.WriteLine($"joint_{i+1} origin (GLTF): ({pos.X*1000:F2}, {pos.Y*1000:F2}, {pos.Z*1000:F2}) mm");
        }

        // Same but in scene Z-up mm (apply GltfToScene to each GLTF position)
        output.WriteLine("");
        output.WriteLine("=== Joint origins in scene Z-up space (mm) at KRL=0 ===");
        var gtoc = GltfToScene();
        chain = Matrix4x4.Identity;
        for (int i = 0; i < 6; i++)
        {
            chain = restPoses[i] * chain;
            var posGltf = chain.Translation;
            // GltfToScene: scene = (1000x, -1000z, 1000y)
            var posScene = new Vector3(1000*posGltf.X, -1000*posGltf.Z, 1000*posGltf.Y);
            output.WriteLine($"joint_{i+1} origin (scene): ({posScene.X:F2}, {posScene.Y:F2}, {posScene.Z:F2}) mm");
        }
    }

    /// <summary>
    /// Dumps the local transforms of all nodes above joint_1 (the intermediate
    /// wrapper nodes). These are NOT included in ComputeGltfFlangeMatrix and may
    /// carry rotational offsets that break the FK.
    /// </summary>
    [Fact]
    public void GltfFK_PrintIntermediateNodeTransforms()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "../../../../..", "assets", "LFAM2Robot.glb"),
            Path.Combine(Directory.GetCurrentDirectory(), "assets", "LFAM2Robot.glb"),
        ];
        var glbPath = candidates.FirstOrDefault(File.Exists);
        if (glbPath is null) { output.WriteLine("SKIP: GLB not found"); return; }

        var model = ModelRoot.Load(glbPath);

        // Walk and print every node's local transform until we hit joint_1
        output.WriteLine("=== GLTF node local transforms (T in mm, rotation rows) ===");
        void Walk(Node n, int depth, bool pastJoint1)
        {
            var m = n.LocalMatrix;
            var t = m.Translation;
            bool isJoint = (n.Name ?? "").StartsWith("joint_");
            string indent = new(' ', depth * 2);
            output.WriteLine($"{indent}[{n.Name ?? "(null)"}]");
            output.WriteLine($"{indent}  T=({t.X*1000:F2}, {t.Y*1000:F2}, {t.Z*1000:F2})mm");
            output.WriteLine($"{indent}  R0=({m.M11:F4},{m.M12:F4},{m.M13:F4}) R1=({m.M21:F4},{m.M22:F4},{m.M23:F4}) R2=({m.M31:F4},{m.M32:F4},{m.M33:F4})");

            if (!pastJoint1 || isJoint)
                foreach (var c in n.VisualChildren)
                    Walk(c, depth + 1, pastJoint1 || isJoint);
        }

        foreach (var root in model.DefaultScene!.VisualChildren)
            Walk(root, 0, false);
    }

    /// <summary>
    /// Round-trip test: feed GLTF FK home flange pose into OPW IK and check
    /// whether any solution matches home angles. This diagnoses whether OPW IK
    /// and GLTF FK share the same coordinate frame.
    /// </summary>
    [Fact]
    public void GltfFK_Home_RoundTrip_OPW_IK()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "../../../../..", "assets", "LFAM2Robot.glb"),
            Path.Combine(Directory.GetCurrentDirectory(), "assets", "LFAM2Robot.glb"),
        ];
        var glbPath = candidates.FirstOrDefault(File.Exists);
        if (glbPath is null) { output.WriteLine("SKIP: GLB not found"); return; }

        var model     = ModelRoot.Load(glbPath);
        var restPoses = new Matrix4x4[6];
        void Walk(Node n)
        {
            for (int i = 0; i < 6; i++)
                if (n.Name == $"joint_{i+1}") restPoses[i] = n.LocalMatrix;
            foreach (var c in n.VisualChildren) Walk(c);
        }
        foreach (var root in model.DefaultScene!.VisualChildren) Walk(root);

        float[] homeKrl = [0f, -90f, 90f, 0f, 15f, 0f];

        var wt     = ComputeGltfFlangeMatrix(restPoses, homeKrl);
        var rotMat = ExtractRotation(wt);
        // Flange in ROBROOT: subtract robot base translation (0,0,1000)
        var flange = new Vector3(wt.M41, wt.M42, wt.M43 - 1000f);

        var (a, b, c) = MassiveSlicer.Core.Kinematics.KukaIkSolver.MatrixToAbc(rotMat);

        output.WriteLine($"GLTF FK at home: flange ROBROOT = {Fmt(flange)}");
        output.WriteLine($"GLTF FK rotation (rows = scene X/Y/Z axes of flange frame):");
        output.WriteLine($"  Row0: ({rotMat.M11:F4}, {rotMat.M12:F4}, {rotMat.M13:F4})");
        output.WriteLine($"  Row1: ({rotMat.M21:F4}, {rotMat.M22:F4}, {rotMat.M23:F4})");
        output.WriteLine($"  Row2: ({rotMat.M31:F4}, {rotMat.M32:F4}, {rotMat.M33:F4})");
        output.WriteLine($"  ABC = A={a:F2}deg B={b:F2}deg C={c:F2}deg");
        output.WriteLine($"  Tool Z (row2) in scene = ({rotMat.M31:F4}, {rotMat.M32:F4}, {rotMat.M33:F4})");
        output.WriteLine("");

        var solutions = MassiveSlicer.Core.Kinematics.KukaIkSolver.SolveAll(flange, rotMat);
        output.WriteLine("OPW SolveAll using GLTF FK home pose:");
        for (int i = 0; i < solutions.Length; i++)
        {
            var s = solutions[i];
            string tag = s.Unreachable ? "UNREACHABLE" : s.InLimits ? "IN_LIMITS" : "OOL";
            output.WriteLine($"  [{i}] {tag}: [{string.Join(", ", s.Krl.Select(v => $"{v:F2}"))}]");
        }

        // Check if any solution matches home angles within 1deg
        bool found = solutions.Any(s =>
        {
            if (s.Unreachable) return false;
            for (int i = 0; i < 6; i++)
                if (Math.Abs(s.Krl[i] - homeKrl[i]) > 1f) return false;
            return true;
        });
        output.WriteLine($"\nHome angles [{string.Join(", ", homeKrl.Select(v => $"{v:F1}"))}] found in solutions: {found}");
        output.WriteLine("");
        output.WriteLine("If 'found=False', OPW IK and GLTF FK use different frames.");
    }

    // -- GLTF FK implementation ------------------------------------------------

    /// <summary>
    /// Computes joint_6 world transform in scene mm (robot base at world 0,0,1000).
    /// Replicates RobotFkController.Apply + SceneNode.WorldTransform exactly.
    /// The rotation rows are scaled by 1000 (Scale(1000) from GltfToScene is embedded).
    /// </summary>
    private static Matrix4x4 ComputeGltfFlangeMatrix(Matrix4x4[] restPoses, float[] krlDeg)
    {
        var baseTranslation = Matrix4x4.CreateTranslation(0f, 0f, 1000f);
        var gltfRoot = GltfToScene();
        var wt = gltfRoot * baseTranslation;

        for (int i = 0; i < 6; i++)
        {
            var cfg = JointCfg[i];
            float boneAngle = cfg.Sign * krlDeg[i] * MathF.PI / 180f;

            Matrix4x4 rot = cfg.Axis switch
            {
                0 => Matrix4x4.CreateRotationX(boneAngle),
                2 => Matrix4x4.CreateRotationZ(boneAngle),
                _ => Matrix4x4.CreateRotationY(boneAngle),
            };

            var lt = rot * restPoses[i];
            wt = lt * wt;
        }

        return wt;
    }

    private static Vector3 ComputeGltfFlange(Matrix4x4[] restPoses, float[] krlDeg)
        => ComputeGltfFlangeMatrix(restPoses, krlDeg).Translation;

    /// <summary>
    /// Extracts a normalized rotation matrix from the GLTF FK world transform,
    /// removing the Scale(1000) embedded by GltfToScene. Result is in scene/ROBROOT frame.
    /// </summary>
    private static Matrix4x4 ExtractRotation(Matrix4x4 wt)
    {
        // GltfToScene includes Scale(1000), so rotation rows are 1000× too large.
        return new Matrix4x4(
            wt.M11 / 1000f, wt.M12 / 1000f, wt.M13 / 1000f, 0f,
            wt.M21 / 1000f, wt.M22 / 1000f, wt.M23 / 1000f, 0f,
            wt.M31 / 1000f, wt.M32 / 1000f, wt.M33 / 1000f, 0f,
            0f,             0f,             0f,              1f);
    }

    private static string Fmt(Vector3 v) => $"({v.X:F2}, {v.Y:F2}, {v.Z:F2})";
    private static string Fmt(Matrix4x4 m)
    {
        var t = m.Translation;
        return $"T=({t.X*1000:F1}, {t.Y*1000:F1}, {t.Z*1000:F1})mm";
    }
}
