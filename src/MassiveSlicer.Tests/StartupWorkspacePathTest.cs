namespace MassiveSlicer.Tests;

public class StartupWorkspacePathTest
{
    [Fact]
    public void ResolveStartupWorkspacePath_returns_null_for_empty_args()
    {
        Assert.Null(MassiveSlicer.App.App.ResolveStartupWorkspacePath([]));
        Assert.Null(MassiveSlicer.App.App.ResolveStartupWorkspacePath(null));
    }

    [Fact]
    public void ResolveStartupWorkspacePath_ignores_non_mass_args()
    {
        Assert.Null(MassiveSlicer.App.App.ResolveStartupWorkspacePath(["--help", "model.glb"]));
    }

    [Fact]
    public void ResolveStartupWorkspacePath_accepts_existing_mass_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"massive-test-{Guid.NewGuid():N}.mass");
        try
        {
            File.WriteAllText(path, "{}");
            var resolved = MassiveSlicer.App.App.ResolveStartupWorkspacePath([$"\"{path}\""]);
            Assert.Equal(Path.GetFullPath(path), resolved);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}