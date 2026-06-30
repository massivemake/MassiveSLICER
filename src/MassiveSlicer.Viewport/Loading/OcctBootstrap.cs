namespace MassiveSlicer.Viewport.Loading;

/// <summary>
/// Ensures Open CASCADE native DLLs are on the search path before any Occt.NET types load.
/// Occt.NET's generated <c>OcctConfiguration</c> static constructor handles path setup.
/// </summary>
internal static class OcctBootstrap
{
    private static int _initialized;

    internal static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
            return;

        OcctConfiguration.Configure();
    }
}