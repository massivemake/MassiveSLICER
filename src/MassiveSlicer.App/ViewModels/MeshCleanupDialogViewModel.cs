using MassiveSlicer.ViewModels.Base;
using MassiveSlicer.Viewport.Scene;

namespace MassiveSlicer.ViewModels;

/// <summary>ViewModel for the Mesh Cleanup dialog (Modo-style repair toggles).</summary>
public sealed class MeshCleanupDialogViewModel : ViewModelBase
{
    private bool _removeFloatingVertices = true;
    private bool _removeOnePointPolygons = true;
    private bool _removeTwoPointPolygons = true;
    private bool _fixDuplicatePointsInPolygons = true;
    private bool _removeColinearVertices = true;
    private bool _fixFaceNormalVectors = true;
    private bool _mergeVertices = true;
    private bool _unifyPolygons = true;
    private bool _forceUnify;
    private bool _fixGaps;

    public bool RemoveFloatingVertices
    {
        get => _removeFloatingVertices;
        set => SetField(ref _removeFloatingVertices, value);
    }

    public bool RemoveOnePointPolygons
    {
        get => _removeOnePointPolygons;
        set => SetField(ref _removeOnePointPolygons, value);
    }

    public bool RemoveTwoPointPolygons
    {
        get => _removeTwoPointPolygons;
        set => SetField(ref _removeTwoPointPolygons, value);
    }

    public bool FixDuplicatePointsInPolygons
    {
        get => _fixDuplicatePointsInPolygons;
        set => SetField(ref _fixDuplicatePointsInPolygons, value);
    }

    public bool RemoveColinearVertices
    {
        get => _removeColinearVertices;
        set => SetField(ref _removeColinearVertices, value);
    }

    public bool FixFaceNormalVectors
    {
        get => _fixFaceNormalVectors;
        set => SetField(ref _fixFaceNormalVectors, value);
    }

    public bool MergeVertices
    {
        get => _mergeVertices;
        set => SetField(ref _mergeVertices, value);
    }

    public bool UnifyPolygons
    {
        get => _unifyPolygons;
        set => SetField(ref _unifyPolygons, value);
    }

    public bool ForceUnify
    {
        get => _forceUnify;
        set => SetField(ref _forceUnify, value);
    }

    public bool FixGaps
    {
        get => _fixGaps;
        set => SetField(ref _fixGaps, value);
    }

    public MeshCleanupOptions ToOptions() => new()
    {
        RemoveFloatingVertices       = RemoveFloatingVertices,
        RemoveOnePointPolygons       = RemoveOnePointPolygons,
        RemoveTwoPointPolygons       = RemoveTwoPointPolygons,
        FixDuplicatePointsInPolygons = FixDuplicatePointsInPolygons,
        RemoveColinearVertices       = RemoveColinearVertices,
        FixFaceNormalVectors         = FixFaceNormalVectors,
        MergeVertices                = MergeVertices,
        UnifyPolygons                = UnifyPolygons,
        ForceUnify                   = ForceUnify,
        FixGaps                      = FixGaps,
    };
}