using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;
using static CarRental.Domain.Tests.Bookings.BookingTestData;

namespace CarRental.Domain.Tests.Bookings;

/// <summary>
/// The status machine of docs/conception/phase-1-exploitation.md §2, exhaustively: every command from every status, on
/// a real aggregate. An allowed command reaches its target status and writes exactly one timeline row; any other
/// transition is <c>booking.invalid_transition</c> and changes nothing.
/// </summary>
public sealed class BookingStateMachineTests
{
    private static readonly BookingStatus[] BeforeHandover =
    [
        BookingStatus.PendingValidation,
        BookingStatus.InformationRequested,
        BookingStatus.OptionHeld,
        BookingStatus.AwaitingPayment,
        BookingStatus.Confirmed,
    ];

    /// <summary>The table of §2, written independently of the implementation.</summary>
    private static readonly Dictionary<BookingCommand, BookingStatus[]> Specification = new()
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
    };

    private static readonly Dictionary<BookingCommand, BookingStatus?> Targets = new()
    {
        [BookingCommand.Submit] = BookingStatus.PendingValidation,
        [BookingCommand.RequestInformation] = BookingStatus.InformationRequested,
        [BookingCommand.ProvideInformation] = BookingStatus.PendingValidation,
        [BookingCommand.Validate] = BookingStatus.Confirmed,
        [BookingCommand.Refuse] = BookingStatus.Refused,
        [BookingCommand.Expire] = BookingStatus.Expired,
        [BookingCommand.ConfirmOption] = BookingStatus.Confirmed,
        [BookingCommand.MarkPaid] = BookingStatus.Confirmed,
        [BookingCommand.Cancel] = BookingStatus.Cancelled,
        [BookingCommand.MarkNoShow] = BookingStatus.NoShow,
        [BookingCommand.StartRental] = BookingStatus.InProgress,
        [BookingCommand.CompleteReturn] = BookingStatus.Returned,
        [BookingCommand.Close] = BookingStatus.Closed,
        [BookingCommand.Amend] = null,
        [BookingCommand.AssignVehicle] = null,
        [BookingCommand.UnassignVehicle] = null,
    };

    public static TheoryData<BookingCommand, BookingStatus> EveryCommandFromEveryStatus()
    {
        var data = new TheoryData<BookingCommand, BookingStatus>();
        foreach (var command in Enum.GetValues<BookingCommand>())
        {
            foreach (var status in Enum.GetValues<BookingStatus>())
            {
                data.Add(command, status);
            }
        }

        return data;
    }

    [Fact]
    public void The_transition_table_is_the_one_of_the_conception()
    {
        Enum.GetValues<BookingCommand>().ShouldBe(Specification.Keys, ignoreOrder: true);
        foreach (var (command, statuses) in Specification)
        {
            BookingStateMachine.StatusesFor(command).ShouldBe(statuses, ignoreOrder: true, command.ToString());
        }
    }

    [Fact]
    public void Terminal_statuses_allow_no_command()
    {
        foreach (var status in new[] { BookingStatus.Closed, BookingStatus.Refused, BookingStatus.Expired, BookingStatus.Cancelled, BookingStatus.NoShow })
        {
            status.IsTerminal().ShouldBeTrue();
            BookingStateMachine.CommandsFor(status).ShouldBeEmpty(status.ToString());
        }

        foreach (var status in new[] { BookingStatus.Draft, BookingStatus.PendingValidation, BookingStatus.Confirmed, BookingStatus.InProgress, BookingStatus.Returned })
        {
            status.IsTerminal().ShouldBeFalse();
            BookingStateMachine.CommandsFor(status).ShouldNotBeEmpty(status.ToString());
        }
    }

    [Theory]
    [MemberData(nameof(EveryCommandFromEveryStatus))]
    public void A_command_follows_the_table(BookingCommand command, BookingStatus status)
    {
        var booking = InStatus(status);
        booking.Status.ShouldBe(status);
        var allowed = Specification[command].Contains(status);
        if (allowed)
        {
            Prepare(command, booking);
        }

        booking.DequeueDomainEvents();
        var rowsBefore = booking.Events.Count;
        var sequenceBefore = booking.LastEventSequence;

        var result = Apply(command, booking);

        if (!allowed)
        {
            result.Error.ShouldBe(BookingErrors.InvalidTransition);
            booking.Status.ShouldBe(status);
            booking.Events.Count.ShouldBe(rowsBefore);
            booking.LastEventSequence.ShouldBe(sequenceBefore);
            booking.DomainEvents.ShouldBeEmpty();
            return;
        }

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
        booking.Status.ShouldBe(Targets[command] ?? status);
        booking.Events.Count.ShouldBe(rowsBefore + 1);
        booking.LastEventSequence.ShouldBe(sequenceBefore + 1);
        var row = booking.Events[^1];
        row.Sequence.ShouldBe(booking.LastEventSequence);
        row.StatusBefore.ShouldBe(status);
        row.StatusAfter.ShouldBe(booking.Status);
        row.BookingId.ShouldBe(booking.Id);
        booking.DomainEvents.Count.ShouldBe(1);
    }

    [Fact]
    public void Statuses_map_onto_the_occupancy_kinds_of_ADR_0025()
    {
        var expected = new Dictionary<BookingStatus, OccupancyKind>
        {
            [BookingStatus.Draft] = OccupancyKind.None,
            [BookingStatus.Refused] = OccupancyKind.None,
            [BookingStatus.Expired] = OccupancyKind.None,
            [BookingStatus.Cancelled] = OccupancyKind.None,
            [BookingStatus.NoShow] = OccupancyKind.None,
            [BookingStatus.PendingValidation] = OccupancyKind.Planned,
            [BookingStatus.InformationRequested] = OccupancyKind.Planned,
            [BookingStatus.OptionHeld] = OccupancyKind.Planned,
            [BookingStatus.AwaitingPayment] = OccupancyKind.Planned,
            [BookingStatus.Confirmed] = OccupancyKind.Planned,
            [BookingStatus.InProgress] = OccupancyKind.InProgress,
            [BookingStatus.Returned] = OccupancyKind.Returned,
            [BookingStatus.Closed] = OccupancyKind.Returned,
        };

        Enum.GetValues<BookingStatus>().ShouldBe(expected.Keys, ignoreOrder: true);
        foreach (var (status, kind) in expected)
        {
            status.ToOccupancyKind().ShouldBe(kind, status.ToString());
            BookingStatusRules.Occupying.Contains(status).ShouldBe(kind != OccupancyKind.None, status.ToString());
            BookingStatusRules.Planned.Contains(status).ShouldBe(kind == OccupancyKind.Planned, status.ToString());
        }
    }

    [Fact]
    public void Open_files_are_every_status_but_the_draft_and_the_terminal_ones()
    {
        foreach (var status in Enum.GetValues<BookingStatus>())
        {
            BookingStatusRules.Open.Contains(status).ShouldBe(status != BookingStatus.Draft && !status.IsTerminal(), status.ToString());
            status.IsOpen().ShouldBe(BookingStatusRules.Open.Contains(status));
        }
    }

    [Fact]
    public void Unknown_commands_are_bugs()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => BookingStateMachine.StatusesFor((BookingCommand)999));
    }

    /// <summary>Gives the file what the command needs besides its status (a vehicle to start or to unassign).</summary>
    private static void Prepare(BookingCommand command, Booking booking)
    {
        if (command is BookingCommand.StartRental or BookingCommand.UnassignVehicle && booking.AssignedVehicleId is null)
        {
            Expect(booking.AssignVehicle(VehicleA, null, null, Agent, Now.AddHours(1)));
        }
    }

    /// <summary>The command with valid arguments and a valid instant for it, so that only the status decides.</summary>
    private static Result Apply(BookingCommand command, Booking booking)
    {
        var duringRental = PickupAt.AddHours(1);
        return command switch
        {
            BookingCommand.Submit => booking.Submit(Parties, Hold, Customer, Now.AddHours(2)),
            BookingCommand.RequestInformation => booking.RequestInformation(InformationRequestReason.UnreadableDocuments, "Photo du permis illisible.", Hold, Agent, Now.AddHours(2)),
            BookingCommand.ProvideInformation => booking.ProvideInformation(Customer, Now.AddHours(2)),
            BookingCommand.Validate => booking.Validate(null, Agent, Now.AddHours(2)),
            BookingCommand.Refuse => booking.Refuse("Documents non conformes", Agent, Now.AddHours(2)),
            BookingCommand.Expire => booking.Expire(Platform, Now.AddDays(5)),
            BookingCommand.ConfirmOption => booking.ConfirmOption(Agent, Now.AddHours(2)),
            BookingCommand.MarkPaid => booking.MarkPaid("CTP-123", Platform, Now.AddHours(2)),
            BookingCommand.Cancel => booking.Cancel("Le client annule", Money.Zero, feeWaived: false, Agent, Now.AddHours(2)),
            BookingCommand.MarkNoShow => booking.MarkNoShow("Client absent", NoShowDelay, Agent, PickupAt + NoShowDelay),
            BookingCommand.StartRental => booking.StartRental(PickupAt, Agent, PickupAt),
            BookingCommand.CompleteReturn => booking.CompleteReturn(ReturnAt, Agent, ReturnAt),
            BookingCommand.Close => booking.Close(Agent, ReturnAt.AddHours(3)),
            BookingCommand.Amend => booking.Amend(
                booking.CurrentChanges with { ReturnAt = booking.ReturnAt.AddDays(1) },
                QuoteRef(booking.CurrentQuoteVersion + 1),
                reason: null,
                manualDiscount: false,
                Agent,
                booking.Status == BookingStatus.InProgress ? duringRental : Now.AddHours(2)),
            BookingCommand.AssignVehicle => booking.Status == BookingStatus.InProgress
                ? booking.AssignVehicle(VehicleB, null, "Panne de climatisation", Agent, duringRental)
                : booking.AssignVehicle(VehicleB, null, null, Agent, Now.AddHours(2)),
            BookingCommand.UnassignVehicle => booking.UnassignVehicle(Agent, Now.AddHours(2)),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
        };
    }
}
