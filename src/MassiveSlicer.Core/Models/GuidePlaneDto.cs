using System.Text.Json;
using System.Text.Json.Serialization;

namespace MassiveSlicer.Core.Models;

/// <summary>One Multi-Planar guide as stored in a .mass file. Named fields so a
/// zero tilt is still present — a positional pair used to drop X.</summary>
public sealed class GuidePlaneDto
{
    public double HeightPct { get; set; }
    public double AngleX { get; set; }
    public double AngleY { get; set; }
}

/// <summary>
/// Writes <c>{HeightPct, AngleX, AngleY}</c> and still reads the old
/// <c>[height, angleY]</c> / <c>[height, angleY, angleX]</c> arrays.
/// </summary>
public sealed class GuidePlaneListConverter : JsonConverter<List<GuidePlaneDto>>
{
    public override List<GuidePlaneDto> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return [];
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("MultiPlanarPlanes must be an array.");

        var list = new List<GuidePlaneDto>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.StartArray)
                list.Add(ReadArray(ref reader));
            else if (reader.TokenType == JsonTokenType.StartObject)
                list.Add(ReadObject(ref reader));
        }
        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<GuidePlaneDto> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var row in value)
        {
            writer.WriteStartObject();
            writer.WriteNumber("HeightPct", row.HeightPct);
            writer.WriteNumber("AngleX", row.AngleX);
            writer.WriteNumber("AngleY", row.AngleY);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static GuidePlaneDto ReadArray(ref Utf8JsonReader reader)
    {
        var nums = new List<double>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            if (reader.TokenType == JsonTokenType.Number)
                nums.Add(reader.GetDouble());
        return new GuidePlaneDto
        {
            HeightPct = nums.Count > 0 ? nums[0] : 0,
            AngleY    = nums.Count > 1 ? nums[1] : 0,
            AngleX    = nums.Count > 2 ? nums[2] : 0,
        };
    }

    private static GuidePlaneDto ReadObject(ref Utf8JsonReader reader)
    {
        double h = 0, x = 0, y = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            string name = reader.GetString() ?? "";
            reader.Read();
            if (reader.TokenType != JsonTokenType.Number) continue;
            double v = reader.GetDouble();
            if (name.Equals("HeightPct", StringComparison.OrdinalIgnoreCase)) h = v;
            else if (name.Equals("AngleX", StringComparison.OrdinalIgnoreCase)) x = v;
            else if (name.Equals("AngleY", StringComparison.OrdinalIgnoreCase)
                     || name.Equals("AngleDeg", StringComparison.OrdinalIgnoreCase)) y = v;
        }
        return new GuidePlaneDto { HeightPct = h, AngleX = x, AngleY = y };
    }
}
