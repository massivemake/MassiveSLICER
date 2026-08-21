using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Tests;

public sealed class WorkspaceSaveLogTest
{
    [Theory]
    [InlineData("/Volumes/MassiveFILES/Projects/26-173 - x/06-Production Documents/file.mass",
        "Projects/26-173 - x/06-Production Documents/file.mass")]
    [InlineData(@"\\192.168.0.191\MassiveFILES\Projects\26-173\file.mass",
        "Projects/26-173/file.mass")]
    [InlineData(@"Z:\Projects\26-173\file.mass",
        "Projects/26-173/file.mass")]
    [InlineData(@"Z:\Research\LFAM\MassiveSLICER\scratch.mass",
        "Research/LFAM/MassiveSLICER/scratch.mass")]
    public void Share_relative_covers_mac_unc_and_shop_Z(string input, string expected)
        => Assert.Equal(expected, UnasPaths.ToShareRelative(input));

    [Fact]
    public void Local_desktop_path_is_not_a_share_path()
    {
        Assert.Null(UnasPaths.ToShareRelative("/Users/thom/Desktop/local.mass"));
        Assert.Null(UnasPaths.ToShareRelative("/Volumes/OnlyShare"));
    }

    [Fact]
    public void UnasProjectsRoot_maps_a_drive_letter_folder()
    {
        Assert.Equal(
            "Projects/job/a.mass",
            UnasPaths.ToShareRelative(@"D:\jobs\job\a.mass", @"D:\jobs"));
    }

    [Fact]
    public void Append_writes_jsonl_to_the_share_log()
    {
        string root = Path.Combine(Path.GetTempPath(), "ms-unas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string mass = Path.Combine(root, "part.mass");
        File.WriteAllText(mass, "{}");
        try
        {
            var rec = WorkspaceSaveLog.Build(
                mass, root, "LFAM 3",
                new ErpAttachment { Type = "project", Id = "9", Number = "26-173", Title = "Test" });
            Assert.Equal("part.mass", rec.File);
            Assert.Equal("LFAM 3", rec.Cell);
            Assert.Equal("26-173", rec.ProjectNumber);
            Assert.True(rec.Bytes > 0);

            var written = WorkspaceSaveLog.Append(rec, root);
            string shareLog = WorkspaceSaveLog.ShareLogPath(root)!;
            Assert.Contains(shareLog, written);
            Assert.True(File.Exists(shareLog));
            string line = File.ReadAllText(shareLog);
            Assert.Contains("\"file\":\"part.mass\"", line);
            Assert.Contains("26-173", line);
            Assert.Contains("LFAM 3", line);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* tmp */ }
        }
    }
}
