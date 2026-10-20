using CarRental.Domain.Common;

namespace CarRental.Application.Common.Concurrency;

public static class AvailabilityLockErrors
{
    public static readonly Error Timeout = Error.Conflict(
        "availability.lock_timeout",
        "Another operation is updating the availability of this category; try again.");

    public static readonly Error Deadlock = Error.Conflict(
        "availability.lock_deadlock",
        "The availability lock request was chosen as a deadlock victim.");

    public static readonly Error Failed = Error.Failure("availability.lock_failed", "The availability lock could not be acquired.");
}
