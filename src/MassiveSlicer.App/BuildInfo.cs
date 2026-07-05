namespace MassiveSlicer.App;

public static class BuildInfo
{
    // Incremented manually or via CI. Baked at compile time.
    public const int BuildNumber = 30;
    public static readonly string Label = $"build {BuildNumber}  ·  {BuildTimestamp}";
    private const string BuildTimestamp = "2026-06-30";
}
