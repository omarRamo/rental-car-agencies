namespace CarRental.Domain.Common;

public static class PeriodErrors
{
    public static readonly Error EndMustBeAfterStart = Error.Validation(
        "period.end_must_be_after_start",
        "The end of a period must be strictly after its start.");
}
