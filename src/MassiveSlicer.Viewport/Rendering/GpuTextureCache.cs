using MassiveSlicer.Viewport.Scene;
using OpenTK.Graphics.OpenGL4;

namespace MassiveSlicer.Viewport.Rendering;

/// <summary>
/// Reference-counted pool of GL texture handles keyed by <see cref="TextureData.CacheId"/>.
/// Mirrors <see cref="GpuMeshCache"/>: a decoded image shared by several materials (the loader
/// de-dupes by source image) uploads once and is reused across materials and cell swaps.
/// Must be called on the OpenGL thread.
/// </summary>
public static class GpuTextureCache
{
    private sealed class Entry
    {
        public int Handle;
        public int RefCount;
    }

    private static readonly Dictionary<int, Entry> _entries = new();

    /// <summary>Uploads <paramref name="tex"/> (once per CacheId) and returns its GL handle.</summary>
    public static int Acquire(TextureData tex)
    {
        if (!_entries.TryGetValue(tex.CacheId, out var entry))
        {
            entry = new Entry { Handle = Upload(tex), RefCount = 0 };
            _entries[tex.CacheId] = entry;
        }

        entry.RefCount++;
        return entry.Handle;
    }

    /// <summary>Decrements the ref count for a handle (mirrors GpuMeshCache: frees at DisposeAll).</summary>
    public static void Release(int handle)
    {
        if (handle == 0) return;
        foreach (var entry in _entries.Values)
        {
            if (entry.Handle != handle) continue;
            entry.RefCount = Math.Max(0, entry.RefCount - 1);
            return;
        }
    }

    /// <summary>Deletes all pooled GL textures (app shutdown / GL context teardown).</summary>
    public static void DisposeAll()
    {
        foreach (var entry in _entries.Values)
            if (entry.Handle != 0)
                GL.DeleteTexture(entry.Handle);
        _entries.Clear();
    }

    private static int Upload(TextureData tex)
    {
        int id = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, id);

        // sRGB internal format for colour maps (GPU decodes to linear at sample time);
        // plain RGBA8 for data maps (normal/MR/AO) which must stay linear.
        var internalFormat = tex.IsSrgb ? PixelInternalFormat.Srgb8Alpha8 : PixelInternalFormat.Rgba8;
        GL.TexImage2D(TextureTarget.Texture2D, 0, internalFormat, tex.Width, tex.Height, 0,
                      PixelFormat.Rgba, PixelType.UnsignedByte, tex.Pixels);

        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                        (int)TextureMinFilter.LinearMipmapLinear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                        (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)MapWrap(tex.WrapS));
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)MapWrap(tex.WrapT));
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);

        GL.BindTexture(TextureTarget.Texture2D, 0);
        return id;
    }

    private static TextureWrapMode MapWrap(TextureWrapKind kind) => kind switch
    {
        TextureWrapKind.ClampToEdge    => TextureWrapMode.ClampToEdge,
        TextureWrapKind.MirroredRepeat => TextureWrapMode.MirroredRepeat,
        _                               => TextureWrapMode.Repeat,
    };
}
