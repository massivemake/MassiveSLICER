using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.Viewport.Loading;

/// <summary>
/// Tessellates STEP (.stp / .step) CAD files into a scene mesh.
/// On Windows we use open-source cascadio (Python/OCCT) rather than the third-party
/// Occt.NET binding, which pops a garbled license MessageBox and has been crashing
/// the host process on this machine.
/// Assumes Z-up millimetres (Rhino / CAD convention).
/// </summary>
public static class StepLoader
{
    public static SceneNode Load(string path, string? name = null)
        => CascadioStepConverter.Load(path, name);
}
