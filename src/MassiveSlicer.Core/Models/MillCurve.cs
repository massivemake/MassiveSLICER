using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MassiveSlicer.Core.Models;

/// <summary>Which side of a curve the tool center moves to.</summary>
public enum CurveOffsetSide
{
    Outside,
    Inside,
    OnLine,
}

/// <summary>
/// A polyline the mill can follow. Points are slicer millimetres, Z up.
/// A closed chain does not need to repeat its first point.
/// </summary>
public sealed class MillCurve
{
    public string Name { get; set; } = "Curve";
    public bool Closed { get; set; }
    public bool Included { get; set; } = true;
    /// <summary>Swaps which side of an open chain is outside. Closed chains use the loop center either way.</summary>
    public bool Flipped { get; set; }
    [JsonConverter(typeof(MillCurvePointsJson))]
    public List<Vector3> Points { get; set; } = [];
}

/// <summary>Vector3 is a field struct, so the workspace writer stores each point as [x, y, z].</summary>
public sealed class MillCurvePointsJson : JsonConverter<List<Vector3>>
{
    public override List<Vector3> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var pts = new List<Vector3>();
        if (reader.TokenType == JsonTokenType.Null) return pts;
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Curve points must be an array.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException("Each curve point must be [x, y, z].");
            reader.Read();
            float x = reader.GetSingle();
            reader.Read();
            float y = reader.GetSingle();
            reader.Read();
            float z = reader.GetSingle();
            reader.Read();
            pts.Add(new Vector3(x, y, z));
        }
        return pts;
    }

    public override void Write(Utf8JsonWriter writer, List<Vector3> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var p in value)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(p.X);
            writer.WriteNumberValue(p.Y);
            writer.WriteNumberValue(p.Z);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }
}
