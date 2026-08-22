using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public sealed class KrlPostProcessLoaderTest
{
    [Fact]
    public void FactoryPath_is_the_repo_assets_file_not_bin()
    {
        string path = KrlPostProcessLoader.FactoryPath();
        Assert.EndsWith("assets/krl_postprocess.json", path.Replace('\\', '/'));
        Assert.DoesNotContain("/bin/", path.Replace('\\', '/'));
        Assert.True(File.Exists(path), path);
    }

    [Fact]
    public void Load_round_trips_rules()
    {
        var loaded = KrlPostProcessLoader.Load();
        Assert.True(loaded.RulesSaved);
        Assert.True(loaded.TravelStartStopEnabled);
        Assert.NotNull(loaded.CodeEditorInject);
        Assert.Equal("Before", loaded.CodeEditorInject!.StopDirection);
        Assert.Equal(350.0, loaded.CodeEditorInject.StopDistance);
    }
}
