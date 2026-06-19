using System.Text.RegularExpressions;

namespace MassiveSlicer.Core.C3Bridge;

/// <summary>
/// Extracts named numeric fields from KUKA struct response strings, e.g.
///   {A1 -12.754, A2 47.546, A3 -130.201, A4 0.0, A5 -10.123, A6 0.0}
///   {X 1433.83, Y -1377.36, Z -870.0, A 0.013, B -0.001, C 179.998}
/// </summary>
public static class KrlVarParser
{
    /// <summary>
    /// Extracts the specified field names from a KUKA response string.
    /// Missing fields default to 0.0.
    /// </summary>
    public static Dictionary<string, double> Parse(string response, IEnumerable<string> labels)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in labels)
        {
            var escaped = Regex.Escape(label);
            var m = Regex.Match(
                response,
                $@"\b{escaped}\s*[= ]\s*([-+]?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)",
                RegexOptions.IgnoreCase);
            result[label] = m.Success ? double.Parse(m.Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture) : 0.0;
        }
        return result;
    }

    /// <summary>
    /// Returns joint angles [A1..A6, E1] in KRL degrees from a $AXIS_ACT response.
    /// Index 6 is E1 (external axis 1 — rotary bed). Defaults to 0 if absent.
    /// </summary>
    public static double[] ParseAxisAct(string response)
    {
        var v = Parse(response, ["A1", "A2", "A3", "A4", "A5", "A6", "E1"]);
        return [v["A1"], v["A2"], v["A3"], v["A4"], v["A5"], v["A6"], v["E1"]];
    }

    /// <summary>Returns joint angles [A1..A6] and the E1 external-axis value from a $AXIS_ACT response.</summary>
    public static (double[] Joints, double E1) ParseAxisActWithE1(string response)
    {
        var v = Parse(response, ["A1", "A2", "A3", "A4", "A5", "A6", "E1"]);
        return ([v["A1"], v["A2"], v["A3"], v["A4"], v["A5"], v["A6"]], v["E1"]);
    }

    /// <summary>Returns (X, Y, Z, A, B, C) in mm / degrees from a $POS_ACT response.</summary>
    public static (double X, double Y, double Z, double A, double B, double C) ParsePosAct(string response)
    {
        var v = Parse(response, ["X", "Y", "Z", "A", "B", "C"]);
        return (v["X"], v["Y"], v["Z"], v["A"], v["B"], v["C"]);
    }
}
