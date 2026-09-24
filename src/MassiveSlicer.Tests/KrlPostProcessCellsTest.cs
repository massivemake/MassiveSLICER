using System.Text.Json;
using MassiveSlicer.Core.IO;
using MassiveSlicer.Core.Models;
using MassiveSlicer.ViewModels;

namespace MassiveSlicer.Tests;

/// <summary>
/// Per-robot KRL recipes: one document holding a shared recipe plus a complete recipe per
/// cell, where publishing one robot never touches another, and a document published by a
/// build from before per-robot recipes (which drops every robot's entry) is detected and
/// recoverable.
/// </summary>
public sealed class KrlPostProcessCellsTest : IDisposable
{
    private readonly string _backupPath =
        Path.Combine(Path.GetTempPath(), $"krl-sections-{Guid.NewGuid():N}.json");
    private readonly string _originalBackupPath = KrlPostProcessSectionBackup.FilePath;

    public KrlPostProcessCellsTest() => KrlPostProcessSectionBackup.FilePath = _backupPath;

    public void Dispose()
    {
        KrlPostProcessSectionBackup.FilePath = _originalBackupPath;
        if (File.Exists(_backupPath)) File.Delete(_backupPath);
    }

    private static KrlPostProcessSettings Recipe(string header, double cvel) => new()
    {
        RulesSaved = true,
        HeaderText = header,
        FooterText = "END",
        ApoCvel    = cvel,
    };

    private static KrlPostProcessSettings SharedOnly() => Recipe("SHARED", 100);

    [Fact]
    public void Robot_without_its_own_recipe_uses_the_shared_one()
    {
        var r = KrlPostProcessCells.Resolve(SharedOnly(), "LFAM 2");
        Assert.Equal("SHARED", r.HeaderText);
        Assert.Null(r.Cells);
    }

    [Fact]
    public void Robot_with_its_own_recipe_gets_it_matched_case_insensitively()
    {
        var doc = KrlPostProcessCells.WithCell(SharedOnly(), "LFAM 1", Recipe("ONE", 50));
        Assert.Equal("ONE", KrlPostProcessCells.Resolve(doc, "lfam 1").HeaderText);
        Assert.Equal(50, KrlPostProcessCells.Resolve(doc, "LFAM 1").ApoCvel);
        Assert.Equal("SHARED", KrlPostProcessCells.Resolve(doc, "LFAM 3").HeaderText);
    }

    [Fact]
    public void Writing_one_robot_leaves_the_others_and_the_shared_recipe_alone()
    {
        var doc = KrlPostProcessCells.WithCell(SharedOnly(), "LFAM 1", Recipe("ONE", 50));
        doc = KrlPostProcessCells.WithCell(doc, "LFAM 2", Recipe("TWO", 0));
        doc = KrlPostProcessCells.WithCell(doc, "lfam 1", Recipe("ONE-v2", 60));

        Assert.Equal("SHARED", doc.HeaderText);
        Assert.Equal(100, doc.ApoCvel);
        Assert.Equal(["lfam 1", "LFAM 2"], KrlPostProcessCells.Names(doc));
        Assert.Equal("ONE-v2", KrlPostProcessCells.Find(doc, "LFAM 1")!.HeaderText);
        Assert.Equal("TWO", KrlPostProcessCells.Find(doc, "LFAM 2")!.HeaderText);
    }

    [Fact]
    public void WithCell_does_not_mutate_its_inputs()
    {
        var root = SharedOnly();
        var recipe = Recipe("ONE", 50);
        KrlPostProcessCells.WithCell(root, "LFAM 1", recipe);
        Assert.Null(root.Cells);
        Assert.Equal("ONE", recipe.HeaderText);
    }

    [Fact]
    public void Per_robot_document_round_trips_through_the_file_and_Lab_format()
    {
        var doc = KrlPostProcessCells.WithCell(SharedOnly(), "LFAM 1", Recipe("ONE", 50));
        var json = KrlPostProcessDocument.SerializePayload(doc);
        Assert.True(KrlPostProcessDocument.TryParse(json, out var back, out var err), err);
        Assert.Equal("SHARED", back.HeaderText);
        Assert.Equal("ONE", KrlPostProcessCells.Find(back, "LFAM 1")!.HeaderText);
    }

    /// <summary>What an older build's model looks like: everything but the robot map.</summary>
    private sealed class OldBuildSettings
    {
        public string HeaderText { get; set; } = "";
        public double? ApoCvel { get; set; }
    }

    [Fact]
    public void Older_builds_still_read_the_shared_recipe_from_a_per_robot_document()
    {
        var doc = KrlPostProcessCells.WithCell(SharedOnly(), "LFAM 1", Recipe("ONE", 50));
        var json = KrlPostProcessDocument.SerializePayload(doc);
        var old = JsonSerializer.Deserialize<OldBuildSettings>(json, KrlPostProcessDocument.JsonOptions)!;
        Assert.Equal("SHARED", old.HeaderText);
        Assert.Equal(100, old.ApoCvel);
    }

    [Fact]
    public void Old_build_publish_wipe_is_detected_parked_and_restorable()
    {
        var lab = KrlPostProcessCells.WithCell(SharedOnly(), "LFAM 1", Recipe("ONE", 50));
        lab = KrlPostProcessCells.WithCell(lab, "LFAM 2", Recipe("TWO", 0));

        // This PC pulls the per-robot document: both robots are now known to be on the Lab.
        Assert.Empty(KrlPostProcessSectionBackup.Reconcile(lab, SharedOnly()));
        var local = lab;   // the pull wrote it to the local file

        // An old build published only a shared recipe: no robot entries at all.
        var wiped = Recipe("OLD-BUILD", 100);
        Assert.Equal(["LFAM 1", "LFAM 2"], KrlPostProcessSectionBackup.Reconcile(wiped, local));

        // The next pull sees the wiped document again (the local file is now wiped too):
        // still flagged, and the parked copies survive.
        Assert.Equal(["LFAM 1", "LFAM 2"], KrlPostProcessSectionBackup.Reconcile(wiped, wiped));

        // Someone on a new build republished LFAM 1 in the meantime. That entry wins.
        var labNow = KrlPostProcessCells.WithCell(wiped, "LFAM 1", Recipe("ONE-new", 70));
        Assert.Equal(["LFAM 2"], KrlPostProcessSectionBackup.Reconcile(labNow, wiped));

        var restored = KrlPostProcessCells.RestoreMissing(
            labNow, KrlPostProcessSectionBackup.Load(), out var names);
        Assert.Equal(["LFAM 2"], names);
        Assert.Equal("ONE-new", KrlPostProcessCells.Find(restored, "LFAM 1")!.HeaderText);
        Assert.Equal("TWO", KrlPostProcessCells.Find(restored, "LFAM 2")!.HeaderText);
        Assert.Equal("OLD-BUILD", restored.HeaderText);

        // Once the restore is published, nothing stays parked.
        Assert.Empty(KrlPostProcessSectionBackup.Published(restored));
        Assert.Empty(KrlPostProcessSectionBackup.Load());
    }

    [Fact]
    public void Robot_saved_locally_but_never_published_is_not_reported_missing()
    {
        // The Lab has never had robot entries; this PC saved LFAM 2 locally without publishing.
        Assert.Empty(KrlPostProcessSectionBackup.Reconcile(SharedOnly(), SharedOnly()));
        var local = KrlPostProcessCells.WithCell(SharedOnly(), "LFAM 2", Recipe("TWO", 0));
        Assert.Empty(KrlPostProcessSectionBackup.Reconcile(SharedOnly(), local));
        Assert.Empty(KrlPostProcessSectionBackup.Load());
    }

    [Fact]
    public void Normal_first_rollout_raises_no_warning()
    {
        // Lab and this PC both still single-recipe: nothing is missing.
        Assert.Empty(KrlPostProcessSectionBackup.Reconcile(SharedOnly(), SharedOnly()));
        // Another PC published LFAM 1; this PC never had robot entries: still nothing missing.
        var lab = KrlPostProcessCells.WithCell(SharedOnly(), "LFAM 1", Recipe("ONE", 50));
        Assert.Empty(KrlPostProcessSectionBackup.Reconcile(lab, SharedOnly()));
    }

    [Fact]
    public void Dialog_title_names_the_robot_and_switching_robot_loads_its_rules()
    {
        var add = new AdditiveSettingsViewModel();
        var post = add.KrlPostProcess;
        var doc = KrlPostProcessCells.WithCell(SharedOnly(), "LFAM 1", Recipe("ONE", 50));
        doc = KrlPostProcessCells.WithCell(doc, "LFAM 2", Recipe("TWO", 0));

        Assert.True(post.SetCell("LFAM 1", doc));
        Assert.Equal("KRL Post-Processing — LFAM 1", post.DialogTitle);
        Assert.Equal("ONE", post.HeaderText);
        Assert.Equal(50, add.ApoCvel);

        Assert.True(post.SetCell("LFAM 2", doc));
        Assert.Equal("KRL Post-Processing — LFAM 2", post.DialogTitle);
        Assert.Equal("TWO", post.HeaderText);
        Assert.Equal(0, add.ApoCvel);

        Assert.True(post.SetCell("LFAM 3", doc));
        Assert.Equal("SHARED", post.HeaderText);
        Assert.Equal(100, add.ApoCvel);
    }

    [Fact]
    public void Reloading_the_same_cell_keeps_unsaved_edits()
    {
        var post = new AdditiveSettingsViewModel().KrlPostProcess;
        var doc = KrlPostProcessCells.WithCell(SharedOnly(), "LFAM 1", Recipe("ONE", 50));
        post.SetCell("LFAM 1", doc);
        post.HeaderText = "EDITED";
        Assert.False(post.SetCell("lfam 1", doc));
        Assert.Equal("EDITED", post.HeaderText);
    }

    [Fact]
    public void ToSettings_is_a_single_robot_recipe()
    {
        var post = new AdditiveSettingsViewModel().KrlPostProcess;
        post.SetCell("LFAM 1", KrlPostProcessCells.WithCell(SharedOnly(), "LFAM 1", Recipe("ONE", 50)));
        Assert.Null(post.ToSettings().Cells);
    }
}
