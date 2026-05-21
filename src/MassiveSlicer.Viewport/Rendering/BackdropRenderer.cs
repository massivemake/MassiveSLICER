using System.IO;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using StbImageSharp;

namespace MassiveSlicer.Viewport.Rendering;

/// <summary>
/// Renders a spherical backdrop fixed to the camera using an equirectangular HDR image (.hdr).
/// The backdrop is drawn at maximum depth so all 3-D geometry always appears in front.
/// Must be created and disposed on the GL thread.
/// </summary>
public sealed class BackdropRenderer : IDisposable
{
    private readonly Shader _shader;
    private readonly int    _vao, _vbo, _tex;
    private bool _disposed;

    private static readonly string VertSrc = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec3 aRayDir;
        out vec3 vRayDir;
        void main() {
            gl_Position = vec4(aPos, 1.0, 1.0);
            vRayDir = aRayDir;
        }
        """;

    // Z-up right-hand equirectangular mapping.
    // atan(y,x) gives azimuth around Z; asin(z) gives elevation.
    // Linear→sRGB gamma correction matches how Unity displays the same images.
    private static readonly string FragSrc = """
        #version 330 core
        in vec3 vRayDir;
        uniform sampler2D uTex;
        uniform float uBlurLevel;
        const float PI = 3.14159265;
        out vec4 fragColor;
        void main() {
            vec3 dir = normalize(vRayDir);
            float u = atan(dir.y, dir.x) / (2.0 * PI) + 0.5;
            float v = 0.5 - asin(clamp(dir.z, -1.0, 1.0)) / PI;
            vec3 color = textureLod(uTex, vec2(u, v), uBlurLevel).rgb;
            color = pow(max(color, vec3(0.0)), vec3(1.0 / 2.2));
            fragColor = vec4(color, 1.0);
        }
        """;

    /// <summary>
    /// Loads <paramref name="path"/> as an equirectangular HDR image and uploads it
    /// as a 16-bit float GL texture with mipmaps for blur support.
    /// Must be called on the GL thread.
    /// </summary>
    public BackdropRenderer(string path)
    {
        ImageResultFloat img;
        using (var stream = File.OpenRead(path))
            img = ImageResultFloat.FromStream(stream, ColorComponents.RedGreenBlue);

        _tex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _tex);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgb16f,
                      img.Width, img.Height, 0,
                      PixelFormat.Rgb, PixelType.Float, img.Data);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                        (int)TextureMinFilter.LinearMipmapLinear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter,
                        (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS,
                        (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT,
                        (int)TextureWrapMode.ClampToEdge);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        GL.BindTexture(TextureTarget.Texture2D, 0);

        _shader = new Shader(VertSrc, FragSrc);

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, 6 * 5 * sizeof(float), IntPtr.Zero,
                      BufferUsageHint.DynamicDraw);
        int stride = 5 * sizeof(float);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 2 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.BindVertexArray(0);
    }

    /// <summary>
    /// Draws the backdrop for one frame. <paramref name="invViewProj"/> is the inverse
    /// of <c>view × proj</c> used to unproject frustum-corner rays into world space.
    /// Caller must disable depth writes and depth testing before this call.
    /// </summary>
    public void Draw(Matrix4 invViewProj, float blurLevel = 2.5f)
    {
        var bl = RayDir(-1f, -1f, invViewProj);
        var br = RayDir( 1f, -1f, invViewProj);
        var tr = RayDir( 1f,  1f, invViewProj);
        var tl = RayDir(-1f,  1f, invViewProj);

        Span<float> verts = stackalloc float[]
        {
            -1f, -1f,  bl.X, bl.Y, bl.Z,
             1f, -1f,  br.X, br.Y, br.Z,
             1f,  1f,  tr.X, tr.Y, tr.Z,
            -1f, -1f,  bl.X, bl.Y, bl.Z,
             1f,  1f,  tr.X, tr.Y, tr.Z,
            -1f,  1f,  tl.X, tl.Y, tl.Z,
        };

        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, verts.Length * sizeof(float), ref verts[0]);

        _shader.Use();
        _shader.SetFloat("uBlurLevel", blurLevel);
        _shader.SetInt("uTex", 0);

        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _tex);

        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
        GL.BindVertexArray(0);

        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shader.Dispose();
        GL.DeleteVertexArray(_vao);
        GL.DeleteBuffer(_vbo);
        GL.DeleteTexture(_tex);
    }

    private static Vector3 RayDir(float ndcX, float ndcY, Matrix4 invVP)
    {
        var near = RowTransform(new Vector4(ndcX, ndcY, -1f, 1f), invVP);
        var far  = RowTransform(new Vector4(ndcX, ndcY,  1f, 1f), invVP);
        near /= near.W;
        far  /= far.W;
        return Vector3.Normalize(far.Xyz - near.Xyz);
    }

    private static Vector4 RowTransform(Vector4 v, Matrix4 m) => new(
        v.X * m.M11 + v.Y * m.M21 + v.Z * m.M31 + v.W * m.M41,
        v.X * m.M12 + v.Y * m.M22 + v.Z * m.M32 + v.W * m.M42,
        v.X * m.M13 + v.Y * m.M23 + v.Z * m.M33 + v.W * m.M43,
        v.X * m.M14 + v.Y * m.M24 + v.Z * m.M34 + v.W * m.M44);
}
