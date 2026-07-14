using System.Text.Json;
using MassiveSlicer.Core.Models;
using Xunit;

namespace MassiveSlicer.Tests;

public class ErpAttachmentWorkspaceTest
{
    static readonly JsonSerializerOptions Load = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    [Fact]
    public void AttachmentRoundTrips()
    {
        var doc = new WorkspaceDocument
        {
            Erp = new ErpAttachment
            {
                Type = "project", Id = "42", Number = "25-114",
                Title = "Stove Surround", ElementId = "7", ElementName = "Element 3",
            },
        };

        string json = JsonSerializer.Serialize(doc);
        var back = JsonSerializer.Deserialize<WorkspaceDocument>(json, Load);

        Assert.NotNull(back?.Erp);
        Assert.Equal("25-114", back!.Erp!.Number);
        Assert.Equal("7", back.Erp.ElementId);
        Assert.Equal("Element 3", back.Erp.ElementName);
    }

    [Fact]
    public void OldWorkspacesLoadWithNullAttachment()
    {
        var back = JsonSerializer.Deserialize<WorkspaceDocument>(
            """{ "Version": 2, "RightPanelTab": "Additive", "Models": [] }""", Load);

        Assert.NotNull(back);
        Assert.Null(back!.Erp);
    }

    [Fact]
    public void FileHasModelsGuardsAgainstEmptySceneOverwrite()
    {
        string dir = Directory.CreateTempSubdirectory("msl-guard").FullName;
        try
        {
            string withModels = Path.Combine(dir, "real.mass");
            File.WriteAllText(withModels, """{ "Version": 2, "Models": [ { "Name": "Part" } ] }""");
            Assert.True(MassiveSlicer.App.WorkspaceService.FileHasModels(withModels));

            string empty = Path.Combine(dir, "empty.mass");
            File.WriteAllText(empty, """{ "Version": 2, "Models": [] }""");
            Assert.False(MassiveSlicer.App.WorkspaceService.FileHasModels(empty));

            string corrupt = Path.Combine(dir, "corrupt.mass");
            File.WriteAllText(corrupt, "not json");
            Assert.False(MassiveSlicer.App.WorkspaceService.FileHasModels(corrupt));

            Assert.False(MassiveSlicer.App.WorkspaceService.FileHasModels(Path.Combine(dir, "missing.mass")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
