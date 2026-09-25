using System.Globalization;
using System.Text;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.ViewModels;

/// <summary>
/// The <c>seam-guide</c> console command: read, set, clear, or snap the seam position guide to a
/// corner of the selected part, without opening the seam editor.
/// </summary>
/// <remarks>
/// The guides live in <see cref="AdditiveSettingsViewModel.SeamGuides"/> — a saved preference, not
/// part of the model — so a guide placed for one part keeps steering the seam of the next one until
/// it is moved or cleared. This command is the way to see that and fix it headlessly. Writing goes
/// through <see cref="AdditiveSettingsViewModel.SetSeamGuides"/>, the same path the seam editor's
/// Save uses, so the re-slice and the on-screen markers follow exactly as they do from the panel.
/// </remarks>
public sealed partial class ViewportViewModel
{
    /// <summary>
    /// World bounds of the selected part — its mesh, even when its toolpath is the selection. Null
    /// when nothing is selected or the selection has no geometry. Wired by the view.
    /// </summary>
    internal Func<(System.Numerics.Vector3 Min, System.Numerics.Vector3 Max)?>? GetSelectedPartWorldBounds { get; set; }

    private const string SeamGuideUsage =
        "[seam-guide] usage: seam-guide [list | clear | set <x> <y> [z] | add <x> <y> [z] | corner <-x|+x> <-y|+y>]";

    /// <summary>Backs the <c>seam-guide</c> command.</summary>
    public string SeamGuideCommand(string args)
    {
        var p = (args ?? string.Empty).Split((char[])[' ', ','],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string verb = p.Length == 0 ? "list" : p[0].ToLowerInvariant();

        if (AdditiveSettings is not { } add)
            return "[seam-guide] settings not ready.";
        if (verb == "list")
            return DescribeSeamGuides(add.SeamGuides);

        // The editor holds its own draft and writes it back on Save — changing the guides underneath
        // it would be silently overwritten, or would overwrite what the user is placing.
        if (IsSeamEditorActive || IsToolpathSeamEditActive)
            return "[seam-guide] the seam editor is open — Save or Cancel it first.";

        List<SeamGuidePoint> next;
        switch (verb)
        {
            case "clear":
                next = [];
                break;

            case "set":
            case "add":
                if (!TryParseGuide(p, out var point))
                    return SeamGuideUsage;
                next = verb == "add" ? [.. add.SeamGuides, point] : [point];
                break;

            case "corner":
                if (p.Length < 3 || !TryParseSide(p[1], 'x', out bool highX) || !TryParseSide(p[2], 'y', out bool highY))
                    return SeamGuideUsage;
                if (GetSelectedPartWorldBounds?.Invoke() is not { } b)
                    return "[seam-guide] select a part first — corner is measured from its bounds.";
                next = [CornerGuide(b.Min, b.Max, highX, highY)];
                break;

            default:
                return SeamGuideUsage;
        }

        add.SetSeamGuides(next);
        OnSeamGuidesChanged?.Invoke();
        return DescribeSeamGuides(add.SeamGuides);
    }

    /// <summary>
    /// A guide on the chosen corner of the part's bounds, at the bottom of the part. The seam lands
    /// where each layer's loop comes closest to the guide in XY, so on a part whose walls run along
    /// the bounds this is that corner of every layer.
    /// </summary>
    internal static SeamGuidePoint CornerGuide(System.Numerics.Vector3 min, System.Numerics.Vector3 max, bool highX, bool highY)
        => new(highX ? max.X : min.X, highY ? max.Y : min.Y, min.Z);

    private static bool TryParseSide(string token, char axis, out bool high)
    {
        high = false;
        string t = token.ToLowerInvariant();
        if (t == $"-{axis}") return true;
        if (t == $"+{axis}" || t == $"{axis}") { high = true; return true; }
        return false;
    }

    private static bool TryParseGuide(string[] p, out SeamGuidePoint point)
    {
        point = new SeamGuidePoint(0f, 0f, 0f);
        if (p.Length < 3) return false;
        if (!float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)) return false;
        if (!float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)) return false;
        float z = 0f;
        if (p.Length > 3 && !float.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;
        point = new SeamGuidePoint(x, y, z);
        return true;
    }

    private static string DescribeSeamGuides(IReadOnlyCollection<SeamGuidePoint> guides)
    {
        if (guides.Count == 0)
            return "[seam-guide] no guides — the seam goes where the seam mode puts it.";
        var sb = new StringBuilder($"[seam-guide] {guides.Count} guide(s):");
        int i = 0;
        foreach (var g in guides)
            sb.Append(CultureInfo.InvariantCulture, $"\n[seam-guide]   #{i++}  X {g.X:0.0}  Y {g.Y:0.0}  Z {g.Z:0.0}");
        return sb.ToString();
    }
}
