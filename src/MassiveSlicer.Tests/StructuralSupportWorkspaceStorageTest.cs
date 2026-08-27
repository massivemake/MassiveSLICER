using System.Text.Json;
using MassiveSlicer.Core.Models;
using Xunit;

namespace MassiveSlicer.Tests;

/// <summary>
/// Structural Support pockets belong to the workspace (.mass), not to AppPreferences.
/// <para>
/// They used to be saved in app preferences, which made them app-wide: a support placed on
/// one part came back on an unrelated model after a relaunch, still silently modifying its
/// toolpath, and it survived its own deletion across a rebuild. Worse, the cards in the
/// workspace hold an integer index into the spec list — indexing across two separate stores,
/// so one workspace's cards could address another workspace's specs.
/// </para>
/// </summary>
public sealed class StructuralSupportWorkspaceStorageTest
{
    static StructuralSupportSpec Spec(string name = "Support 1") => new()
    {
        Name = name,
        Shape = SupportShapeKind.Rectangle,
        AnchorX = 2409.4f, AnchorY = -393.7f, AnchorLayer = 21,
        LayersUp = 9999, LayersDown = 40,
        CenterX = 2406.5f, CenterY = -226.6f,
        WidthMm = 92f, DepthMm = 42f, RotationDeg = -35f,
        Enabled = false,
    };

    [Fact]
    public void A_support_survives_a_round_trip_through_the_workspace_dto()
    {
        var back = WorkspaceStructuralSupport.From(Spec()).ToSpec("fallback");

        // Every field, because a dropped one here is a pocket that silently moves or
        // re-enables itself when a job is reopened.
        Assert.Equal("Support 1", back.Name);
        Assert.Equal(SupportShapeKind.Rectangle, back.Shape);
        Assert.Equal(2409.4f, back.AnchorX, 3);
        Assert.Equal(-393.7f, back.AnchorY, 3);
        Assert.Equal(21, back.AnchorLayer);
        Assert.Equal(9999, back.LayersUp);
        Assert.Equal(40, back.LayersDown);
        Assert.Equal(2406.5f, back.CenterX, 3);
        Assert.Equal(-226.6f, back.CenterY, 3);
        Assert.Equal(92f, back.WidthMm, 3);
        Assert.Equal(42f, back.DepthMm, 3);
        Assert.Equal(-35f, back.RotationDeg, 3);
        Assert.False(back.Enabled);
    }

    [Fact]
    public void A_circle_pocket_keeps_its_shape_through_the_round_trip()
    {
        var spec = Spec() with { Shape = SupportShapeKind.Circle };
        Assert.Equal(SupportShapeKind.Circle,
            WorkspaceStructuralSupport.From(spec).ToSpec("fallback").Shape);
    }

    [Fact]
    public void A_blank_name_falls_back_rather_than_showing_an_empty_label()
    {
        var spec = Spec(name: "");
        Assert.Equal("Support 7",
            WorkspaceStructuralSupport.From(spec).ToSpec("Support 7").Name);
    }

    [Fact]
    public void The_names_ride_with_their_own_support_not_in_a_parallel_list()
    {
        // The preferences version stored a float[12] plus an index-parallel name list, which
        // had to be kept in lockstep by hand — filter one spec and every later name shifted.
        var specs = new[] { Spec("Alpha"), Spec("Beta"), Spec("Gamma") };
        var dtos = specs.Select(WorkspaceStructuralSupport.From).ToList();
        dtos.RemoveAt(1);                                   // drop the middle one

        Assert.Equal("Alpha", dtos[0].ToSpec("x").Name);
        Assert.Equal("Gamma", dtos[1].ToSpec("x").Name);     // NOT "Beta"
    }

    /// <summary>
    /// The options WorkspaceLoader actually saves with. WhenWritingDefault drops any property
    /// equal to its TYPE's default, so a test using plain default options proves nothing about
    /// the real file — it was exactly that gap that hid Enabled=false being lost.
    /// </summary>
    static readonly JsonSerializerOptions WorkspaceSaveOptions = new()
    {
        DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
    };

    static WorkspaceUiSession RoundTrip(WorkspaceUiSession s) =>
        JsonSerializer.Deserialize<WorkspaceUiSession>(
            JsonSerializer.Serialize(s, WorkspaceSaveOptions), WorkspaceSaveOptions)!;

    [Fact]
    public void Supports_serialise_inside_the_workspace_ui_session()
    {
        var back = RoundTrip(new WorkspaceUiSession
        {
            StructuralSupports = [WorkspaceStructuralSupport.From(Spec("Support 4"))],
        });

        Assert.Single(back.StructuralSupports);
        Assert.Equal("Support 4", back.StructuralSupports[0].Name);
        Assert.Equal(-35f, back.StructuralSupports[0].RotationDeg, 3);
    }

    [Fact]
    public void A_disabled_support_does_not_come_back_enabled()
    {
        // Enabled defaults to true, and false IS the type default, so the real save options
        // would drop it and the load would resurrect it as enabled. A support the user
        // switched off, quietly modifying the toolpath again on reopen.
        var spec = Spec() with { Enabled = false };
        var back = RoundTrip(new WorkspaceUiSession
        {
            StructuralSupports = [WorkspaceStructuralSupport.From(spec)],
        });

        Assert.False(back.StructuralSupports[0].Enabled);
        Assert.False(back.StructuralSupports[0].ToSpec("x").Enabled);
    }

    [Fact]
    public void This_layer_only_does_not_come_back_as_all_the_way_up()
    {
        // Same trap: LayersUp initialises to 9999, and 0 is the type default. Dropping it
        // turns "just the anchor layer" into "every layer to the top".
        var spec = Spec() with { LayersUp = 0 };
        var back = RoundTrip(new WorkspaceUiSession
        {
            StructuralSupports = [WorkspaceStructuralSupport.From(spec)],
        });

        Assert.Equal(0, back.StructuralSupports[0].LayersUp);
        Assert.Equal(0, back.StructuralSupports[0].ToSpec("x").LayersUp);
    }

    [Fact]
    public void A_workspace_written_before_this_change_has_no_supports_and_still_loads()
    {
        // Older .mass files have a UiSession with no StructuralSupports property at all.
        // It must deserialise to an empty list, not null — the restore path iterates it.
        const string oldJson = """
        { "ViewMode": "Preview", "IsPaintEditOpen": false }
        """;
        var back = JsonSerializer.Deserialize<WorkspaceUiSession>(oldJson);

        Assert.NotNull(back);
        Assert.NotNull(back!.StructuralSupports);
        Assert.Empty(back.StructuralSupports);
    }

    [Fact]
    public void A_card_index_from_an_older_file_points_past_the_end_of_an_empty_list()
    {
        // Pins the condition the restore path guards: a card saved when specs lived in
        // preferences carries an index that now addresses nothing. Left unchecked it would
        // resolve to whichever pocket occupies that slot in an unrelated job.
        var card = new WorkspacePaintModification { StructuralIndex = 2 };
        var session = new WorkspaceUiSession { PaintModifications = [card] };

        Assert.True(card.StructuralIndex >= session.StructuralSupports.Count);
    }
}
