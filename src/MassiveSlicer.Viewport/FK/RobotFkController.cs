using MassiveSlicer.Core.Kinematics;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.FK;

/// <summary>
/// Applies forward kinematics to a loaded robot scene graph by rotating
/// the six joint nodes (named <c>joint_a1</c>…<c>joint_a6</c>) around their
/// bone-local Y-axis according to KRL joint angles.
/// <para>
/// Bone angle formula: <c>bone_rad = KrlSign × krl_deg × π/180</c>
/// KrlOffset is a KRL↔math convention artifact used only for IK, not FK.
/// </para>
/// </summary>
public sealed class RobotFkController
{
    private static readonly string[] JointNames =
        ["joint_1", "joint_2", "joint_3", "joint_4", "joint_5", "joint_6"];

    private readonly SceneNode?[]  _joints   = new SceneNode[6];
    private readonly Matrix4[]     _restPose = new Matrix4[6];
    private readonly JointConfig[] _jcfg     = new JointConfig[6];

    /// <summary>
    /// The flange joint node (joint_6). Attach tool scene nodes here so they
    /// follow the flange as FK is applied each frame.
    /// </summary>
    public SceneNode? FlangeNode => _joints[5];

    /// <summary>
    /// Rest-pose local transforms for each joint (joint_1 … joint_6), in GLTF Y-up space.
    /// Used by <see cref="GltfNumericalIkSolver"/> to replicate the FK without touching the scene graph.
    /// </summary>
    public IReadOnlyList<Matrix4> RestPoses => _restPose;

    /// <summary>
    /// WorldTransform of joint_1's parent node at the time the controller was built.
    /// Equals <c>GltfToScene × T(robotWorldPos)</c> for a standard cell, but uses the
    /// actual scene-graph value so intermediate-node transforms are included exactly.
    /// Used as the chain-root base transform in <see cref="GltfNumericalIkSolver"/>.
    /// </summary>
    public Matrix4 ChainRootTransform { get; private set; } = Matrix4.Identity;

    /// <summary>
    /// The GLTF "tcp" node, a child of joint_6 placed at the tool centre point.
    /// Its WorldTransform.Row3.Xyz gives the TCP world position each frame.
    /// Null if the node wasn't found in the robot model.
    /// </summary>
    public SceneNode? TcpNode { get; private set; }

    /// <summary>
    /// Rest-pose local transform of the tcp node (tcp relative to joint_6) in GLTF space.
    /// Identity if the tcp node wasn't found. Pass to <see cref="GltfNumericalIkSolver"/>
    /// so it can target TCP position rather than flange position.
    /// </summary>
    public Matrix4 TcpRestPose { get; private set; } = Matrix4.Identity;

    private RobotFkController() { }

    /// <summary>
    /// Searches <paramref name="robotRoot"/> for the six joint nodes and builds
    /// a controller. Returns <c>null</c> if none of the expected nodes are found.
    /// Also writes a diagnostic file listing all node names to help identify
    /// naming mismatches.
    /// </summary>
    public static RobotFkController? TryBuild(SceneNode robotRoot, IReadOnlyList<JointConfig> jointConfigs)
    {
        var ctrl  = new RobotFkController();
        int found = 0;

        for (int i = 0; i < 6; i++)
        {
            var node = robotRoot.FindDescendant(JointNames[i]);
            ctrl._joints[i]   = node;
            ctrl._restPose[i] = node?.LocalTransform ?? Matrix4.Identity;
            ctrl._jcfg[i]     = i < jointConfigs.Count ? jointConfigs[i] : new JointConfig();
            if (node != null) found++;
        }

        if (found > 0)
        {
            ctrl.TcpNode            = robotRoot.FindDescendant("tcp");
            ctrl.TcpRestPose        = ctrl.TcpNode?.LocalTransform ?? Matrix4.Identity;
            ctrl.ChainRootTransform = ctrl._joints[0]?.Parent?.WorldTransform ?? Matrix4.Identity;
        }

        return found > 0 ? ctrl : null;
    }

    /// <summary>
    /// Updates all joint local transforms for the given KRL angles (degrees).
    /// Call once per frame or whenever angles change.
    /// </summary>
    public void Apply(float a1, float a2, float a3, float a4, float a5, float a6)
    {
        float[] krl = [a1, a2, a3, a4, a5, a6];

        for (int i = 0; i < 6; i++)
        {
            if (_joints[i] is null) continue;

            var   cfg       = _jcfg[i];
            float boneAngle = cfg.KrlSign * krl[i] * MathF.PI / 180f;

            Matrix4 rot = cfg.Axis switch
            {
                Core.Kinematics.RotationAxis.X => Matrix4.CreateRotationX(boneAngle),
                Core.Kinematics.RotationAxis.Z => Matrix4.CreateRotationZ(boneAngle),
                _                              => Matrix4.CreateRotationY(boneAngle),
            };

            // Pre-multiply: rotate in joint-local space before applying the rest pose.
            _joints[i]!.LocalTransform = rot * _restPose[i];
        }
    }
}
