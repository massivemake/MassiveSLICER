using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.Viewport.Loading;

/// <summary>
/// STEP (.stp/.step) import for macOS/Linux — same cascadio converter as Windows.
/// </summary>
public static class StepLoader
{
    public static SceneNode Load(string path, string? name = null)
        => CascadioStepConverter.Load(path, name);
}
