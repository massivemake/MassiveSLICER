namespace MassiveSlicer.Viewport.Scene;

/// <summary>Texture wrap behaviour, mirrors the glTF sampler wrap modes (GL-free so
/// <c>Scene/</c> has no OpenTK.Graphics dependency; mapped to GL enums in the renderer).</summary>
public enum TextureWrapKind
{
    Repeat,
    ClampToEdge,
    MirroredRepeat,
}

/// <summary>
/// CPU-side decoded texture (tightly packed RGBA8) ready for GPU upload.
/// Produced by <see cref="Loading.GltfLoader"/> and uploaded once per <see cref="CacheId"/>
/// by <see cref="Rendering.GpuTextureCache"/> on the GL thread.
/// <para>
/// Immutable and shared by reference: the loader de-duplicates source images so a glTF image
/// referenced by several materials yields a single <see cref="TextureData"/> instance, which
/// in turn maps to a single GPU texture handle (pooled across cell swaps).
/// </para>
/// </summary>
public sealed class TextureData
{
    private static int _nextCacheId;

    /// <summary>Stable id for GPU texture pooling. Mirrors <see cref="MeshData.CacheId"/>.</summary>
    public int CacheId { get; } = Interlocked.Increment(ref _nextCacheId);

    /// <summary>Decoded pixels, 4 bytes (RGBA) per texel, row-major, <see cref="Width"/>×<see cref="Height"/>.</summary>
    public byte[] Pixels { get; }

    public int Width { get; }
    public int Height { get; }

    /// <summary>
    /// True for colour textures authored in sRGB (base colour, emissive) — uploaded as an
    /// sRGB internal format so the GPU decodes to linear at sample time. False for data
    /// textures (normal, metallic-roughness, occlusion) which stay linear.
    /// </summary>
    public bool IsSrgb { get; }

    public TextureWrapKind WrapS { get; }
    public TextureWrapKind WrapT { get; }

    public TextureData(byte[] pixels, int width, int height, bool isSrgb,
                       TextureWrapKind wrapS = TextureWrapKind.Repeat,
                       TextureWrapKind wrapT = TextureWrapKind.Repeat)
    {
        Pixels = pixels;
        Width  = width;
        Height = height;
        IsSrgb = isSrgb;
        WrapS  = wrapS;
        WrapT  = wrapT;
    }
}
