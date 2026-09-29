using OpenTK.Mathematics;
using MassiveSlicer.Viewport.Camera;

namespace MassiveSlicer.Tests;

/// <summary>
/// 2D slice view is elevation 90° orthographic. A click on a drawn line must
/// produce a pick ray that actually passes near that line. If the ray misses,
/// screen-near segments are rejected and points/lines feel unselectable.
/// </summary>
public sealed class SlicePlanePickRayTest
{
    [Fact]
    public void Top_down_ortho_ray_passes_through_the_clicked_line()
    {
        var cam = new OrbitCamera
        {
            Azimuth = -90f,
            Elevation = 90f,
            Radius = 2500f,
            Target = new Vector3(1500f, 800f, 120f),
            IsOrthographic = true,
        };

        const float vpW = 1200f;
        const float vpH = 800f;
        var viewProj = cam.GetViewMatrix() * cam.GetProjectionMatrix(vpW / vpH);

        var a = new Vector3(1400f, 700f, 123f);
        var b = new Vector3(1800f, 900f, 123f);
        var mid = (a + b) * 0.5f;
        var screen = Project(mid, viewProj, vpW, vpH);
        Assert.False(float.IsNaN(screen.X), "line midpoint did not project");

        var ray = cam.GetPickRay(screen.X, screen.Y, vpW, vpH);
        float dist = DistanceRayToSegment(ray.Origin, ray.Direction, a, b, out float rayT);

        Assert.True(rayT > 0f, $"rayT={rayT} origin={ray.Origin} dir={ray.Direction}");
        Assert.True(dist < 5f, $"ray missed the line by {dist:0.0} mm (rayT={rayT})");
    }

    static Vector2 Project(Vector3 world, Matrix4 mvp, float vpW, float vpH)
    {
        var clip = new Vector4(world, 1f) * mvp;
        if (clip.W <= 0f) return new Vector2(float.NaN);
        float invW = 1f / clip.W;
        return new Vector2(
            (clip.X * invW * 0.5f + 0.5f) * vpW,
            (1f - (clip.Y * invW * 0.5f + 0.5f)) * vpH);
    }

    static float DistanceRayToSegment(Vector3 rayO, Vector3 rayD, Vector3 a, Vector3 b, out float rayT)
    {
        var ab = b - a;
        float abLen2 = ab.LengthSquared;
        var w0 = rayO - a;
        float rb = Vector3.Dot(rayD, ab);
        float denom = abLen2 - rb * rb;
        float s, u;
        if (MathF.Abs(denom) < 1e-8f)
        {
            u = Math.Clamp(Vector3.Dot(rayO - a, ab) / abLen2, 0f, 1f);
            s = Vector3.Dot(a + ab * u - rayO, rayD);
        }
        else
        {
            float rd = Vector3.Dot(rayD, w0);
            float re = Vector3.Dot(ab, w0);
            s = (rb * re - abLen2 * rd) / denom;
            u = (re - rb * rd) / denom;
            u = Math.Clamp(u, 0f, 1f);
            s = Vector3.Dot(a + ab * u - rayO, rayD);
        }

        rayT = s;
        var onRay = rayO + rayD * MathF.Max(0f, s);
        var onSeg = a + ab * u;
        return (onRay - onSeg).Length;
    }
}
