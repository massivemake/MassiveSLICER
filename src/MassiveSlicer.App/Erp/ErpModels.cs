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
/// app's formatted display strings (e.g. "7h 13m", "132.4 kg").</summary>
public sealed record ErpSliceStats(
    string? PrintTime,
    string? Weight,
    string? Material,
    double? LayerHeightMm,
    double? BeadWidthMm);

/// <summary>ERP's acknowledgement of a registered slice (it assigns the rev number).</summary>
public sealed record ErpSliceReceipt(int Rev, string? Url);

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
