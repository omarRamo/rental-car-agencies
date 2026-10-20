namespace CarRental.Domain.Reporting;

/// <summary>
/// Steps of the public booking funnel (docs/conception/phase-2-vitrine-paiement.md §1 "Mesure"), in order. Values are
/// stored: never renumber them.
/// </summary>
public enum FunnelStep
{
    /// <summary>A valid search was submitted (home form, modified search, refinement or deep link).</summary>
    Search = 1,

    /// <summary>The results page showed at least one available category with its price.</summary>
    Results = 2,

    /// <summary>Step 1 of the funnel (options) was displayed after a category was chosen.</summary>
    Options = 3,

    /// <summary>Step 2 (drivers and documents) was displayed.</summary>
    Documents = 4,

    /// <summary>Step 3 (summary and payment) was displayed.</summary>
    Payment = 5,

    /// <summary>The request was submitted and its confirmation page displayed.</summary>
    Confirmation = 6,
}
