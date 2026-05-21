using MassiveSlicer.ViewModels.Base;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// Displays read-only geometry statistics and bounding-box dimensions for
/// the currently selected object in the viewport. Populated by the selection
/// manager when the selection changes.
/// </summary>
public sealed class PropsViewModel : ViewModelBase
{
    private string _fileName = string.Empty;

    /// <summary>Name of the loaded file (without extension).</summary>
    public string FileName
    {
        get => _fileName;
        set => SetField(ref _fileName, value);
    }

    private string _fileFormat = string.Empty;

    /// <summary>File format string (e.g. "STL", "GLB").</summary>
    public string FileFormat
    {
        get => _fileFormat;
        set => SetField(ref _fileFormat, value);
    }

    private int _vertexCount;

    /// <summary>Number of vertices in the loaded mesh.</summary>
    public int VertexCount
    {
        get => _vertexCount;
        set => SetField(ref _vertexCount, value);
    }

    private int _faceCount;

    /// <summary>Number of triangular faces in the loaded mesh.</summary>
    public int FaceCount
    {
        get => _faceCount;
        set => SetField(ref _faceCount, value);
    }

    private double _boundX, _boundY, _boundZ;

    /// <summary>Bounding box X dimension in mm.</summary>
    public double BoundX { get => _boundX; set => SetField(ref _boundX, value); }
    /// <summary>Bounding box Y dimension in mm.</summary>
    public double BoundY { get => _boundY; set => SetField(ref _boundY, value); }
    /// <summary>Bounding box Z dimension in mm.</summary>
    public double BoundZ { get => _boundZ; set => SetField(ref _boundZ, value); }
}
