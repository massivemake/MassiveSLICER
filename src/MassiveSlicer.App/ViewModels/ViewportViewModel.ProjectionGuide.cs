using MassiveSlicer.Commands;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.ViewModels;

public sealed partial class ViewportViewModel
{
    SceneNode? _projectionGuide;
    SceneNode? _projectionBottom;
    SceneNode? _projectionTop;
    OutlinerItemViewModel? _projectionOutliner;
    bool _projectionHooked;
    bool _suppressProjectionRebuild;
    RelayCommand<object>? _addProjectionGuideCommand;

    public RelayCommand<object> AddProjectionGuideCommand =>
        _addProjectionGuideCommand ??= new RelayCommand<object>(AddProjectionGuide);

    public bool IsProjectionGuideNode(SceneNode? node)
        => node is not null && (ReferenceEquals(node, _projectionGuide)
            || ReferenceEquals(node, _projectionBottom) || ReferenceEquals(node, _projectionTop));

    public bool IsProjectionEdgeNode(SceneNode? node)
        => node is not null && (ReferenceEquals(node, _projectionBottom) || ReferenceEquals(node, _projectionTop));

    /// <summary>World pose of the helper base. Axis Z is the shape axis, axis X carries the roll.</summary>
    public bool TryGetProjectionGuideFrame(
        out System.Numerics.Vector3 center,
        out System.Numerics.Vector3 axisX,
        out System.Numerics.Vector3 axisZ)
    {
        center = axisX = axisZ = default;
        if (_projectionGuide is null) return false;
        var w = _projectionGuide.WorldTransform;
        center = new System.Numerics.Vector3(w.M41, w.M42, w.M43);
        var x = new System.Numerics.Vector3(w.M11, w.M12, w.M13);
        var z = new System.Numerics.Vector3(w.M31, w.M32, w.M33);
        if (x.LengthSquared() < 1e-8f || z.LengthSquared() < 1e-8f) return false;
        axisX = System.Numerics.Vector3.Normalize(x);
        axisZ = System.Numerics.Vector3.Normalize(z);
        return true;
    }

    void AddProjectionGuide(object? shape)
    {
        var sub = SubtractiveSettings;
        if (sub is null) return;
        if (shape is string name && Enum.TryParse<ProjectionShape>(name, ignoreCase: true, out var parsed))
            sub.ProjectionShape = parsed;
        bool fresh = _projectionGuide is null;
        if (fresh) FitProjectionToPart(sub);
        EnsureProjectionHook();
        if (fresh)
        {
            var at = _fitCenter ?? ProjectionBedPoint();
            _fitCenter = null;
            _projectionGuide = NewGuideNode("Projection", BuildProjectionMesh(sub), Matrix4.CreateTranslation(at));
            var node = _projectionGuide;
            _projectionOutliner = CreateOutlinerItem(node, _ => RemoveProjectionGuide(), displayName: "Projection");
            OutlinerItems.Add(_projectionOutliner);
            PendingNodes.Enqueue(node);
            RequestSelectNode?.Invoke(node);
            if (ActiveGizmoModeInternal is GizmoMode.None)
                ActiveGizmoModeInternal = GizmoMode.Translate;
        }
        SyncProjectionEdges();
        NotifyRenderNeeded();
    }

    Vector3? _fitCenter;

    static SceneNode NewGuideNode(string name, MeshData mesh, Matrix4 local) => new()
    {
        Name = name,
        PendingMesh = mesh,
        Selectable = true,
        PickTier = PickTier.Content,
        TranslucentPass = true,
        KeepOwnMaterial = true,
        CullFaces = false,
        LocalTransform = local,
    };

    internal Action<SceneNode>? OnProjectionGuideRefresh { get; set; }

    void RemoveProjectionGuide()
    {
        if (_projectionGuide is null && _projectionOutliner is null) return;
        var node = _projectionGuide;
        _projectionGuide = null;
        _projectionBottom = null;
        _projectionTop = null;
        if (_projectionOutliner is not null)
        {
            OutlinerItems.Remove(_projectionOutliner);
            _projectionOutliner = null;
        }
        // The helper is parented under the bed, so a scene-wide lookup finds the
        // rotary group first and refuses to delete it. Remove this node directly.
        if (node is not null)
            PendingRemoveNodes.Enqueue(node);
        NotifyRenderNeeded();
    }

    /// <summary>
    /// A gizmo drag on the body or a cylinder end. Scale and a radial move become
    /// diameters. A move of the top end changes the height. The mesh is rebuilt
    /// at scale 1 so the next drag starts clean.
    /// </summary>
    internal void BakeProjectionGizmo(SceneNode node)
    {
        var sub = SubtractiveSettings;
        if (sub is null || _projectionGuide is null) return;
        _suppressProjectionRebuild = true;
        try
        {
            if (ReferenceEquals(node, _projectionGuide))
                BakeBody(sub, node);
            else if (ReferenceEquals(node, _projectionBottom))
                BakeEdge(sub, node, top: false);
            else if (ReferenceEquals(node, _projectionTop))
                BakeEdge(sub, node, top: true);
        }
        finally
        {
            _suppressProjectionRebuild = false;
        }
        RebuildProjectionMeshes();
    }

    static void BakeBody(SubtractiveSettingsViewModel sub, SceneNode node)
    {
        var m = node.LocalTransform;
        float sx = m.Row0.Xyz.Length;
        float sy = m.Row1.Xyz.Length;
        float sz = MathF.Max(0.05f, m.Row2.Xyz.Length);
        float sxy = MathF.Max(0.05f, (sx + sy) * 0.5f);
        if (MathF.Abs(sxy - 1f) <= 0.01f && MathF.Abs(sz - 1f) <= 0.01f)
            return;
        sub.ProjectionBaseDiameterMm *= sxy;
        sub.ProjectionTopDiameterMm *= sxy;
        sub.ProjectionHeightMm *= sz;
        var x = Vector3.Normalize(m.Row0.Xyz);
        var y = Vector3.Normalize(m.Row1.Xyz);
        var z = Vector3.Normalize(m.Row2.Xyz);
        var t = m.Row3.Xyz;
        var kept = m;
        kept.Row0 = new Vector4(x, 0);
        kept.Row1 = new Vector4(y, 0);
        kept.Row2 = new Vector4(z, 0);
        kept.Row3 = new Vector4(t, 1);
        node.LocalTransform = kept;
    }

    void BakeEdge(SubtractiveSettingsViewModel sub, SceneNode edge, bool top)
    {
        var m = edge.LocalTransform;
        float sxy = MathF.Max(0.05f, (m.Row0.Xyz.Length + m.Row1.Xyz.Length) * 0.5f);
        float radial = MathF.Sqrt(m.M41 * m.M41 + m.M42 * m.M42);
        if (top)
        {
            sub.ProjectionTopDiameterMm = sub.ProjectionTopDiameterMm * sxy + radial * 2f;
            sub.ProjectionHeightMm += m.M43;
        }
        else
        {
            sub.ProjectionBaseDiameterMm = sub.ProjectionBaseDiameterMm * sxy + radial * 2f;
            if (MathF.Abs(m.M43) > 0.5f && _projectionGuide is not null)
            {
                sub.ProjectionHeightMm -= m.M43;
                var parent = _projectionGuide.LocalTransform;
                var axis = parent.Row2.Xyz;
                if (axis.LengthSquared > 1e-8f) axis = Vector3.Normalize(axis);
                else axis = Vector3.UnitZ;
                var t = parent.Row3.Xyz + axis * m.M43;
                parent.Row3 = new Vector4(t, 1);
                _projectionGuide.LocalTransform = parent;
            }
        }
        edge.LocalTransform = Matrix4.Identity;
    }

    void EnsureProjectionHook()
    {
        if (_projectionHooked || SubtractiveSettings is null) return;
        _projectionHooked = true;
        SubtractiveSettings.PropertyChanged += (_, e) =>
        {
            if (_suppressProjectionRebuild || _projectionGuide is null) return;
            if (e.PropertyName is nameof(SubtractiveSettingsViewModel.ProjectionShape)
                or nameof(SubtractiveSettingsViewModel.ProjectionBaseDiameterMm)
                or nameof(SubtractiveSettingsViewModel.ProjectionTopDiameterMm)
                or nameof(SubtractiveSettingsViewModel.ProjectionHeightMm))
                RebuildProjectionMeshes();
        };
    }

    void RebuildProjectionMeshes()
    {
        if (_projectionGuide is null || SubtractiveSettings is null) return;
        _projectionGuide.PendingMesh = BuildProjectionMesh(SubtractiveSettings);
        SyncProjectionEdges();
        PendingModelRefresh.Enqueue(_projectionGuide);
        NotifyRenderNeeded();
    }

    void SyncProjectionEdges()
    {
        if (_projectionGuide is null || SubtractiveSettings is null) return;
        bool cylinder = SubtractiveSettings.ProjectionShape == ProjectionShape.Cylinder;
        if (!cylinder)
        {
            DropEdge(ref _projectionBottom);
            DropEdge(ref _projectionTop);
            return;
        }
        float baseR = MathF.Max(5f, (float)SubtractiveSettings.ProjectionBaseDiameterMm * 0.5f);
        float topR = MathF.Max(5f, (float)SubtractiveSettings.ProjectionTopDiameterMm * 0.5f);
        float height = MathF.Max(5f, (float)SubtractiveSettings.ProjectionHeightMm);
        _projectionBottom = EnsureEdge(_projectionBottom, "Projection Bottom", baseR, 0f);
        _projectionTop = EnsureEdge(_projectionTop, "Projection Top", topR, height);
    }

    SceneNode EnsureEdge(SceneNode? edge, string name, float radius, float z)
    {
        var mesh = RingMesh(radius, z);
        if (edge is null)
        {
            edge = NewGuideNode(name, mesh, Matrix4.Identity);
            _projectionGuide!.AddChild(edge);
            return edge;
        }
        edge.PendingMesh = mesh;
        edge.LocalTransform = Matrix4.Identity;
        return edge;
    }

    void DropEdge(ref SceneNode? edge)
    {
        if (edge is null) return;
        edge.Parent?.RemoveChild(edge);
        edge = null;
    }

    void FitProjectionToPart(SubtractiveSettingsViewModel sub)
    {
        bool any = false;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var item in EnumerateUserModelItems())
        {
            if (item.IsToolpath || IsProjectionGuideNode(item.Node)) continue;
            foreach (var n in item.Node.SelfAndDescendants())
            {
                var mesh = n.Mesh?.PickingData ?? n.PendingMesh;
                if (mesh is null || mesh.Positions.Length == 0) continue;
                var (b0, b1) = mesh.LocalBounds;
                var w = n.WorldTransform;
                for (int c = 0; c < 8; c++)
                {
                    var p = Vector3.TransformPosition(new Vector3(
                        (c & 1) == 0 ? b0.X : b1.X,
                        (c & 2) == 0 ? b0.Y : b1.Y,
                        (c & 4) == 0 ? b0.Z : b1.Z), w);
                    min = Vector3.ComponentMin(min, p);
                    max = Vector3.ComponentMax(max, p);
                    any = true;
                }
            }
        }
        if (!any) return;
        float dia = MathF.Max(max.X - min.X, max.Y - min.Y) + 300f;
        float height = MathF.Max(10f, max.Z - min.Z) + 300f;
        sub.ProjectionBaseDiameterMm = dia;
        sub.ProjectionTopDiameterMm = dia;
        sub.ProjectionHeightMm = height;
        _fitCenter = new Vector3((min.X + max.X) * 0.5f, (min.Y + max.Y) * 0.5f, min.Z);
    }

    static MeshData RingMesh(float radius, float z)
    {
        const int seg = 40;
        const float tube = 14f;
        var pos = new List<Vector3>();
        var nrm = new List<Vector3>();
        var idx = new List<uint>();
        for (int i = 0; i < seg; i++)
        {
            float a0 = i / (float)seg * MathF.Tau;
            float a1 = (i + 1) / (float)seg * MathF.Tau;
            var p0 = new Vector3(MathF.Cos(a0) * radius, MathF.Sin(a0) * radius, z);
            var p1 = new Vector3(MathF.Cos(a1) * radius, MathF.Sin(a1) * radius, z);
            var out0 = new Vector3(MathF.Cos(a0), MathF.Sin(a0), 0);
            var out1 = new Vector3(MathF.Cos(a1), MathF.Sin(a1), 0);
            uint b = (uint)pos.Count;
            pos.Add(p0 - out0 * tube); nrm.Add(-out0);
            pos.Add(p0 + out0 * tube); nrm.Add(out0);
            pos.Add(p1 + out1 * tube); nrm.Add(out1);
            pos.Add(p1 - out1 * tube); nrm.Add(-out1);
            idx.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
        }
        var color = new Vector4(0.55f, 0.95f, 0.72f, 0.9f);
        return new MeshData(pos.ToArray(), nrm.ToArray(), idx.ToArray(), "Projection edge", color, 0.1f, 0.35f);
    }

    Vector3 ProjectionBedPoint()
    {
        if (ComputeBedCenter() is { } bed)
            return new Vector3(bed.X, bed.Y, bed.Z - 400f);
        return Vector3.Zero;
    }

    static MeshData BuildProjectionMesh(SubtractiveSettingsViewModel sub)
    {
        float baseR = MathF.Max(5f, (float)sub.ProjectionBaseDiameterMm * 0.5f);
        float topR = MathF.Max(5f, (float)sub.ProjectionTopDiameterMm * 0.5f);
        float height = MathF.Max(5f, (float)sub.ProjectionHeightMm);
        var (pos, nrm, idx) = sub.ProjectionShape switch
        {
            ProjectionShape.Sphere => SphereMesh(baseR),
            ProjectionShape.Cube => BoxMesh(baseR * 2f, height),
            _ => FrustumMesh(baseR, topR, height),
        };
        var color = new Vector4(0.35f, 0.78f, 0.95f, 0.35f);
        return new MeshData(pos, nrm, idx, "Projection", color, 0.05f, 0.4f);
    }

    static (Vector3[] Pos, Vector3[] Nrm, uint[] Idx) FrustumMesh(float baseR, float topR, float height)
    {
        const int seg = 28;
        var pos = new List<Vector3>();
        var nrm = new List<Vector3>();
        var idx = new List<uint>();
        float drdz = (topR - baseR) / height;
        for (int i = 0; i <= seg; i++)
        {
            float ang = i / (float)seg * MathF.Tau;
            float c = MathF.Cos(ang), s = MathF.Sin(ang);
            var n = Vector3.Normalize(new Vector3(c, s, -drdz));
            pos.Add(new Vector3(c * baseR, s * baseR, 0));
            nrm.Add(n);
            pos.Add(new Vector3(c * topR, s * topR, height));
            nrm.Add(n);
        }
        for (int i = 0; i < seg; i++)
        {
            uint a = (uint)(i * 2);
            idx.AddRange([a, a + 1, a + 2, a + 1, a + 3, a + 2]);
        }
        return (pos.ToArray(), nrm.ToArray(), idx.ToArray());
    }

    static (Vector3[] Pos, Vector3[] Nrm, uint[] Idx) SphereMesh(float radius)
    {
        const int lat = 16, lon = 24;
        var pos = new List<Vector3>();
        var nrm = new List<Vector3>();
        var idx = new List<uint>();
        for (int i = 0; i <= lat; i++)
        {
            float v = i / (float)lat * MathF.PI;
            float sv = MathF.Sin(v), cv = MathF.Cos(v);
            for (int j = 0; j <= lon; j++)
            {
                float u = j / (float)lon * MathF.Tau;
                var n = new Vector3(sv * MathF.Cos(u), sv * MathF.Sin(u), cv);
                nrm.Add(n);
                pos.Add(n * radius + new Vector3(0, 0, radius));
            }
        }
        int stride = lon + 1;
        for (int i = 0; i < lat; i++)
        for (int j = 0; j < lon; j++)
        {
            uint a = (uint)(i * stride + j);
            idx.AddRange([a, a + (uint)stride, a + 1, a + 1, a + (uint)stride, a + (uint)stride + 1]);
        }
        return (pos.ToArray(), nrm.ToArray(), idx.ToArray());
    }

    static (Vector3[] Pos, Vector3[] Nrm, uint[] Idx) BoxMesh(float size, float height)
    {
        float x = size * 0.5f;
        var corners = new[]
        {
            new Vector3(-x, -x, 0), new Vector3(x, -x, 0), new Vector3(x, x, 0), new Vector3(-x, x, 0),
            new Vector3(-x, -x, height), new Vector3(x, -x, height), new Vector3(x, x, height), new Vector3(-x, x, height),
        };
        int[] faces =
        [
            0, 1, 2, 0, 2, 3,
            4, 6, 5, 4, 7, 6,
            0, 4, 5, 0, 5, 1,
            3, 2, 6, 3, 6, 7,
            0, 3, 7, 0, 7, 4,
            1, 5, 6, 1, 6, 2,
        ];
        var pos = new Vector3[faces.Length];
        var nrm = new Vector3[faces.Length];
        var idx = new uint[faces.Length];
        for (int i = 0; i < faces.Length; i += 3)
        {
            var a = corners[faces[i]];
            var b = corners[faces[i + 1]];
            var c = corners[faces[i + 2]];
            var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            pos[i] = a; pos[i + 1] = b; pos[i + 2] = c;
            nrm[i] = nrm[i + 1] = nrm[i + 2] = n;
            idx[i] = (uint)i; idx[i + 1] = (uint)i + 1; idx[i + 2] = (uint)i + 2;
        }
        return (pos, nrm, idx);
    }
}
