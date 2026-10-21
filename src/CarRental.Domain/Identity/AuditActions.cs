namespace CarRental.Domain.Identity;

/// <summary>Stable action codes of the audit log (dotted, lower case). Never rename a published code.</summary>
public static class AuditActions
{
    // Entity changes, written automatically by the persistence layer.
    public const string EntityCreated = "entity.created";
    public const string EntityUpdated = "entity.updated";
    public const string EntityDeleted = "entity.deleted";
    public const string EntitySoftDeleted = "entity.soft_deleted";

    // Authentication and account security.
    public const string LoginSucceeded = "identity.login_succeeded";
    public const string LoginFailed = "identity.login_failed";
    public const string LoginRequiresTwoFactor = "identity.login_requires_two_factor";
    public const string TwoFactorSucceeded = "identity.two_factor_succeeded";
    public const string TwoFactorFailed = "identity.two_factor_failed";
    public const string RecoveryCodeUsed = "identity.recovery_code_used";
    public const string RecoveryCodeFailed = "identity.recovery_code_failed";
    public const string LockedOut = "identity.locked_out";
    public const string Logout = "identity.logout";
    public const string PasswordChanged = "identity.password_changed";
    public const string PasswordResetRequested = "identity.password_reset_requested";
    public const string PasswordReset = "identity.password_reset";
    public const string TwoFactorEnabled = "identity.two_factor_enabled";
    public const string AuthenticatorReset = "identity.authenticator_reset";
    public const string RecoveryCodesGenerated = "identity.recovery_codes_generated";

    // User administration.
    public const string UserCreated = "identity.user_created";
    public const string UserLocked = "identity.user_locked";
    public const string UserUnlocked = "identity.user_unlocked";
    public const string UserInvitationSent = "identity.user_invitation_sent";
    public const string PermissionGranted = "identity.permission_granted";
    public const string PermissionRevoked = "identity.permission_revoked";

    // Operations (background messages of the outbox).
    public const string OutboxMessageRetried = "outbox.message_retried";
}
