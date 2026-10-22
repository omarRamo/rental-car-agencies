namespace CarRental.Application.Common.Persistence;

/// <summary>
/// Names of the global query filters of the persistence layer. Lift only the one a query needs:
/// <c>IgnoreQueryFilters([QueryFilters.SoftDelete])</c>. A bare <c>IgnoreQueryFilters()</c> also lifts
/// <see cref="Agency"/> and shows the rows of every agency (refused by an architecture test).
/// </summary>
public static class QueryFilters
{
    /// <summary>Hides soft-deleted rows (<c>ISoftDeletable</c>).</summary>
    public const string SoftDelete = "SoftDelete";

    /// <summary>
    /// Hides the rows of the other agencies (ADR-0067). Lifted only by a platform query run across agencies on purpose
    /// (resolution of a public site address, sign-in, support pages).
    /// </summary>
    public const string Agency = "Agency";
}
