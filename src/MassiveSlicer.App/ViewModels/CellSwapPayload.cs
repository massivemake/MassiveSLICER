using MassiveSlicer.Core.Models;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Pre-loaded scene nodes and config for an atomic cell swap.
/// Built on the UI thread; consumed and attached to the scene graph on the GL thread.
/// </summary>
internal record CellSwapPayload(
    CellConfig      Config,
    SceneNode?      RobotBaseNode,
    SceneNode?      BoosterNode,
    SceneNode?      BedNode,
    SceneNode?      ToolHolder,
    ToolCellConfig? FirstTool);
