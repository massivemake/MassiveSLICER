using System.Text.Json;
using MassiveSlicer.App.Erp;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// ErpClient parsing is the isolation seam for ERP field drift (issue #955's
/// exact field names may change) — these tests pin both the documented shape
/// and a drifted variant so the seam stays honest.
/// </summary>
public class ErpParsingTest
{
    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ParsesIssue955Shape()
    {
        var hit = ErpClient.ParseHit(Parse("""
            {
              "type": "project", "id": "42", "number": "25-114",
              "title": "Stove Surround", "client": "Acme Corp",
              "elements": [
                { "id": "7", "name": "Left Panel", "elementNumber": "3", "revCount": 2 }
              ]
            }
            """));

        Assert.NotNull(hit);
        Assert.Equal("project", hit!.Type);
        Assert.Equal("25-114", hit.Number);
        Assert.Equal("Stove Surround", hit.Title);
        Assert.Equal("Acme Corp", hit.Client);
        Assert.Single(hit.Elements);
        Assert.Equal("3", hit.Elements[0].ElementNumber);
        Assert.Equal(2, hit.Elements[0].RevisionCount);
    }

    [Fact]
    public void ParsesDriftedShape()
    {
        // Numeric ids, renamed fields, PascalCase-ish keys.
        var hit = ErpClient.ParseHit(Parse("""
            {
              "Kind": "lead", "Id": 917, "no": "26-002",
              "name": "Mall Feature", "customer": "BuildCo",
              "elements": []
            }
            """));

        Assert.NotNull(hit);
        Assert.Equal("lead", hit!.Type);
        Assert.Equal("917", hit.Id);
        Assert.Equal("26-002", hit.Number);
        Assert.Equal("Mall Feature", hit.Title);
        Assert.Equal("BuildCo", hit.Client);
    }

    [Fact]
    public void ParsesElementDrift()
    {
        var el = ErpClient.ParseElement(Parse("""
            { "elementId": 12, "elementName": "Roof Cap", "element": 4, "revisionCount": "5" }
            """));

        Assert.NotNull(el);
        Assert.Equal("12", el!.Id);
        Assert.Equal("Roof Cap", el.Name);
        Assert.Equal("4", el.ElementNumber);
        Assert.Equal(5, el.RevisionCount);
    }

    [Fact]
    public void ParsesLiveErpSearchEnvelope()
    {
        // Verbatim shape from lab.massivemake.com /api/slicer/v1/search (2026-07-07):
        // projects and leads arrive as sibling arrays in one envelope.
        var root = Parse("""
            {
              "query": "curtain",
              "projects": [
                { "type": "project", "id": 498, "projectNumber": "25-102",
                  "name": "Animal Columns", "status": "Planning", "stage": "Design",
                  "elementCount": 0 }
              ],
              "leads": [
                { "type": "lead", "id": 173, "projectNumber": "26-173",
                  "name": "studio JEFRE llc - Curtain Sculpture, Blue Translucent with Lighting,",
                  "status": "need_followup", "stage": "Submitted" }
              ]
            }
            """);

        var hits = ErpClient.EnumerateArray(root, "projects", "leads", "results", "items", "data")
            .Select(ErpClient.ParseHit).Where(h => h is not null).Select(h => h!).ToList();

        Assert.Equal(2, hits.Count);
        Assert.Equal("25-102", hits[0].Number);
        Assert.Equal("project", hits[0].Type);
        Assert.Equal("26-173", hits[1].Number);
        Assert.Equal("lead", hits[1].Type);
    }

    [Fact]
    public void ParsesLiveErpElement()
    {
        var el = ErpClient.ParseElement(Parse("""
            { "id": 32, "elementNumber": 1, "name": "2025_1210 - TEA Bench",
              "description": "Material: PETG-GF", "quantity": 1, "status": "Completed",
              "sliceCount": 2, "latestRev": null, "latestSliceStatus": null }
            """));

        Assert.NotNull(el);
        Assert.Equal("32", el!.Id);
        Assert.Equal("1", el.ElementNumber);
        Assert.Equal(2, el.RevisionCount);
    }

    [Theory]
    [InlineData("https://erp.example.com", "https://erp.example.com/")]
    [InlineData("https://erp.example.com/", "https://erp.example.com/")]
    [InlineData("https://erp.example.com/api/slicer/v1", "https://erp.example.com/")]
    [InlineData("https://erp.example.com/api/slicer/v1/", "https://erp.example.com/")]
    [InlineData("https://erp.example.com/API/Slicer/V1", "https://erp.example.com/")]
    [InlineData("https://erp.example.com/api/slicer", "https://erp.example.com/")]
    [InlineData("  https://erp.example.com/api/slicer/v1  ", "https://erp.example.com/")]
    public void NormalizesPastedBaseUrls(string pasted, string expected)
        => Assert.Equal(expected, ErpClient.NormalizeBaseUrl(pasted));

    [Fact]
    public void ParsesCreatedElementEnvelopeAndBare()
    {
        // Element create responses accepted wrapped or bare.
        var wrapped = Parse("""{ "element": { "id": 101, "elementNumber": 1, "name": "Curtain Wall" } }""");
        var root = wrapped;
        Assert.True(root.TryGetProperty("element", out var inner));
        Assert.Equal("101", ErpClient.ParseElement(inner)!.Id);

        var bare = ErpClient.ParseElement(Parse("""{ "id": "9", "name": "Bare", "elementNumber": "2" }"""));
        Assert.Equal("9", bare!.Id);
    }

    [Fact]
    public void ShareRelativePathsStripTheVolumesMount()
    {
        Assert.Equal(
            "Projects/26-173 - x/06-Production Documents/file.mass",
            MassiveSlicer.ViewModels.MainWindowViewModel.ToUnasShareRelative(
                "/Volumes/MassiveFILES/Projects/26-173 - x/06-Production Documents/file.mass"));
        Assert.Null(MassiveSlicer.ViewModels.MainWindowViewModel.ToUnasShareRelative(
            "/Users/thom/Desktop/local.mass"));
        Assert.Null(MassiveSlicer.ViewModels.MainWindowViewModel.ToUnasShareRelative(
            "/Volumes/OnlyShare"));
    }

    [Fact]
    public void RejectsRecordsWithoutId()
    {
        Assert.Null(ErpClient.ParseHit(Parse("""{ "title": "no id" }""")));
        Assert.Null(ErpClient.ParseElement(Parse("""{ "name": "no id" }""")));
    }
}
