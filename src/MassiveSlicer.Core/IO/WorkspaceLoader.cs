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

    private static readonly JsonSerializerOptions LoadOptions = new()
    {
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
        Converters                  = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions SaveOptions = new()
    {
        WriteIndented               = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingDefault,
        Converters                  = { new JsonStringEnumConverter() },
    };

    public static bool Exists()
        => File.Exists(DefaultPath) || File.Exists(LegacyPath);

    /// <summary>
    /// Loads a workspace. <paramref name="progress"/> (0..1) reports real read progress —
    /// the deserializer consumes the file incrementally, so bytes-read / file-size tracks
    /// the actual parse position.
    /// </summary>
    public static WorkspaceDocument? Load(string? path = null, Action<float>? progress = null)
    {
        path ??= ResolveDefaultLoadPath();
        path = PathNormalization.Normalize(path);
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1 << 16);
            using var ps = new ProgressReadStream(fs, progress);
            return JsonSerializer.Deserialize<WorkspaceDocument>(ps, LoadOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Read-only stream wrapper reporting consumed-bytes / total-length.</summary>
    private sealed class ProgressReadStream(Stream inner, Action<float>? progress) : Stream
    {
        private long _reported = -1;

        private void Report()
        {
            if (progress is null || inner.Length <= 0) return;
            // throttle: report at ~1% granularity
            long bucket = inner.Position * 100 / inner.Length;
            if (bucket == _reported) return;
            _reported = bucket;
            progress(Math.Clamp(inner.Position / (float)inner.Length, 0f, 1f));
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            Report();
            return n;
        }

        public override int Read(Span<byte> buffer)
        {
            int n = inner.Read(buffer);
            Report();
            return n;
        }

        public override bool CanRead  => true;
        public override bool CanSeek  => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length   => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public static void Save(WorkspaceDocument doc, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(fs, doc, SaveOptions);
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