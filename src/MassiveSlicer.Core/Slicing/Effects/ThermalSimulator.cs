using System.Numerics;
using MassiveSlicer.Core.Models;

namespace MassiveSlicer.Core.Slicing.Effects;

/// <summary>Result of the analytical thermomechanical screen (all temps °C, times s).</summary>
public sealed class ThermalSimResult
{
    /// <summary>Lumped-capacitance cooling time constant of the deposited wall.</summary>
    public float TimeConstantS { get; init; }

    /// <summary>Fastest safe layer time — quicker and the layer below is still above the sag temperature.</summary>
    public float MinLayerTimeS { get; init; }

    /// <summary>Slowest safe layer time — longer and the interface cools below the bonding temperature.</summary>
    public float MaxLayerTimeS { get; init; }

    /// <summary>Layer time the speed recommendation targets (inside the safe window, biased fast).</summary>
    public float TargetLayerTimeS { get; init; }

    /// <summary>Predicted interlayer temperature at the target layer time.</summary>
    public float PredictedInterfaceTempC { get; init; }

    public float RecommendedMinMmS { get; init; }
    public float RecommendedMaxMmS { get; init; }

    public float DepositTempC { get; init; }
    public float BondTempC    { get; init; }
    public float SagTempC     { get; init; }
    public float AmbientTempC { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Fast analytical thermomechanical screen for LFAM in the spirit of FE thermal
/// simulation tools: a lumped-capacitance (Newton cooling) model of the deposited
/// wall predicts the interlayer temperature each layer lands on, derives the safe
/// layer-time window between the sag limit (previous layer still too soft) and the
/// bonding limit (interface below glass transition), and turns that window into
/// Adaptive Speed low/high values so every layer takes the same target time.
/// This is an analytical estimate, not validated finite-element analysis.
/// </summary>
public static class ThermalSimulator
{
    /// <summary>Specific heat of filled amorphous thermoplastic (J/kg·K).</summary>
    private const float SpecificHeatJKgK = 1800f;

    /// <summary>Combined convection + linearised radiation film coefficient (W/m²·K).</summary>
    private const float FilmCoeffWM2K = 12f;

    /// <summary>Default bond margin above Tg when the material preset does not override it.</summary>
    public const float DefaultBondMarginC = 10f;

    /// <summary>Default sag margin above Tg when the material preset does not override it.</summary>
    public const float DefaultSagMarginC = 45f;

    /// <summary>Safe robot speed clamp for the recommendation (mm/s).</summary>
    private const float MinSpeedMmS = 1f, MaxSpeedMmS = 250f;

    /// <summary>Approximate glass-transition (bonding-relevant) temperature by material family.</summary>
    public static float GlassTransitionC(string? materialType)
    {
        string m = (materialType ?? "").ToUpperInvariant();
        if (m.Contains("PEI") || m.Contains("ULTEM")) return 217f;
        if (m.Contains("PESU") || m.Contains("PES"))  return 225f;
        if (m.Contains("PSU"))                        return 187f;
        if (m.Contains("PC"))                         return 145f;
        if (m.Contains("ABS"))                        return 105f;
        if (m.Contains("ASA"))                        return 100f;
        if (m.Contains("PETG") || m.Contains("PET"))  return 78f;
        if (m.Contains("PLA"))                        return 60f;
        if (m.Contains("PA") || m.Contains("NYLON"))  return 70f;
        if (m.Contains("PP"))                         return 100f;  // semicrystalline: solidification proxy
        return 100f;
    }

    /// <summary>Cooling time constant τ = ρ·c·(w/2)/h for a free wall of bead width w.</summary>
    public static float TimeConstantS(SliceSettings settings)
    {
        float rhoKgM3 = MathF.Max(settings.ThermalDensityGmCc, 0.2f) * 1000f;
        float halfWallM = MathF.Max(settings.BeadWidth, 0.5f) * 0.5f / 1000f;
        return rhoKgM3 * SpecificHeatJKgK * halfWallM / FilmCoeffWM2K;
    }

    public static ThermalSimResult Simulate(Toolpath toolpath, SliceSettings settings)
    {
        float tau  = TimeConstantS(settings);
        float tDep = settings.ThermalDepositTempC;
        float tAmb = settings.ThermalAmbientTempC;
        float tg   = settings.ThermalGlassTransitionC;
        float bondMargin = settings.ThermalBondMarginC > 0f
            ? settings.ThermalBondMarginC : DefaultBondMarginC;
        float sagMargin = settings.ThermalSagMarginC > 0f
            ? settings.ThermalSagMarginC : DefaultSagMarginC;
        float tBond = tg + bondMargin;
        float tSag  = tg + sagMargin;

        var warnings = new List<string>();
        if (tDep <= tSag + 5f)
        {
            warnings.Add($"Deposit temperature {tDep:0}°C is at or below the sag limit {tSag:0}°C — model degenerate, no speeds set.");
            return new ThermalSimResult
            {
                TimeConstantS = tau, DepositTempC = tDep, BondTempC = tBond,
                SagTempC = tSag, AmbientTempC = tAmb, Warnings = warnings,
            };
        }

        // Newton cooling: T(t) = Tamb + (Tdep − Tamb)·e^(−t/τ)  ⇒  t(T) = τ·ln((Tdep−Tamb)/(T−Tamb))
        float tMin = tau * MathF.Log((tDep - tAmb) / (tSag  - tAmb));
        float tMax = tau * MathF.Log((tDep - tAmb) / (tBond - tAmb));

        // Target inside the window, biased toward the fast (production) end.
        float tTarget = tMin * MathF.Pow(tMax / tMin, 0.35f);
        float tInterface = tAmb + (tDep - tAmb) * MathF.Exp(-tTarget / tau);

        // Per-layer extrusion length. Adaptive Speed (cut-length basis) interpolates
        // linearly between low/high — with low = Lmin/t* and high = Lmax/t* every
        // layer takes exactly t*, i.e. a constant interlayer temperature.
        float lMin = float.MaxValue, lMax = 0f;
        foreach (var layer in toolpath.Layers)
        {
            float len = 0f;
            foreach (var move in layer.Moves)
                if (move.Kind == MoveKind.Extrude)
                    len += Vector3.Distance(move.From, move.To);
            if (len <= 0.1f) continue;
            lMin = MathF.Min(lMin, len);
            lMax = MathF.Max(lMax, len);
        }
        if (lMax <= 0f)
        {
            warnings.Add("Toolpath has no extrusion moves — nothing to simulate.");
            return new ThermalSimResult
            {
                TimeConstantS = tau, MinLayerTimeS = tMin, MaxLayerTimeS = tMax,
                TargetLayerTimeS = tTarget, PredictedInterfaceTempC = tInterface,
                DepositTempC = tDep, BondTempC = tBond, SagTempC = tSag,
                AmbientTempC = tAmb, Warnings = warnings,
            };
        }

        float vMin = lMin / tTarget;
        float vMax = lMax / tTarget;

        if (vMin < MinSpeedMmS)
        {
            vMin = MinSpeedMmS;
            if (lMin / MinSpeedMmS < tMin)
                warnings.Add($"Shortest layer ({lMin:0} mm) finishes in {lMin / MinSpeedMmS:0} s even at {MinSpeedMmS:0} mm/s — under the {tMin:0} s sag limit. Add dwell time or active cooling.");
        }
        if (vMax > MaxSpeedMmS)
        {
            vMax = MaxSpeedMmS;
            if (lMax / MaxSpeedMmS > tMax)
                warnings.Add($"Longest layer ({lMax / 1000f:0.0} m) takes {lMax / MaxSpeedMmS:0} s even at {MaxSpeedMmS:0} mm/s — over the {tMax:0} s bonding limit. Raise deposit temperature or heat the environment.");
        }
        if (vMin > vMax) vMin = vMax;

        return new ThermalSimResult
        {
            TimeConstantS = tau, MinLayerTimeS = tMin, MaxLayerTimeS = tMax,
            TargetLayerTimeS = tTarget, PredictedInterfaceTempC = tInterface,
            RecommendedMinMmS = vMin, RecommendedMaxMmS = vMax,
            DepositTempC = tDep, BondTempC = tBond, SagTempC = tSag,
            AmbientTempC = tAmb, Warnings = warnings,
        };
    }

    /// <summary>
    /// Stamps <see cref="ToolpathLayer.ThermalTempC"/> on every layer: the predicted
    /// temperature of the surface a layer is deposited onto, from the previous layer's
    /// actual print time (per-move speeds included). Cheap — run after post-processing.
    /// </summary>
    public static void StampLayerTemps(Toolpath toolpath, SliceSettings settings)
    {
        float tau  = TimeConstantS(settings);
        float tDep = settings.ThermalDepositTempC;
        float tAmb = settings.ThermalAmbientTempC;
        var rates = new ToolpathMotionRates(
            settings.PrintSpeedMps * 1000f,
            settings.TravelSpeed * 1000f,
            settings.WipeSpeed * 1000f);

        float prevTime = float.NaN;
        foreach (var layer in toolpath.Layers)
        {
            double layerTime = 0.0;
            foreach (var move in layer.Moves)
            {
                double dist = Vector3.Distance(move.From, move.To);
                layerTime += ToolpathStatistics.MoveTimeSeconds(move, rates, dist);
            }
            // First layer lands on the bed — use its own period as the proxy.
            float period = float.IsNaN(prevTime) ? (float)layerTime : prevTime;
            layer.ThermalTempC = tAmb + (tDep - tAmb) * MathF.Exp(-period / tau);
            prevTime = (float)layerTime;
        }
    }
}
