using MassiveSlicer.App;
using MassiveSlicer.Core.Models;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Pre-loaded scene nodes and config for an atomic cell swap.
/// Built on the UI thread; consumed and attached to the scene graph on the GL thread.
/// </summary>
internal record CellSwapPayload(
    CellConfig                              Config,
    string                                  CellPath,
    SceneNode?                              RobotBaseNode,
    SceneNode?                              BoosterNode,
    SceneNode?                              BedNode,
    SceneNode?                              ToolHolder,
    ToolCellConfig?                         FirstTool,
    IReadOnlyList<SceneNode>                EnvironmentNodes,
    SceneNode?                              RotaryBedPivot,
    CellEnvironmentBuilder.CellMultiToolSet? MultiTools,
    SceneNode?                              FlangeAttachment,
    int                                     Generation = 0);