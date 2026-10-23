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
        "The last active manager of the agency cannot be locked: nobody could administer it any more.");

    public static readonly Error LastSupport = Error.Conflict(
        "identity.last_support",
        "The last active support account cannot be locked: nobody could administer the platform any more.");

    public static readonly Error AgencyRequired = Error.Validation(
        "identity.agency_required",
        "Open an agency first: its users are managed from its back office.");

    public static readonly Error AgencyNotFound = Error.NotFound("identity.agency_not_found", "The agency does not exist.");

    public static readonly Error PlatformScopeRequired = Error.Validation(
        "identity.platform_scope_required",
        "This operation is run from the platform pages, outside any agency.");

    public static readonly Error NotSupportAccount = Error.Validation(
        "identity.not_support_account",
        "Only a support account enters and leaves the agencies.");

    public static readonly Error PermissionNotForAgency = Error.Validation(
        "identity.permission_not_for_agency",
        "A platform permission cannot be given to a member of an agency.");

    public static readonly Error PermissionUnknown = Error.Validation("identity.permission_unknown", "Unknown permission.");

    public static readonly Error CannotChangeOwnPermissions = Error.Validation(
        "identity.cannot_change_own_permissions",
        "Users cannot change their own permissions.");

    public static Error OperationFailed(string details) => Error.Failure("identity.operation_failed", details);
}
