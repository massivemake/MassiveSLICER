using System.Text.Json;
using System.Text.Json.Serialization;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Persists and restores <see cref="WorkspaceDocument"/> workspace files.
/// Default path: <c>%AppData%\MassiveSlicer\workspace.mass</c>
/// </summary>
public static class WorkspaceLoader
{
    public static readonly string WorkspaceDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MassiveSlicer");

    public static readonly string DefaultPath = Path.Combine(WorkspaceDir, "workspace.mass");

    private static readonly string LegacyPath = Path.Combine(WorkspaceDir, "workspace.json");

    public static string MeshesDir => Path.Combine(WorkspaceDir, "workspace_meshes");

    /// <summary>Sidecar mesh folder beside a <c>.mass</c> workspace file.</summary>
    public static string MeshesDirFor(string workspacePath)
        => Path.Combine(Path.GetDirectoryName(workspacePath)!, "workspace_meshes");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        Converters                  = { new JsonStringEnumConverter() },
    };

    public static bool Exists()
        => File.Exists(DefaultPath) || File.Exists(LegacyPath);

    public static WorkspaceDocument? Load(string? path = null)
    {
        path ??= ResolveDefaultLoadPath();
        if (!File.Exists(path)) return null;
        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<WorkspaceDocument>(json, Options);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(WorkspaceDocument doc, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(doc, Options));
        }
        catch { /* non-fatal */ }
    }

    public static string ResolveMeshPath(string workspacePath, string relativeMeshPath)
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(workspacePath)!, relativeMeshPath));

    public static string ToRelativeMeshPath(string fileName)
        => Path.Combine("workspace_meshes", fileName).Replace('\\', '/');

    /// <summary>Returns the last-saved path if it exists, otherwise the default workspace path.</summary>
    public static string? GetRestorePath(string? lastSavedPath)
    {
        if (!string.IsNullOrEmpty(lastSavedPath) && File.Exists(lastSavedPath))
            return lastSavedPath;
        if (File.Exists(DefaultPath)) return DefaultPath;
        if (File.Exists(LegacyPath))  return LegacyPath;
        return null;
    }

    private static string ResolveDefaultLoadPath()
    {
        if (File.Exists(DefaultPath)) return DefaultPath;
        if (File.Exists(LegacyPath))  return LegacyPath;
        return DefaultPath;
    }
}