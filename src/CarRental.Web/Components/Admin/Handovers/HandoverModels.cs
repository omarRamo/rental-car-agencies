using CarRental.Domain.Bookings.Handover;

namespace CarRental.Web.Components.Admin.Handovers;

/// <summary>A step of a hand-over wizard: its key (in the address, <c>?step=photos</c>), title, whether it is done and open.</summary>
public sealed record HandoverStep(string Key, string Title, bool Done, bool Enabled = true);

/// <summary>A tap on a view of the damage schematic, in percent of the view.</summary>
public sealed record SchematicTap(DamageView View, decimal X, decimal Y);

/// <summary>What the damage dialog returns.</summary>
internal sealed record DamageDialogResult(DamageInput Input, bool Remove = false);

/// <summary>What the deferral dialog returns.</summary>
internal sealed record DeferralInput(bool DeferBalance, bool WaiveDeposit, string Reason);

/// <summary>The answer of <c>prepare</c> of js/admin/handover-photos.js.</summary>
internal sealed record PreparedPhoto(string Id, long Size, bool HasThumbnail);
