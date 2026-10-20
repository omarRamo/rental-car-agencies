using CarRental.Domain.Common;

namespace CarRental.Application.Identity;

public static class IdentityErrors
{
    public static readonly Error Forbidden = Error.Forbidden("identity.forbidden", "The current user lacks the required permission.");

    public static readonly Error EmailInvalid = Error.Validation("identity.email_invalid", "The e-mail address is invalid.");

    public static readonly Error EmailTaken = Error.Conflict("identity.email_taken", "A user with this e-mail address already exists.");

    public static readonly Error DisplayNameInvalid = Error.Validation(
        "identity.display_name_invalid",
        "The display name is required (100 characters max).");

    public static readonly Error RoleUnknown = Error.Validation("identity.role_unknown", "Unknown role.");

    public static readonly Error UserNotFound = Error.NotFound("identity.user_not_found", "The user does not exist.");

    public static readonly Error CannotLockSelf = Error.Validation("identity.cannot_lock_self", "Users cannot lock their own account.");

    public static readonly Error LastManager = Error.Conflict(
        "identity.last_manager",
        "The last active manager cannot be locked: nobody could administer the platform any more.");

    public static Error OperationFailed(string details) => Error.Failure("identity.operation_failed", details);
}
