using OpenTK.Mathematics;

namespace MassiveSlicer.Viewport.Rendering;

/// <summary>
/// World-space tags for the TCP / flange / sensor RGB triads.
/// X = red, Y = green, Z = blue. Axis length matches <see cref="AxisRenderer"/>.
/// </summary>
public static class TcpAxisLabelLayout
{
    public const float AxisLengthMm = 200f;
    public const float LetterPastTipMm = 28f;
    public const float TitleLiftMm = 36f;

    public const string ColorX = "#E63838";
    public const string ColorY = "#38CC4D";
    public const string ColorZ = "#4073F2";
    public const string ColorTitle = "#F2F2F2";

    public readonly record struct WorldLabel(
        Vector3 World, string Text, bool IsTitle, string ColorHex);

    public static IReadOnlyList<WorldLabel> Build(
        Matrix4? tcp,
        Matrix4? flange,
        Matrix4? sensor,
        string tcpName)
    {
        var list = new List<WorldLabel>(16);
        if (tcp is { } t)
            AddFrame(list, t, string.IsNullOrWhiteSpace(tcpName) ? "TCP" : tcpName.Trim());
        if (flange is { } f)
            AddFrame(list, f, "FLANGE");
        if (sensor is { } s)
            AddFrame(list, s, "SENSOR");
        return list;
    }

    static void AddFrame(List<WorldLabel> list, Matrix4 m, string title)
    {
        var o = m.Row3.Xyz;
        var x = SafeAxis(m.Row0.Xyz);
        var y = SafeAxis(m.Row1.Xyz);
        var z = SafeAxis(m.Row2.Xyz);
        float tip = AxisLengthMm + LetterPastTipMm;
        list.Add(new WorldLabel(o + z * TitleLiftMm, title, true, ColorTitle));
        list.Add(new WorldLabel(o + x * tip, "x", false, ColorX));
        list.Add(new WorldLabel(o + y * tip, "y", false, ColorY));
        list.Add(new WorldLabel(o + z * tip, "z", false, ColorZ));
    }

    static Vector3 SafeAxis(Vector3 v)
        => v.LengthSquared < 1e-10f ? Vector3.UnitX : Vector3.Normalize(v);
}
