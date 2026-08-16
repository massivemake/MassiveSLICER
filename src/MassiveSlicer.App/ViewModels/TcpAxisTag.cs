namespace MassiveSlicer.ViewModels;

/// <summary>Screen-space label on a TCP / flange / sensor triad (name or x/y/z).</summary>
public sealed record TcpAxisTag(
    float ScreenX,
    float ScreenY,
    string Label,
    bool IsTitle,
    string ColorHex)
{
    public double FontSize => IsTitle ? 12 : 10;
}
