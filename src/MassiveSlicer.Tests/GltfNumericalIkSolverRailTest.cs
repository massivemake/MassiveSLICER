using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Viewport.FK;
using OpenTK.Mathematics;

namespace MassiveSlicer.Tests;

public class GltfNumericalIkSolverRailTest
{
    static readonly JointConfig[] Joints =
    [
        new() { KrlOffset = 0,  KrlSign = -1, MinDeg = -60,  MaxDeg = 60,  Axis = RotationAxis.Y },
        new() { KrlOffset = 90, KrlSign = -1, MinDeg = -120, MaxDeg = 70,  Axis = RotationAxis.X },
        new() { KrlOffset = -90,KrlSign = -1, MinDeg = -120, MaxDeg = 168, Axis = RotationAxis.X },
        new() { KrlOffset = 0,  KrlSign = -1, MinDeg = -350, MaxDeg = 350, Axis = RotationAxis.Y },
        new() { KrlOffset = 0,  KrlSign = -1, MinDeg = -125, MaxDeg = 125, Axis = RotationAxis.X },
        new() { KrlOffset = 0,  KrlSign = -1, MinDeg = -350, MaxDeg = 350, Axis = RotationAxis.Y },
    ];

    [Fact]
    public void UpdateSceneBase_shifts_tcp_with_rail_carriage()
    {
        var rest = Enumerable.Repeat(Matrix4.Identity, 6).ToList();
        var home = new float[] { 0f, -90f, 90f, 0f, 15f, 0f };
        var robrootHome = new Vector3(0f, 0f, 500f);
        var tcpLocal    = Matrix4.CreateTranslation(0.9f, 0.25f, -0.165f);

        var solver = new GltfNumericalIkSolver(rest, Matrix4.CreateTranslation(robrootHome),
            robrootHome, tcpLocal, Joints, -MathF.PI / 2f);
        var tcpAtHome = solver.ComputeTcpPosScene(home);

        var railOffset = new Vector3(0f, -1500f, 0f);
        var robrootRail = robrootHome + railOffset;
        solver.UpdateSceneBase(Matrix4.CreateTranslation(robrootRail), robrootRail);
        var tcpOnRail = solver.ComputeTcpPosScene(home);

        var delta = tcpOnRail - tcpAtHome;
        Assert.InRange(delta.X, -1f, 1f);
        Assert.InRange(delta.Y, -1501f, -1499f);
        Assert.InRange(delta.Z, -1f, 1f);
    }
}