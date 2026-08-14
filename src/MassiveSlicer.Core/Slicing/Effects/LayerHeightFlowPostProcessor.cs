using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing.Effects;

/// <summary>
/// Makes extrusion flow follow the ACTUAL thickness of each adaptive layer.
///
/// Adaptive layer height varies the Z spacing between slice planes, but extruder RPM is
/// derived from the single nominal <see cref="SliceSettings.LayerHeight"/>. Without this pass
/// a 1.75 mm layer is handed the material for a 3 mm one. Measured on a real part (Dragon
/// column, 44 of 314 layers below nominal): up to 1.71x over-extrusion, concentrated in the
/// curved base — it showed on the machine as bulging.
///
/// The correction rides on <see cref="ToolpathMove.HeightScale"/>, the same per-move factor
/// the multi-planar wedge slicer already uses, so every downstream consumer picks it up with
/// no further change: <see cref="IO.ToolpathRpm"/>, the KRL exporter, the RPM view and
/// gradient, and the 99 % export gate.
///
/// Scales are &lt;= 1 in practice (adaptive heights never exceed nominal), so this can only
/// reduce commanded flow — it cannot push a previously-valid job over the export limit.
/// </summary>
public static class LayerHeightFlowPostProcessor
{
    /// <summary>
    /// Bounds on the correction. A skipped or empty slice plane can leave a Z gap several
    /// layers tall; scaling flow by that verbatim would be worse than leaving it alone.
    /// </summary>
    public const float MinScale = 0.05f;

    /// <inheritdoc cref="MinScale"/>
    public const float MaxScale = 2.0f;

    /// <summary>
    /// Stamps <see cref="ToolpathMove.HeightScale"/> from each layer's real thickness.
    ///
    /// No-op unless <see cref="SliceSettings.VariesLayerThickness"/> — with genuinely uniform
    /// layers every scale is 1 and the nominal height is already correct, so gating here keeps
    /// existing prints bit-identical.
    ///
    /// It gates on that ONE property rather than on <see cref="SliceSettings.AdaptiveLayerHeight"/>
    /// alone, because support-driven thinning also moves slice planes. Keyed to the adaptive flag
    /// this pass simply did not run for it, and every thinned layer went out at full nominal flow.
    ///
    /// Mutates <paramref name="toolpath"/> in place, and is idempotent: the scale is assigned
    /// rather than accumulated, so calling it twice cannot compound.
    /// </summary>
    public static void Apply(Toolpath toolpath, SliceSettings settings)
    {
        if (!settings.VariesLayerThickness) return;

        float nominal = settings.LayerHeight;
        if (nominal <= 1e-4f) return;

        foreach (var layer in toolpath.Layers)
        {
            // Height unset (0) means we do not know the thickness — leave flow alone rather
            // than guess. Every planar-sliced layer carries it (PlanarSlicer sets it).
            if (layer.Height <= 0f) continue;

            float scale = ScaleFor(layer.Height, nominal);
            for (int mi = 0; mi < layer.Moves.Count; mi++)
            {
                var move = layer.Moves[mi];
                if (MathF.Abs(move.HeightScale - scale) > 1e-6f)
                    layer.Moves[mi] = move with { HeightScale = scale };
            }
        }
    }

    /// <summary>Flow scale for a layer of <paramref name="heightMm"/> against <paramref name="nominalMm"/>.</summary>
    public static float ScaleFor(float heightMm, float nominalMm)
        => nominalMm <= 1e-4f || heightMm <= 0f
            ? 1f
            : Math.Clamp(heightMm / nominalMm, MinScale, MaxScale);
}
