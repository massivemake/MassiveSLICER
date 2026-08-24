using MassiveSlicer.App.Erp;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using Xunit;

namespace MassiveSlicer.Tests;

public class KrlPostProcessDocumentTest
{
    [Fact]
    public void ParsesBareFactoryFile()
    {
        const string json = """
        {
          "HeaderText": "DEF {{PROGRAM_NAME}}()",
          "FooterText": "END",
          "RulesSaved": true,
          "RobotModeEnabled": true,
          "TravelStartStopEnabled": false,
          "ApoCvel": 50
        }
        """;
        Assert.True(KrlPostProcessDocument.TryParse(json, out var s, out var err), err);
        Assert.Equal("DEF {{PROGRAM_NAME}}()", s.HeaderText);
        Assert.Equal("END", s.FooterText);
        Assert.True(s.RobotModeEnabled);
        Assert.False(s.TravelStartStopEnabled);
        Assert.Equal(50, s.ApoCvel);
    }

    [Fact]
    public void ParsesEnvelopeAndRoundTrips()
    {
        var original = new KrlPostProcessSettings
        {
            RulesSaved = true,
            HeaderText = "HDR",
            FooterText = "FTR",
            RobotModeEnabled = true,
            TravelStartStopEnabled = true,
            ApoCvel = 80,
            ExtrusionResumeWaitSec = 0.5,
        };
        var json = KrlPostProcessDocument.SerializeEnvelope(original);
        Assert.Contains("MassiveSLICER.KrlPostProcess", json);
        Assert.True(KrlPostProcessDocument.TryParse(json, out var s, out var err), err);
        Assert.Equal("HDR", s.HeaderText);
        Assert.Equal("FTR", s.FooterText);
        Assert.True(s.TravelStartStopEnabled);
        Assert.Equal(80, s.ApoCvel);
        Assert.Equal(1, s.SchemaVersion);
        Assert.NotNull(s.UpdatedAtUtc);
    }

    [Fact]
    public void RejectsGarbage()
    {
        Assert.False(KrlPostProcessDocument.TryParse("not json", out _, out var err));
        Assert.False(string.IsNullOrWhiteSpace(err));
    }

    [Fact]
    public void ParsesKrlPostProcessInPresetsBundle()
    {
        var bundle = ErpClient.ParsePresetsBundle(JsonDocumentParse("""
        {
          "version": "v3",
          "printPresets": [],
          "materialPresets": [],
          "millTools": [],
          "krlPostProcess": {
            "id": "default",
            "updatedAt": "2026-08-21T18:00:00Z",
            "updatedBy": "thom@massivemake.com",
            "payload": { "RulesSaved": true, "RobotModeEnabled": true, "ApoCvel": 40, "HeaderText": "H" }
          }
        }
        """));
        Assert.NotNull(bundle.KrlPostProcess);
        Assert.Equal("default", bundle.KrlPostProcess!.Id);
        Assert.Contains("RobotModeEnabled", bundle.KrlPostProcess.PayloadJson);
        Assert.Contains("\"H\"", bundle.KrlPostProcess.PayloadJson);
    }

    static System.Text.Json.JsonElement JsonDocumentParse(string json)
        => System.Text.Json.JsonDocument.Parse(json).RootElement;
}
