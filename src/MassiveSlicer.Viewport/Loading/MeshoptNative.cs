using System.Runtime.InteropServices;

namespace MassiveSlicer.Viewport.Loading;

/// <summary>P/Invoke to meshoptimizer.dll (bundled via Meshoptimizer.NET).</summary>
internal static class MeshoptNative
{
    private const string Lib = "meshoptimizer";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int meshopt_decodeIndexBuffer(
        IntPtr destination, nuint indexCount, nuint indexSize,
        IntPtr buffer, nuint bufferSize);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int meshopt_decodeIndexSequence(
        IntPtr destination, nuint indexCount, nuint indexSize,
        IntPtr buffer, nuint bufferSize);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int meshopt_decodeVertexBuffer(
        IntPtr destination, nuint vertexCount, nuint vertexSize,
        IntPtr buffer, nuint bufferSize);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void meshopt_decodeFilterOct(IntPtr buffer, nuint count, nuint stride);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void meshopt_decodeFilterQuat(IntPtr buffer, nuint count, nuint stride);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void meshopt_decodeFilterExp(IntPtr buffer, nuint count, nuint stride);
}