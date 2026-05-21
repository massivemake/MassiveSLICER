using OpenTK.Graphics.OpenGL4;

namespace MassiveSlicer.Viewport.Rendering;

/// <summary>
/// Compiles and links a GLSL vertex + fragment shader program.
/// Provides typed uniform setters and tracks the active program handle.
/// Dispose to release the GPU program object.
/// </summary>
public sealed class Shader : IDisposable
{
    private readonly int _handle;
    private bool _disposed;

    /// <summary>
    /// Compiles vertex and fragment GLSL source strings and links them into a program.
    /// Throws <see cref="Exception"/> on compile or link failure, with the info log attached.
    /// </summary>
    /// <param name="vertexSource">GLSL source for the vertex stage.</param>
    /// <param name="fragmentSource">GLSL source for the fragment stage.</param>
    public Shader(string vertexSource, string fragmentSource)
    {
        int vert = Compile(ShaderType.VertexShader,   vertexSource);
        int frag = Compile(ShaderType.FragmentShader, fragmentSource);

        _handle = GL.CreateProgram();
        GL.AttachShader(_handle, vert);
        GL.AttachShader(_handle, frag);
        GL.LinkProgram(_handle);

        GL.GetProgram(_handle, GetProgramParameterName.LinkStatus, out int linkStatus);
        if (linkStatus == 0)
        {
            string log = GL.GetProgramInfoLog(_handle);
            GL.DeleteProgram(_handle);
            throw new Exception($"Shader link failed:\n{log}");
        }

        // Shaders are no longer needed once linked into the program.
        GL.DetachShader(_handle, vert);
        GL.DetachShader(_handle, frag);
        GL.DeleteShader(vert);
        GL.DeleteShader(frag);
    }

    /// <summary>Activates this shader program for subsequent draw calls.</summary>
    public void Use() => GL.UseProgram(_handle);

    /// <summary>Sets a 4×4 matrix uniform by name.</summary>
    /// <remarks>
    /// Uses <c>transpose: true</c> because OpenTK stores matrices row-major while OpenGL
    /// expects column-major. Transposing on upload corrects the mismatch, so GLSL receives
    /// exactly the matrix computed in C# and row-vector GLSL arithmetic (<c>v * M</c>) works correctly.
    /// </remarks>
    public void SetMatrix4(string name, ref OpenTK.Mathematics.Matrix4 matrix)
    {
        int loc = GL.GetUniformLocation(_handle, name);
        GL.UniformMatrix4(loc, true, ref matrix);
    }

    /// <summary>Sets a 3×3 matrix uniform by name.</summary>
    public void SetMatrix3(string name, ref OpenTK.Mathematics.Matrix3 matrix)
    {
        int loc = GL.GetUniformLocation(_handle, name);
        GL.UniformMatrix3(loc, true, ref matrix);
    }

    /// <summary>Sets a vec3 uniform by name.</summary>
    public void SetVector3(string name, OpenTK.Mathematics.Vector3 value)
    {
        int loc = GL.GetUniformLocation(_handle, name);
        GL.Uniform3(loc, value);
    }

    /// <summary>Sets a float uniform by name.</summary>
    public void SetFloat(string name, float value)
    {
        int loc = GL.GetUniformLocation(_handle, name);
        GL.Uniform1(loc, value);
    }

    /// <summary>Sets an integer uniform by name (also used for sampler uniforms).</summary>
    public void SetInt(string name, int value)
    {
        int loc = GL.GetUniformLocation(_handle, name);
        GL.Uniform1(loc, value);
    }

    /// <summary>Sets a vec2 uniform by name.</summary>
    public void SetVector2(string name, OpenTK.Mathematics.Vector2 value)
    {
        int loc = GL.GetUniformLocation(_handle, name);
        GL.Uniform2(loc, value.X, value.Y);
    }

    /// <summary>Sets a vec4 colour uniform by name.</summary>
    public void SetVector4(string name, OpenTK.Mathematics.Vector4 value)
    {
        int loc = GL.GetUniformLocation(_handle, name);
        GL.Uniform4(loc, value);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!_disposed)
        {
            GL.DeleteProgram(_handle);
            _disposed = true;
        }
    }

    private static int Compile(ShaderType type, string source)
    {
        int shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);

        GL.GetShader(shader, ShaderParameter.CompileStatus, out int status);
        if (status == 0)
        {
            string log = GL.GetShaderInfoLog(shader);
            GL.DeleteShader(shader);
            throw new Exception($"{type} compile failed:\n{log}");
        }

        return shader;
    }
}
