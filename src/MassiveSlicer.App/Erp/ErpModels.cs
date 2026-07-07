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

public enum ErpErrorKind { Network, Unauthorized, Timeout, BadResponse }

public sealed record ErpError(ErpErrorKind Kind, string Message);

/// <summary>Result-or-error channel — the client never throws to callers.</summary>
public readonly record struct ErpResult<T>(T? Value, ErpError? Error)
{
    public bool Ok => Error is null;
    public static ErpResult<T> Success(T value) => new(value, null);
    public static ErpResult<T> Fail(ErpErrorKind kind, string message) => new(default, new ErpError(kind, message));
}
