using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.Viewport.Loading;

/// <summary>
/// Non-Windows stub for <see cref="StepLoader"/>. Open CASCADE (Occt.NET 7.9.0) ships
/// only Windows x64 native libraries via a C++/CLI host, so STEP (.stp/.step) import is
/// unavailable on macOS/Linux. This mirrors the public surface of the Windows loader so
/// callers compile unchanged; <see cref="Load"/> throws and the import pipeline
/// (ImportHelper.LoadFile) treats it as an unreadable file rather than crashing.
/// </summary>
public static class StepLoader
{
    public static SceneNode Load(string path, string? name = null) =>
        throw new PlatformNotSupportedException(
            "STEP (.stp/.step) import requires Open CASCADE, which is only available on Windows.");
}
