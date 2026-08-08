using System.Text.Json;
using System.Text.Json.Serialization;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.IO;

/// <summary>
/// Writes every property of a workspace settings block, including values that happen to equal the
/// CLR default for their type.
///
/// The workspace writer sets <see cref="JsonIgnoreCondition.WhenWritingDefault"/> so the bulky
/// per-move toolpath records stay small. Applied to settings it silently discarded any operator
/// choice of <c>0</c> or <c>false</c>: 115 properties on <see cref="AppPreferences"/> initialise to
/// something other than their type default, so a dropped property came back on load as the
/// initializer's value rather than what was saved. Adaptive quality 0 reopened as 0.5, and every
/// toggle switched off reopened switched on.
///
/// Three properties had already been patched individually for this (<c>Visible</c> on both entry
/// types, <c>XBracingShowHelper</c>, and <c>ShowMultiPlanarPlanes</c> made nullable). Handling it
/// per type removes the whole class instead of one property at a time.
///
/// Nulls are still omitted, so a string or nullable left null keeps its initializer on load rather
/// than arriving as null and surprising the code that reads it.
/// </summary>
internal sealed class AlwaysWriteValuesConverter<T> : JsonConverter<T> where T : class, new()
{
    private static readonly JsonSerializerOptions Full = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        Converters                  = { new JsonStringEnumConverter() },
    };

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => JsonSerializer.Deserialize<T>(ref reader, Full) ?? new T();

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, Full);
}
