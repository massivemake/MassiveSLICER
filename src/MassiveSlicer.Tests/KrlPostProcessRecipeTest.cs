using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public class KrlPostProcessRecipeTest
{
    [Fact]
    public void Apply_overlays_lab_header_flags_and_clears_legacy_urm()
    {
        var export = new KrlExportSettings
        {
            ProgramName = "job",
            HeaderTemplate = "OLD HEADER",
            FooterTemplate = "OLD FOOTER",
            RobotModeEnabled = false,
            TravelStartStopEnabled = false,
            DigitalStartStopEnabled = true,
            ExtruderAirEnabled = false,
            ApoCvel = 10,
            ExtrusionStartWaitSec = 1f,
        };

        var recipe = new KrlPostProcessSettings
        {
            HeaderText = "LAB HEADER {{PROGRAM_NAME}}",
            FooterText = "LAB FOOTER",
            RobotModeEnabled = true,
            TravelStartStopEnabled = true,
            ExtruderAirEnabled = true,
            ApoCvel = 80,
            ExtrusionStartWaitSec = 0.25,
        };

        var applied = KrlPostProcessRecipe.Apply(export, recipe);

        Assert.Equal("LAB HEADER {{PROGRAM_NAME}}", applied.HeaderTemplate);
        Assert.Equal("LAB FOOTER", applied.FooterTemplate);
        Assert.True(applied.RobotModeEnabled);
        Assert.True(applied.TravelStartStopEnabled);
        Assert.False(applied.DigitalStartStopEnabled);
        Assert.True(applied.ExtruderAirEnabled);
        Assert.Equal(80, applied.ApoCvel);
        Assert.Equal(0.25f, applied.ExtrusionStartWaitSec);
    }

    [Fact]
    public void Apply_keeps_export_header_when_recipe_header_is_blank()
    {
        var export = new KrlExportSettings
        {
            ProgramName = "job",
            HeaderTemplate = "KEEP",
            FooterTemplate = "KEEPF",
            TravelStartStopEnabled = true,
        };
        var recipe = new KrlPostProcessSettings
        {
            HeaderText = "",
            FooterText = "  ",
        };

        var applied = KrlPostProcessRecipe.Apply(export, recipe);
        Assert.Equal("KEEP", applied.HeaderTemplate);
        Assert.Equal("KEEPF", applied.FooterTemplate);
        Assert.True(applied.TravelStartStopEnabled);
        Assert.False(applied.DigitalStartStopEnabled);
    }
}
