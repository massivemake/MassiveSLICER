using System.Linq;

namespace MassiveSlicer.App.Erp;

/// <summary>One combined Project/Lead quick-search result from the ERP.</summary>
public sealed record ErpSearchHit(
    string Type,                            // "project" | "lead"
    string Id,
    string Number,                          // "25-114"
    string Title,
    string? Client,
    IReadOnlyList<ErpElement> Elements);

/// <summary>One element of an ERP project (print jobs attach at element level).</summary>
public sealed record ErpElement(
    string Id,
    string Name,
    string? ElementNumber,
    int RevisionCount);

/// <summary>One file reference registered with a slice — bytes live on the UNAS share,
/// the ERP stores only this share-relative path and resolves it via its UNAS API.</summary>
public sealed record ErpSliceFile(
    string Kind,                            // "preview" | "workspace" | "krl"
    string Path,                            // share-relative, e.g. "Projects/26-173 …/file.src"
    long? Bytes);

/// <summary>Slice metadata registered against an element. All fields optional —
/// send what the app knows; the ERP renders what it receives. Time/weight are the
/// app's formatted display strings (e.g. "7h 13m", "132.4 kg"); the numeric twins
/// let the ERP compute an authoritative cost snapshot for the revision.</summary>
public sealed record ErpSliceStats(
    string? PrintTime,
    string? Weight,
    string? Material,
    double? LayerHeightMm,
    double? BeadWidthMm,
    double? PrintTimeSec = null,
    double? WeightKg = null);

/// <summary>ERP's acknowledgement of a registered slice (it assigns the rev number).
/// Costing is the ERP-computed cost snapshot for the revision (null when the slice
/// carried no resolvable time/weight).</summary>
public sealed record ErpSliceReceipt(int Rev, string? Url, ErpCosting? Costing = null);

// ── Pricing (ERP is the source of truth — never hard-code rates) ────────────

/// <summary>One material from the ERP pricing catalog (Settings → Materials).</summary>
public sealed record ErpPricingMaterial(
    string Id,
    string Name,
    string? Type,
    double? CostPerKg,
    double? CostPerLb,
    double? DensityGmCc);

/// <summary>Quantity discount tier: <c>Rate</c> off at <c>MinQuantity</c>+ units.</summary>
public sealed record ErpQuantityDiscount(int MinQuantity, double Rate);

/// <summary>The ERP's pricing configuration (GET /pricing). <c>Version</c> is a hash
/// that changes whenever any pricing number changes — cache the config and re-fetch
/// when a quote/costing echoes a different <c>pricingVersion</c>.</summary>
public sealed record ErpPricingConfig(
    string Version,
    double? RatePerHour,
    double? RateWithFinishingPerHour,
    double? OverheadRate,
    double? ProfitRate,
    IReadOnlyList<ErpPricingMaterial> Materials,
    IReadOnlyList<ErpQuantityDiscount> QuantityDiscounts)
{
    /// <summary>Catalog material whose name or type matches (case-insensitive contains).</summary>
    public ErpPricingMaterial? MatchMaterial(string? presetNameOrType)
    {
        if (string.IsNullOrWhiteSpace(presetNameOrType)) return null;
        var probe = presetNameOrType.Trim();
        return Materials.FirstOrDefault(m =>
                   probe.Contains(m.Name, StringComparison.OrdinalIgnoreCase)
                   || m.Name.Contains(probe, StringComparison.OrdinalIgnoreCase))
               ?? Materials.FirstOrDefault(m =>
                   m.Type is { Length: > 0 } t
                   && (probe.Contains(t, StringComparison.OrdinalIgnoreCase)
                       || t.Contains(probe, StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>Cost breakdown from POST /quote or a slice registration's costing block.
/// <c>SubtotalCost</c> is the raw internal cost — never client-facing;
/// <c>ClientPrice</c> is the number that goes on anything a customer sees.</summary>
public sealed record ErpCosting(
    double? MachineCost,
    double? MaterialCost,
    double? QuantityDiscount,
    double? Markup,
    double? SubtotalCost,
    double? ClientPrice,
    string? PricingVersion);

/// <summary>Inputs for an authoritative quote (POST /quote). Send at least a print
/// time or a resolvable weight or the ERP responds 400.</summary>
public sealed record ErpQuoteRequest(
    double? PrintTimeSec,
    double? WeightKg,
    string? Material = null,
    int Quantity = 1,
    bool Finishing = false,
    double? CustomMachineRatePerHour = null);

public enum ErpErrorKind { Network, Unauthorized, Timeout, BadResponse }

/// <summary>Message is the server's human-readable error when the body carried one
/// (surfaced to the user verbatim per ERP #961), else "HTTP nnn". Body keeps the raw
/// response for callers that need structured hints (e.g. converted-lead project id).</summary>
public sealed record ErpError(ErpErrorKind Kind, string Message, int? HttpStatus = null, string? Body = null);

/// <summary>Result-or-error channel — the client never throws to callers.</summary>
public readonly record struct ErpResult<T>(T? Value, ErpError? Error)
{
    public bool Ok => Error is null;
    public static ErpResult<T> Success(T value) => new(value, null);
    public static ErpResult<T> Fail(ErpErrorKind kind, string message) => new(default, new ErpError(kind, message));
    public static ErpResult<T> Fail(ErpError error) => new(default, error);
}

/// <summary>A project resolved by id (the elements endpoint's envelope) — used to
/// re-attach after a lead was converted to a project.</summary>
public sealed record ErpProjectInfo(
    string Id,
    string Number,
    string Title,
    IReadOnlyList<ErpElement> Elements);

// ── Shared print / material preset library (ERP source of truth) ─────────────

/// <summary>
/// One library entry from GET/POST/PUT print-presets or material-presets.
/// <see cref="PayloadJson"/> is the raw desktop record (PrintPresetRecord or MaterialPreset)
/// as JSON text so unknown future fields round-trip without a hard schema lock.
/// </summary>
public sealed record ErpPresetEntry(
    string Id,
    DateTime? UpdatedAt,
    string? UpdatedBy,
    string PayloadJson);

/// <summary>GET /presets-bundle — both libraries plus a version stamp for cheap re-sync.</summary>
public sealed record ErpPresetsBundle(
    string Version,
    IReadOnlyList<ErpPresetEntry> PrintPresets,
    IReadOnlyList<ErpPresetEntry> MaterialPresets);

/// <summary>
/// Result of POST /api/slicer/v1/login (email + password). The token is the same
/// bearer used on every other slicer route (Settings → Slicer Access today).
/// </summary>
public sealed record ErpLoginResult(string Token, string? Email, string? DisplayName, DateTime? ExpiresAt);
