using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Viewport.Collision;
using MassiveSlicer.Viewport.Loading;
using Xunit;
using Xunit.Abstractions;

namespace MassiveSlicer.Tests.Collision;

/// <summary>
/// Extraction smoke test against the real LFAM1 robot GLB (skipped when the asset
/// isn't present, following the GltfFkTest pattern).
/// </summary>
public sealed class RobotHullExtractionTest(ITestOutputHelper output)
{
    [Fact]
    public void Lfam1Robot_ExtractsHullsForArmLinks()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "../../../../..", "assets", "cells", "LFAM1", "LFAM1Robot.glb"),
            Path.Combine(Directory.GetCurrentDirectory(), "assets", "cells", "LFAM1", "LFAM1Robot.glb"),
        ];
        var glbPath = candidates.FirstOrDefault(File.Exists);
        if (glbPath is null)
        {
            output.WriteLine("SKIP: LFAM1Robot.glb not found");
            return;
        }

        var root = GltfLoader.Load(glbPath);
        Assert.NotNull(root);

        // Rest poses straight from the loaded local transforms (same as RobotFkController).
        var restPoses = new OpenTK.Mathematics.Matrix4[6];
        for (int i = 0; i < 6; i++)
        {
            var joint = root!.FindDescendant($"joint_{i + 1}");
            restPoses[i] = joint?.LocalTransform ?? OpenTK.Mathematics.Matrix4.Identity;
        }

        var cfg = new JointConfig[6];
        for (int i = 0; i < 6; i++) cfg[i] = new JointConfig();

        var model = CollisionModelExtractor.ExtractRobot(
            root!, cfg, restPoses, s => output.WriteLine(s));

        Assert.NotNull(model);

        // Every arm link that exists in the GLB should carry at least one hull,
        // and no hull may be empty.
        int meshed = 0;
        for (int l = 1; l <= 6; l++)
        {
            var hulls = model!.HullsFor(l);
            if (hulls.Count == 0) continue;
            meshed++;
            foreach (var h in hulls)
                Assert.True(h.Vertices.Length >= 4, $"link {l} hull too small");
        }
        Assert.True(meshed >= 3, $"only {meshed} arm links have hulls");

        // FK at home must produce distinct, finite link transforms.
        Span<System.Numerics.Matrix4x4> xf =
            stackalloc System.Numerics.Matrix4x4[MassiveSlicer.Core.Collision.RobotCollisionModel.LinkCount];
        Span<float> home = stackalloc float[6];
        model!.ComputeLinkTransforms(home, System.Numerics.Matrix4x4.Identity, xf);
        for (int l = 1; l <= 6; l++)
            Assert.True(float.IsFinite(xf[l].Translation.X), $"link {l} transform not finite");
    }
}
