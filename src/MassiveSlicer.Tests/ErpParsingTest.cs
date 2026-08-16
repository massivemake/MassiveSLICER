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
    public void ErrorBodiesSurfaceTheServersMessage()
    {
        // ERP #961: human-readable errors shown verbatim.
        Assert.Equal(
            "Leads can't own elements — convert the lead to a project first.",
            ErpClient.ExtractErrorMessage(
                """{ "message": "Leads can't own elements — convert the lead to a project first." }""", 400));
        Assert.Equal("Not found", ErpClient.ExtractErrorMessage("""{ "message": "Not found" }""", 404));
        Assert.Equal("HTTP 500", ErpClient.ExtractErrorMessage("<html>oops</html>", 500));
        Assert.Equal("HTTP 400", ErpClient.ExtractErrorMessage("", 400));
    }

    [Fact]
    public void ConvertedLeadHintYieldsTheLinkedProjectId()
    {
        Assert.Equal("43", ErpClient.ExtractLinkedProjectId(
            """{ "message": "Lead 26-173 was converted to project 25-120.", "projectId": 43 }"""));
        Assert.Null(ErpClient.ExtractLinkedProjectId(
            """{ "message": "Leads can't own elements." }"""));
        Assert.Null(ErpClient.ExtractLinkedProjectId(null));
        Assert.Null(ErpClient.ExtractLinkedProjectId("not json"));
    }

    [Fact]
    public void RevisionFoldersCountUpFromExisting()
    {
        string dir = Directory.CreateTempSubdirectory("msl-rev").FullName;
        try
        {
            Assert.EndsWith("Rev 1", MassiveSlicer.ViewModels.MainWindowViewModel.NextRevisionDir(dir));
            Directory.CreateDirectory(Path.Combine(dir, "Rev 1"));
            Directory.CreateDirectory(Path.Combine(dir, "Rev 3"));   // gap: user deleted Rev 2
            Directory.CreateDirectory(Path.Combine(dir, "rev 7"));   // case-insensitive
            Directory.CreateDirectory(Path.Combine(dir, "Revisions"));   // ignored
            Assert.EndsWith("Rev 8", MassiveSlicer.ViewModels.MainWindowViewModel.NextRevisionDir(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void RejectsRecordsWithoutId()
    {
        Assert.Null(ErpClient.ParseHit(Parse("""{ "title": "no id" }""")));
        Assert.Null(ErpClient.ParseElement(Parse("""{ "name": "no id" }""")));
    }

    // -- Pricing (GET /pricing, POST /quote, slice costing) ----------------------

    [Fact]
    public void ParsesPricingConfigNestedShape()
    {
        var cfg = ErpClient.ParsePricing(Parse("""
        {
          "version": "a1b2c3d4e5",
          "machineRates": { "effectiveRatePerHour": 85.0, "effectiveRateWithFinishingPerHour": 120.0 },
          "materials": [
            { "id": "mat-1", "name": "ASA Black", "type": "ASA", "costPerKg": 9.5, "costPerLb": 4.31, "density": 1.07 },
            { "id": "mat-2", "name": "CF-ABS", "costPerKg": 12.0, "density": 1.11 }
          ],
          "markup": { "overheadRate": 0.10, "profitRate": 0.05 },
          "quantityDiscounts": [ { "minQuantity": 5, "rate": 0.08 }, { "minQuantity": 10, "rate": 0.15 } ]
        }
        """));
        Assert.NotNull(cfg);
        Assert.Equal("a1b2c3d4e5", cfg!.Version);
        Assert.Equal(85.0, cfg.RatePerHour);
        Assert.Equal(120.0, cfg.RateWithFinishingPerHour);
        Assert.Equal(0.10, cfg.OverheadRate);
        Assert.Equal(0.05, cfg.ProfitRate);
        Assert.Equal(2, cfg.Materials.Count);
        Assert.Equal(1.07, cfg.Materials[0].DensityGmCc);
        Assert.Equal(2, cfg.QuantityDiscounts.Count);
        Assert.Equal(5, cfg.QuantityDiscounts[0].MinQuantity);
        Assert.Equal(0.08, cfg.QuantityDiscounts[0].Rate);
    }

    [Fact]
    public void ParsesPricingConfigFlatShapeAndPercentRates()
    {
        // Flat rates, percent-style markup (10 = 10%), discounts under different names.
        var cfg = ErpClient.ParsePricing(Parse("""
        {
          "pricingVersion": "v77",
          "ratePerHour": 90,
          "overhead": 10,
          "profit": 5,
          "materials": [],
          "quantityDiscounts": [ { "minQty": 5, "percent": 8 } ]
        }
        """));
        Assert.NotNull(cfg);
        Assert.Equal("v77", cfg!.Version);
        Assert.Equal(90.0, cfg.RatePerHour);
        Assert.Equal(0.10, cfg.OverheadRate!.Value, 3);
        Assert.Equal(0.05, cfg.ProfitRate!.Value, 3);
        Assert.Equal(0.08, cfg.QuantityDiscounts[0].Rate, 3);
    }

    [Fact]
    public void MatchesCatalogMaterialByNameOrType()
    {
        var cfg = ErpClient.ParsePricing(Parse("""
        {
          "version": "x",
          "ratePerHour": 85,
          "materials": [
            { "id": "1", "name": "ASA", "type": "ASA", "costPerKg": 9.5 },
            { "id": "2", "name": "Polycarbonate CF", "type": "PC", "costPerKg": 14.0 }
          ]
        }
        """))!;
        Assert.Equal("1", cfg.MatchMaterial("ASA - Black")!.Id);   // preset name contains catalog name
        Assert.Equal("2", cfg.MatchMaterial("PC")!.Id);            // type match
        Assert.Null(cfg.MatchMaterial("PLA"));
    }

    [Fact]
    public void ParsesLivePricingShape()
    {
        // Verbatim structure from lab.massivemake.com GET /pricing (2026-07-09).
        var cfg = ErpClient.ParsePricing(Parse("""
        {
          "version": "c5f049d824a14232",
          "updatedAt": "2026-07-08T21:06:31.121Z",
          "machineRate": {
            "totalPrinterCostPerHour": 48.02, "overheadPerHour": 22.42,
            "laborOverheadPerHour": 67.25,
            "effectiveRatePerHour": 70.43764682012315,
            "effectiveRateWithFinishingPerHour": 115.2679223756787
          },
          "markup": { "overheadRate": 0, "profitRate": 0.2, "markupPercent": 20 },
          "quantityDiscounts": [
            { "minQuantity": 5, "discountPercent": 8 },
            { "minQuantity": 10, "discountPercent": 15 }
          ],
          "materials": [
            { "id": 2622, "key": "asa", "name": "ASA", "costPerKg": 5, "costPerLb": 2.27, "density": 1.07 },
            { "id": 2626, "key": "polycarbonate", "name": "Polycarbonate", "costPerKg": 55, "costPerLb": 24.95, "density": 1.2 }
          ]
        }
        """));
        Assert.NotNull(cfg);
        Assert.Equal("c5f049d824a14232", cfg!.Version);
        Assert.Equal(70.44, cfg.RatePerHour!.Value, 2);
        Assert.Equal(115.27, cfg.RateWithFinishingPerHour!.Value, 2);
        Assert.Equal(0.0, cfg.OverheadRate);
        Assert.Equal(0.2, cfg.ProfitRate);
        Assert.Equal(0.08, cfg.QuantityDiscounts[0].Rate, 3);
        Assert.Equal(0.15, cfg.QuantityDiscounts[1].Rate, 3);
        Assert.Equal("2622", cfg.MatchMaterial("ASA - Black")!.Id);
    }

    [Fact]
    public void ParsesLiveQuoteShape()
    {
        // Verbatim structure from lab.massivemake.com POST /quote (2026-07-09).
        var c = ErpClient.ParseCosting(Parse("""
        {
          "pricingVersion": "c5f049d824a14232",
          "inputs": { "printTimeHours": 7, "materialKg": 60.2, "quantity": 1, "finishing": false },
          "material": { "id": 2622, "name": "ASA", "costPerKg": 5 },
          "perUnit": { "machineCost": 493.06, "materialCost": 301, "totalCost": 794.06 },
          "quantityDiscount": { "rate": 0, "amount": 0 },
          "subtotalCost": 794.06,
          "markup": { "overheadAmount": 0, "profitAmount": 158.81, "totalAmount": 158.81 },
          "clientPrice": 952.87
        }
        """));
        Assert.Equal(493.06, c.MachineCost);
        Assert.Equal(301.0, c.MaterialCost);
        Assert.Equal(0.0, c.QuantityDiscount);
        Assert.Equal(158.81, c.Markup);
        Assert.Equal(794.06, c.SubtotalCost);
        Assert.Equal(952.87, c.ClientPrice);
        Assert.Equal("c5f049d824a14232", c.PricingVersion);
    }

    [Fact]
    public void ParsesCostingBreakdown()
    {
        var c = ErpClient.ParseCosting(Parse("""
        {
          "machineCost": 425.50, "materialCost": 88.20, "quantityDiscount": 41.10,
          "markup": 70.89, "subtotalCost": 472.60, "clientPrice": 543.49,
          "pricingVersion": "a1b2c3"
        }
        """));
        Assert.Equal(425.50, c.MachineCost);
        Assert.Equal(88.20, c.MaterialCost);
        Assert.Equal(41.10, c.QuantityDiscount);
        Assert.Equal(472.60, c.SubtotalCost);
        Assert.Equal(543.49, c.ClientPrice);
        Assert.Equal("a1b2c3", c.PricingVersion);
    }

    // -- Shared print / material presets (GET /presets-bundle) -------------------

    [Fact]
    public void ParsesPresetsBundleWrappedAndBarePayload()
    {
        var bundle = ErpClient.ParsePresetsBundle(Parse("""
        {
          "version": "2026-08-16T18:00:00.000Z",
          "printPresets": [
            {
              "id": "pp-1",
              "updatedAt": "2026-08-16T18:00:00Z",
              "payload": { "Name": "Master Defaults", "Folder": "Uncategorized", "BeadWidth": 6, "LayerHeight": 3 }
            }
          ],
          "materialPresets": [
            { "id": "mp-1", "payload": { "Name": "ASA GF - Black", "MaterialType": "ASA", "FlowRate": 0.4115 } }
          ]
        }
        """));
        Assert.Equal("2026-08-16T18:00:00.000Z", bundle.Version);
        Assert.Single(bundle.PrintPresets);
        Assert.Equal("pp-1", bundle.PrintPresets[0].Id);
        Assert.Contains("Master Defaults", bundle.PrintPresets[0].PayloadJson);
        Assert.Single(bundle.MaterialPresets);
        Assert.Equal("mp-1", bundle.MaterialPresets[0].Id);
        Assert.Contains("ASA GF - Black", bundle.MaterialPresets[0].PayloadJson);
    }

    [Fact]
    public void ParsesBarePresetPayloadUsingNameAsFallbackId()
    {
        var entry = ErpClient.ParsePresetEntry(Parse("""
        { "Name": "HHN Nasty Wall", "BeadWidth": 6.5, "LayerHeight": 3 }
        """));
        Assert.NotNull(entry);
        Assert.Equal("name:HHN Nasty Wall", entry!.Id);
        Assert.Contains("HHN Nasty Wall", entry.PayloadJson);
    }

    [Fact]
    public void ParsePresetEntryRejectsEmptyObject()
        => Assert.Null(ErpClient.ParsePresetEntry(Parse("""{ }""")));

    [Fact]
    public void ParsesLoginTokenFromSeveralShapes()
    {
        var a = ErpClient.ParseLogin(Parse("""{ "token": "msl_abc", "email": "thom@massivemake.com", "name": "Thom" }"""));
        Assert.NotNull(a);
        Assert.Equal("msl_abc", a!.Token);
        Assert.Equal("thom@massivemake.com", a.Email);
        Assert.Equal("Thom", a.DisplayName);

        var b = ErpClient.ParseLogin(Parse("""{ "accessToken": "tok2", "user": { "email": "a@b.c", "displayName": "A" } }"""));
        Assert.Equal("tok2", b!.Token);
        Assert.Equal("a@b.c", b.Email);
        Assert.Equal("A", b.DisplayName);

        var c = ErpClient.ParseLogin(Parse("""{ "data": { "api_token": "tok3" } }"""));
        Assert.Equal("tok3", c!.Token);

        Assert.Null(ErpClient.ParseLogin(Parse("""{ "ok": true }""")));
    }
}
