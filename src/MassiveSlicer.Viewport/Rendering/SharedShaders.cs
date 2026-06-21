namespace MassiveSlicer.Viewport.Rendering;

/// <summary>Process-wide GL programs reused by all mesh draw calls.</summary>
internal static class SharedShaders
{
    private static Shader? _meshPhong;

    /// <summary>Phong/Lambert mesh shader — compiled once per GL context lifetime.</summary>
    public static Shader MeshPhong
        => _meshPhong ??= new Shader(MeshRenderer.VertSrc, MeshRenderer.FragSrc);
}