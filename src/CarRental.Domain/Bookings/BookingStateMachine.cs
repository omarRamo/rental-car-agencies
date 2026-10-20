using System.Collections.Frozen;

namespace CarRental.Domain.Bookings;

/// <summary>The business commands of a rental file (docs/conception/phase-1-exploitation.md §2).</summary>
public enum BookingCommand
{
    Submit = 1,
    RequestInformation = 2,
    ProvideInformation = 3,
    Validate = 4,
    Refuse = 5,
    Expire = 6,
    ConfirmOption = 7,
    MarkPaid = 8,
    Cancel = 9,
    MarkNoShow = 10,
    StartRental = 11,
    CompleteReturn = 12,
    Close = 13,
    Amend = 14,
    AssignVehicle = 15,
    UnassignVehicle = 16,
}

/// <summary>
/// The transition table of docs/conception/phase-1-exploitation.md §2, in one place: the aggregate checks every command
/// against it (any other transition is <see cref="BookingErrors.InvalidTransition"/>) and the screens offer the commands
/// it allows. <c>CreateOption</c> and <c>CreateConfirmed</c> are factories, not transitions.
/// </summary>
/// <remarks>
/// <see cref="BookingCommand.Amend"/> on <see cref="BookingStatus.InProgress"/> is an extension only, and
/// <see cref="BookingCommand.AssignVehicle"/> on <see cref="BookingStatus.InProgress"/> a vehicle swap with a reason:
/// the aggregate enforces these restrictions on top of the table.
/// </remarks>
public static class BookingStateMachine
{
    private static readonly BookingStatus[] BeforeHandover =
    [
        BookingStatus.PendingValidation,
        BookingStatus.InformationRequested,
        BookingStatus.OptionHeld,
        BookingStatus.AwaitingPayment,
        BookingStatus.Confirmed,
    ];

    private static readonly FrozenDictionary<BookingCommand, FrozenSet<BookingStatus>> AllowedFrom =
        new Dictionary<BookingCommand, BookingStatus[]>
        {
            [BookingCommand.Submit] = [BookingStatus.Draft],
            [BookingCommand.RequestInformation] = [BookingStatus.PendingValidation],
            [BookingCommand.ProvideInformation] = [BookingStatus.InformationRequested],
            [BookingCommand.Validate] = [BookingStatus.PendingValidation],
            [BookingCommand.Refuse] = [BookingStatus.PendingValidation, BookingStatus.InformationRequested],
            [BookingCommand.Expire] = [BookingStatus.PendingValidation, BookingStatus.InformationRequested, BookingStatus.OptionHeld, BookingStatus.AwaitingPayment],
            [BookingCommand.ConfirmOption] = [BookingStatus.OptionHeld],
            [BookingCommand.MarkPaid] = [BookingStatus.AwaitingPayment],
            [BookingCommand.Cancel] = BeforeHandover,
            [BookingCommand.MarkNoShow] = [BookingStatus.Confirmed],
            [BookingCommand.StartRental] = [BookingStatus.Confirmed],
            [BookingCommand.CompleteReturn] = [BookingStatus.InProgress],
            [BookingCommand.Close] = [BookingStatus.Returned],
            [BookingCommand.Amend] = [.. BeforeHandover, BookingStatus.InProgress],
            [BookingCommand.AssignVehicle] = [.. BeforeHandover, BookingStatus.InProgress],
            [BookingCommand.UnassignVehicle] = BeforeHandover,
        }.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.ToFrozenSet());

    /// <summary>The statuses from which <paramref name="command"/> is allowed.</summary>
    public static IReadOnlySet<BookingStatus> StatusesFor(BookingCommand command) =>
        AllowedFrom.TryGetValue(command, out var statuses)
            ? statuses
            : throw new ArgumentOutOfRangeException(nameof(command), command, "Unknown booking command.");

    public static bool CanApply(BookingCommand command, BookingStatus status) => StatusesFor(command).Contains(status);

    /// <summary>The commands allowed on a file in <paramref name="status"/>, in declaration order.</summary>
    public static IReadOnlyList<BookingCommand> CommandsFor(BookingStatus status) =>
        [.. Enum.GetValues<BookingCommand>().Where(command => CanApply(command, status))];
}
