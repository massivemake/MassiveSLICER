using MassiveSlicer.Viewport.Scene;
using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Camera;

/// <summary>
/// Spherical orbit camera with a Z-up right-hand coordinate system.
/// Position is expressed in spherical coordinates (azimuth, elevation, radius)
/// relative to a world-space <see cref="Target"/> point.
/// All input handlers apply changes immediately -- there is no smoothing or inertia.
/// </summary>
public sealed class OrbitCamera
{
    // -- Spherical coordinates ---------------------------------------------

    /// <summary>Horizontal rotation around the Z axis, in degrees.</summary>
    public float Azimuth { get; set; } = 45f;

    /// <summary>
    /// Vertical angle above the XY plane, in degrees.
    /// Wraps within ±180deg -- the camera can orbit continuously over and under the target.
    /// </summary>
    public float Elevation { get; set; } = 30f;

    /// <summary>Distance from <see cref="Target"/> to the camera eye, in world units (mm).</summary>
    public float Radius { get; set; } = 3000f;

    /// <summary>World-space point the camera orbits around and looks at.</summary>
    public Vector3 Target { get; set; } = Vector3.Zero;

    // -- Projection --------------------------------------------------------

    /// <summary>Vertical field of view in degrees.</summary>
    public float FovDegrees { get; set; } = 85f;

    /// <summary>Near clip plane distance in mm.</summary>
    public float NearClip { get; set; } = 1f;

    /// <summary>Far clip plane distance in mm.</summary>
    public float FarClip { get; set; } = 100_000f;

    // -- Derived state -----------------------------------------------------

    /// <summary>World-space position of the camera eye, computed from spherical coordinates.</summary>
    public Vector3 Eye
    {
        get
        {
            float azRad  = MathHelper.DegreesToRadians(Azimuth);
            float elRad  = MathHelper.DegreesToRadians(Elevation);
            float cosEl  = MathF.Cos(elRad);
            return Target + new Vector3(
                Radius * cosEl * MathF.Cos(azRad),
                Radius * cosEl * MathF.Sin(azRad),
                Radius * MathF.Sin(elRad));
        }
    }

    // -- Matrix accessors --------------------------------------------------

    /// <summary>
    /// Returns the view matrix (world -> camera space).
    /// Uses Gram-Schmidt to derive the up vector: project world-Z onto the plane
    /// perpendicular to the look direction. This is smooth at all elevations and
    /// correctly flips when the camera orbits past a pole, without the hard
    /// threshold jump that causes a visible flicker near ±90deg.
    /// </summary>
    public Matrix4 GetViewMatrix()
    {
        var eye     = Eye;
        var lookDir = Vector3.Normalize(Target - eye);

        // Remove the look-direction component from world-Z to get "sky" up.
        var upRaw = Vector3.UnitZ - Vector3.Dot(Vector3.UnitZ, lookDir) * lookDir;

        Vector3 up;
        if (upRaw.LengthSquared > 1e-6f)
            up = Vector3.Normalize(upRaw);
        else
            // Exactly at a pole: derive a consistent up from azimuth so the
            // fallback matches the continuous limit of the formula above.
            up = new Vector3(-MathF.Cos(MathHelper.DegreesToRadians(Azimuth)),
                             -MathF.Sin(MathHelper.DegreesToRadians(Azimuth)), 0f);

        return Matrix4.LookAt(eye, Target, up);
    }

    /// <summary>Returns the perspective projection matrix for the given viewport aspect ratio.</summary>
    /// <param name="aspectRatio">Viewport width divided by height.</param>
    public Matrix4 GetProjectionMatrix(float aspectRatio)
        => Matrix4.CreatePerspectiveFieldOfView(
               MathHelper.DegreesToRadians(FovDegrees),
               aspectRatio,
               NearClip,
               FarClip);

    // -- Picking -----------------------------------------------------------

    /// <summary>
    /// Constructs a world-space ray from a screen-space mouse position.
    /// Uses row-vector convention consistent with the rest of the renderer.
    /// </summary>
    /// <param name="screenX">Mouse X in viewport pixels.</param>
    /// <param name="screenY">Mouse Y in viewport pixels.</param>
    /// <param name="viewportWidth">Viewport width in pixels.</param>
    /// <param name="viewportHeight">Viewport height in pixels.</param>
    public Ray GetPickRay(float screenX, float screenY, float viewportWidth, float viewportHeight)
    {
        float ndcX =  screenX / viewportWidth  * 2f - 1f;
        float ndcY = -(screenY / viewportHeight * 2f - 1f); // flip: screen Y-down -> NDC Y-up

        float aspect = viewportWidth / viewportHeight;
        var view = GetViewMatrix();
        var proj = GetProjectionMatrix(aspect);
        Matrix4.Invert(view * proj, out var invVP);

        var nearClip = new Vector4(ndcX, ndcY, -1f, 1f);
        var farClip  = new Vector4(ndcX, ndcY,  1f, 1f);

        var nearWorld = RowTransform(nearClip, invVP);
        var farWorld  = RowTransform(farClip,  invVP);
        nearWorld /= nearWorld.W;
        farWorld  /= farWorld.W;

        var origin    = nearWorld.Xyz;
        var direction = Vector3.Normalize(farWorld.Xyz - origin);
        return new Ray(origin, direction);
    }

    private static Vector4 RowTransform(Vector4 v, Matrix4 m)
        => new(
            v.X * m.M11 + v.Y * m.M21 + v.Z * m.M31 + v.W * m.M41,
            v.X * m.M12 + v.Y * m.M22 + v.Z * m.M32 + v.W * m.M42,
            v.X * m.M13 + v.Y * m.M23 + v.Z * m.M33 + v.W * m.M43,
            v.X * m.M14 + v.Y * m.M24 + v.Z * m.M34 + v.W * m.M44);

    // -- Input handlers ----------------------------------------------------

    /// <summary>
    /// Orbits the camera by rotating azimuth and elevation.
    /// Applied immediately with no smoothing.
    /// </summary>
    /// <param name="deltaAzimuth">Change in azimuth angle, degrees. Positive = rotate right.</param>
    /// <param name="deltaElevation">Change in elevation angle, degrees. Positive = rotate up.</param>
    public void Orbit(float deltaAzimuth, float deltaElevation)
    {
        Azimuth    = (Azimuth + deltaAzimuth) % 360f;
        // Clamp just short of the poles so the Gram-Schmidt up vector never
        // degenerates and the camera never flips upside-down.
        Elevation  = Math.Clamp(Elevation + deltaElevation, -89.9f, 89.9f);
    }

    /// <summary>
    /// Pans the camera by shifting <see cref="Target"/> perpendicular to the view direction.
    /// Pan speed scales with <see cref="Radius"/> so movement feels consistent at all zoom levels.
    /// </summary>
    /// <param name="deltaX">Screen-space horizontal delta in pixels (positive = pan right).</param>
    /// <param name="deltaY">Screen-space vertical delta in pixels (positive = pan up).</param>
    /// <param name="viewportWidth">Current viewport width in pixels.</param>
    /// <param name="viewportHeight">Current viewport height in pixels.</param>
    public void Pan(float deltaX, float deltaY, float viewportWidth, float viewportHeight)
    {
        float azRad = MathHelper.DegreesToRadians(Azimuth);
        float elRad = MathHelper.DegreesToRadians(Elevation);

        // Camera right: always horizontal (Z = 0) for a Z-up orbit camera,
        // rotated by azimuth only. Derived analytically -- no matrix extraction needed.
        var right = new Vector3(-MathF.Sin(azRad), MathF.Cos(azRad), 0f);

        // Camera up: perpendicular to right and the view direction.
        // This tilts with elevation so pan tracks the screen axes correctly at any angle.
        var up = new Vector3(
            -MathF.Cos(azRad) * MathF.Sin(elRad),
            -MathF.Sin(azRad) * MathF.Sin(elRad),
             MathF.Cos(elRad));

        float scale = Radius / MathF.Min(viewportWidth, viewportHeight);

        Target -= right * (deltaX * scale);
        Target += up    * (deltaY * scale);
    }

    /// <summary>
    /// Zooms by scaling <see cref="Radius"/> multiplicatively so that zooming
    /// always feels proportional regardless of distance.
    /// </summary>
    /// <param name="scrollDelta">Positive = zoom in (decrease radius), negative = zoom out.</param>
    public void Zoom(float scrollDelta)
    {
        const float ZoomFactor = 0.1f;
        Radius *= 1f - scrollDelta * ZoomFactor;
        Radius = Math.Clamp(Radius, 10f, 100_000f);
    }
}
