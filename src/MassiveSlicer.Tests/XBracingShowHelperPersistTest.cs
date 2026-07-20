using System.Text.Json;
using System.Text.Json.Serialization;
using MassiveSlicer.Core.Models;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Workspace .mass save uses WhenWritingDefault — bool false must still round-trip
/// for XBracingShowHelper (property default is true).
/// </summary>
public sealed class XBracingShowHelperPersistTest
{
    private static readonly JsonSerializerOptions SaveLikeMass = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    private static readonly JsonSerializerOptions LoadLikeMass = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void AppPreferences_ShowHelperFalse_RoundTripsInMassSaveOptions()
    {
        var prefs = new AppPreferences { XBracingShowHelper = false };
        string json = JsonSerializer.Serialize(prefs, SaveLikeMass);
        Assert.Contains("XBracingShowHelper", json);
        Assert.Contains("false", json);

        var loaded = JsonSerializer.Deserialize<AppPreferences>(json, LoadLikeMass);
        Assert.NotNull(loaded);
        Assert.False(loaded.XBracingShowHelper);
    }

    [Fact]
    public void WorkspaceDocument_ShowHelperFalse_InSettingsAndUiSession()
    {
        var doc = new WorkspaceDocument
        {
            Settings = new AppPreferences { XBracingShowHelper = false },
            UiSession = new WorkspaceUiSession { XBracingShowHelper = false },
        };
        string json = JsonSerializer.Serialize(doc, SaveLikeMass);
        Assert.Contains("\"XBracingShowHelper\":false", json);

        var loaded = JsonSerializer.Deserialize<WorkspaceDocument>(json, LoadLikeMass);
        Assert.NotNull(loaded);
        Assert.False(loaded.Settings.XBracingShowHelper);
        Assert.False(loaded.UiSession!.XBracingShowHelper);
    }
}
