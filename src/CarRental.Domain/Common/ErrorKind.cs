namespace CarRental.Domain.Common;

/// <summary>Broad category of a business error, used to map failures to transport-level responses (HTTP status, UI severity).</summary>
public enum ErrorKind
{
    /// <summary>The input breaks a rule (HTTP 400/422).</summary>
    Validation = 0,

    /// <summary>The target does not exist or is not visible to the caller (HTTP 404).</summary>
    NotFound = 1,

    /// <summary>The operation conflicts with the current state, e.g. no more capacity (HTTP 409).</summary>
    Conflict = 2,

    /// <summary>The caller is not allowed to perform the operation (HTTP 403).</summary>
    Forbidden = 3,

    /// <summary>The operation could not complete for a business reason not covered above, e.g. a lock timeout.</summary>
    Failure = 4,
}
