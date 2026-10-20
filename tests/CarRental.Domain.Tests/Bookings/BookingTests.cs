using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Availability;
using CarRental.Domain.Common;
using static CarRental.Domain.Tests.Bookings.BookingTestData;

namespace CarRental.Domain.Tests.Bookings;

/// <summary>Rules of the rental file besides the transition table: creation, deadlines, reasons, timeline, events.</summary>
public sealed class BookingTests
{
    [Fact]
    public void A_confirmed_booking_starts_its_timeline_with_the_creation()
    {
        var booking = Confirmed();

        booking.Status.ShouldBe(BookingStatus.Confirmed);
        booking.Number.ShouldStartWith("LOC-2027-");
        booking.LastEventSequence.ShouldBe(1);
        var created = booking.Events.ShouldHaveSingleItem();
        created.Type.ShouldBe(BookingEventType.Created);
        created.Sequence.ShouldBe(1);
        created.StatusBefore.ShouldBeNull();
        created.StatusAfter.ShouldBe(BookingStatus.Confirmed);
        created.ActorName.ShouldBe(Agent.Name);
        created.ActorUserId.ShouldBe(Agent.UserId);
        created.OccurredAtUtc.ShouldBe(Now);
        created.Facts["number"].ShouldBe(booking.Number);
        created.Facts["channel"].ShouldBe("Phone");
        created.Facts["total"].ShouldBe("450.000");
        var raised = booking.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<BookingCreated>();
        raised.Status.ShouldBe(BookingStatus.Confirmed);
        raised.TotalInclVat.ShouldBe(Money.Tnd(450m));
    }

    [Fact]
    public void Every_status_but_the_draft_needs_a_main_driver_and_a_payer()
    {
        var number = NextNumber();
        Booking.CreateConfirmed(Guid.CreateVersion7(), Details(parties: new BookingParties(null, null, PayerId)), number, QuoteRef(), Agent, Now)
            .Error.ShouldBe(BookingErrors.MainDriverRequired);
        Booking.CreateOption(Guid.CreateVersion7(), Details(parties: new BookingParties(MainDriverId, null, null)), number, QuoteRef(), Now.AddHours(1), Agent, Now)
            .Error.ShouldBe(BookingErrors.PayerRequired);
        Booking.CreateConfirmed(Guid.CreateVersion7(), Details(parties: new BookingParties(MainDriverId, MainDriverId, PayerId)), number, QuoteRef(), Agent, Now)
            .Error.ShouldBe(BookingErrors.DriversIdentical);
        Booking.CreateConfirmed(Guid.CreateVersion7(), Details(parties: new BookingParties(null, SecondDriverId, PayerId)), number, QuoteRef(), Agent, Now)
            .Error.ShouldBe(BookingErrors.MainDriverRequired);

        var draft = Booking.CreateDraft(Guid.CreateVersion7(), Details(SalesChannel.Web, BookingParties.None), number, QuoteRef(), Customer, Now);
        draft.IsSuccess.ShouldBeTrue();
        draft.Value.Submit(null, Hold, Customer, Now).Error.ShouldBe(BookingErrors.MainDriverRequired);
    }

    [Fact]
    public void The_pick_up_cannot_be_in_the_past_beyond_the_counter_tolerance()
    {
        var number = NextNumber();
        Booking.CreateConfirmed(Guid.CreateVersion7(), Details(pickupAt: Now.AddMinutes(-31), returnAt: Now.AddDays(1)), number, QuoteRef(), Agent, Now)
            .Error.ShouldBe(BookingErrors.PickupInPast);
        Booking.CreateConfirmed(Guid.CreateVersion7(), Details(pickupAt: Now.AddMinutes(-20), returnAt: Now.AddDays(1)), number, QuoteRef(), Agent, Now)
            .IsSuccess.ShouldBeTrue();
        Booking.CreateConfirmed(Guid.CreateVersion7(), Details(pickupAt: PickupAt, returnAt: PickupAt), number, QuoteRef(), Agent, Now)
            .Error.ShouldBe(BookingErrors.PeriodInvalid);
    }

    [Fact]
    public void Creation_checks_its_details()
    {
        var number = NextNumber();
        var details = Details();
        Booking.CreateConfirmed(Guid.CreateVersion7(), details with { AgencyId = Guid.Empty }, number, QuoteRef(), Agent, Now).Error.ShouldBe(BookingErrors.AgencyRequired);
        Booking.CreateConfirmed(Guid.CreateVersion7(), details with { CategoryId = Guid.Empty }, number, QuoteRef(), Agent, Now).Error.ShouldBe(BookingErrors.CategoryRequired);
        Booking.CreateConfirmed(Guid.CreateVersion7(), details with { ReturnLocationId = Guid.Empty }, number, QuoteRef(), Agent, Now).Error.ShouldBe(BookingErrors.LocationRequired);
        Booking.CreateConfirmed(Guid.CreateVersion7(), details with { ChannelReference = new string('R', 65) }, number, QuoteRef(), Agent, Now).Error.ShouldBe(BookingErrors.ChannelReferenceInvalid);
        Booking.CreateConfirmed(Guid.CreateVersion7(), details with { PromoCode = new string('P', 33) }, number, QuoteRef(), Agent, Now).Error.ShouldBe(BookingErrors.PromoCodeInvalid);
        Booking.CreateConfirmed(Guid.CreateVersion7(), details with { Notes = new string('n', 1001) }, number, QuoteRef(), Agent, Now).Error.ShouldBe(BookingErrors.NotesTooLong);
        Booking.CreateOption(Guid.CreateVersion7(), details, number, QuoteRef(), Now, Agent, Now).Error.ShouldBe(BookingErrors.OptionExpiryInvalid);
        Should.Throw<ArgumentException>(() => Booking.CreateConfirmed(
            Guid.CreateVersion7(),
            details,
            DocumentNumber.Create(DocumentType.Invoice, 2027, 1).Value,
            QuoteRef(),
            Agent,
            Now));

        var booking = Booking.CreateConfirmed(Guid.CreateVersion7(), details with { PromoCode = " bienvenue10 ", ChannelReference = " 123456789 ", Notes = "  Vol TU 712  " }, number, QuoteRef(), Agent, Now).Value;
        booking.PromoCode.ShouldBe("BIENVENUE10");
        booking.ChannelReference.ShouldBe("123456789");
        booking.Notes.ShouldBe("Vol TU 712");
        booking.Extras.ShouldHaveSingleItem().Code.ShouldBe("CHILD_SEAT");
        booking.ExtraSelection.ShouldBe("CHILD_SEAT:1");
    }

    [Fact]
    public void A_guaranteed_model_carries_its_vehicle_from_the_creation()
    {
        var booking = Confirmed(guaranteedVehicleId: VehicleA);

        booking.GuaranteedVehicleId.ShouldBe(VehicleA);
        booking.AssignedVehicleId.ShouldBe(VehicleA);
        booking.ToOccupancy().AssignedVehicleId.ShouldBe(VehicleA);
        booking.AssignVehicle(VehicleB, null, null, Agent, Now).Error.ShouldBe(BookingErrors.VehicleGuaranteedModel);
        booking.UnassignVehicle(Agent, Now).Error.ShouldBe(BookingErrors.VehicleGuaranteedModel);
    }

    [Fact]
    public void A_submitted_request_holds_capacity_until_the_end_of_its_hold()
    {
        var booking = Draft();

        BookingTestData.Expect(booking.Submit(Parties, Hold, Customer, Now.AddMinutes(5)));

        booking.Status.ShouldBe(BookingStatus.PendingValidation);
        booking.HoldExpiresAt.ShouldBe(Now.AddMinutes(5) + Hold);
        booking.CurrentDeadline.ShouldBe(booking.HoldExpiresAt);
        booking.SubmittedAtUtc.ShouldBe(Now.AddMinutes(5));
        booking.PendingSinceUtc.ShouldBe(Now.AddMinutes(5));
        booking.MainDriverId.ShouldBe(MainDriverId);
        booking.PayerCustomerId.ShouldBe(PayerId);
        booking.Events[^1].Type.ShouldBe(BookingEventType.Submitted);
        booking.Events[^1].Channel.ShouldBe(SalesChannel.Web);
        booking.Events[^1].Facts["holdExpiresAt"].ShouldBe("2027-06-02T08:05:00Z");
        booking.DomainEvents[^1].ShouldBeOfType<BookingSubmitted>().HoldExpiresAt.ShouldBe(booking.HoldExpiresAt!.Value);
        booking.ToOccupancy().Kind.ShouldBe(OccupancyKind.Planned);
    }

    [Fact]
    public void An_information_request_needs_a_predefined_reason_and_a_message_and_extends_the_hold()
    {
        var booking = Pending();
        var hold = booking.HoldExpiresAt!.Value;

        booking.RequestInformation((InformationRequestReason)42, "texte", Hold, Agent, Now).Error.ShouldBe(BookingErrors.InformationReasonInvalid);
        booking.RequestInformation(InformationRequestReason.NameMismatch, "  ", Hold, Agent, Now).Error.ShouldBe(BookingErrors.ReasonRequired);
        booking.RequestInformation(InformationRequestReason.NameMismatch, new string('x', 501), Hold, Agent, Now).Error.ShouldBe(BookingErrors.ReasonTooLong);
        BookingTestData.Expect(booking.RequestInformation(InformationRequestReason.NameMismatch, "Le nom du permis diffère.", TimeSpan.FromHours(12), Agent, Now.AddHours(3)));

        booking.Status.ShouldBe(BookingStatus.InformationRequested);
        booking.HoldExpiresAt.ShouldBe(hold + TimeSpan.FromHours(12));
        var row = booking.Events[^1];
        row.Type.ShouldBe(BookingEventType.InformationRequested);
        row.Reason.ShouldBe("Le nom du permis diffère.");
        row.Facts["reason"].ShouldBe("NameMismatch");

        BookingTestData.Expect(booking.ProvideInformation(Customer, Now.AddHours(5)));
        booking.Status.ShouldBe(BookingStatus.PendingValidation);
        booking.HoldExpiresAt.ShouldBe(hold + TimeSpan.FromHours(12));
        booking.PendingSinceUtc.ShouldBe(Now.AddHours(5));
    }

    [Fact]
    public void A_request_whose_hold_is_over_can_neither_be_validated_nor_completed()
    {
        var pending = Pending();
        pending.Validate(null, Agent, pending.HoldExpiresAt!.Value).Error.ShouldBe(BookingErrors.HoldExpired);
        pending.RequestInformation(InformationRequestReason.Other, "texte", Hold, Agent, pending.HoldExpiresAt.Value).Error.ShouldBe(BookingErrors.HoldExpired);

        var waiting = InStatus(BookingStatus.InformationRequested);
        waiting.ProvideInformation(Customer, waiting.HoldExpiresAt!.Value.AddSeconds(1)).Error.ShouldBe(BookingErrors.HoldExpired);
    }

    [Fact]
    public void Validation_confirms_or_waits_for_the_payment_in_the_fallback_mode()
    {
        var confirmed = Pending();
        BookingTestData.Expect(confirmed.Validate(null, Agent, Now.AddHours(1)));
        confirmed.Status.ShouldBe(BookingStatus.Confirmed);
        confirmed.HoldExpiresAt.ShouldBeNull();
        confirmed.DomainEvents[^1].ShouldBeOfType<BookingValidated>().Status.ShouldBe(BookingStatus.Confirmed);

        var awaiting = Pending();
        awaiting.Validate(Now, Agent, Now.AddHours(1)).Error.ShouldBe(BookingErrors.PaymentDueInvalid);
        BookingTestData.Expect(awaiting.Validate(Now.AddHours(25), Agent, Now.AddHours(1)));
        awaiting.Status.ShouldBe(BookingStatus.AwaitingPayment);
        awaiting.PaymentDueAt.ShouldBe(Now.AddHours(25));
        awaiting.CurrentDeadline.ShouldBe(Now.AddHours(25));
        awaiting.Events[^1].Facts["paymentDueAt"].ShouldBe("2027-06-02T09:00:00Z");

        BookingTestData.Expect(awaiting.MarkPaid("ORDER-42", Platform, Now.AddHours(30)));
        awaiting.Status.ShouldBe(BookingStatus.Confirmed);
        awaiting.PaymentDueAt.ShouldBeNull();
        awaiting.Events[^1].Facts["paymentReference"].ShouldBe("ORDER-42");
    }

    [Fact]
    public void A_refusal_needs_a_reason()
    {
        var booking = Pending();

        booking.Refuse(null, Agent, Now).Error.ShouldBe(BookingErrors.ReasonRequired);
        booking.Refuse(new string('r', 501), Agent, Now).Error.ShouldBe(BookingErrors.ReasonTooLong);
        BookingTestData.Expect(booking.Refuse("  Permis trop récent ", Agent, Now.AddHours(1)));

        booking.Status.ShouldBe(BookingStatus.Refused);
        booking.StatusReason.ShouldBe("Permis trop récent");
        booking.Events[^1].Reason.ShouldBe("Permis trop récent");
        booking.ToOccupancy().Kind.ShouldBe(OccupancyKind.None);
    }

    [Fact]
    public void Holds_options_and_payment_deadlines_expire_once_passed()
    {
        var request = Pending();
        request.Expire(Platform, request.HoldExpiresAt!.Value.AddTicks(-1)).Error.ShouldBe(BookingErrors.DeadlineNotReached);
        BookingTestData.Expect(request.Expire(Platform, request.HoldExpiresAt.Value));
        request.Status.ShouldBe(BookingStatus.Expired);
        request.Events[^1].ActorName.ShouldBe("system:tests");
        request.Events[^1].Facts["deadline"].ShouldBe("2027-06-02T08:00:00Z");
        request.DomainEvents[^1].ShouldBeOfType<BookingExpired>().PreviousStatus.ShouldBe(BookingStatus.PendingValidation);

        var option = Option(TimeSpan.FromHours(48));
        option.ConfirmOption(Agent, Now.AddHours(48)).Error.ShouldBe(BookingErrors.OptionExpired);
        option.Expire(Platform, Now.AddHours(47)).Error.ShouldBe(BookingErrors.DeadlineNotReached);
        BookingTestData.Expect(option.Expire(Platform, Now.AddHours(48)));

        var awaiting = InStatus(BookingStatus.AwaitingPayment);
        awaiting.Expire(Platform, Now.AddHours(24)).Error.ShouldBe(BookingErrors.DeadlineNotReached);
        BookingTestData.Expect(awaiting.Expire(Platform, Now.AddHours(25)));
    }

    [Fact]
    public void A_cancellation_records_its_reason_and_fee()
    {
        var booking = Confirmed();

        booking.Cancel(" ", Money.Zero, feeWaived: false, Agent, Now).Error.ShouldBe(BookingErrors.ReasonRequired);
        booking.Cancel("Annulation", Money.Tnd(-1m), feeWaived: false, Agent, Now).Error.ShouldBe(BookingErrors.CancellationFeeInvalid);
        BookingTestData.Expect(booking.Cancel("Changement de programme", Money.Tnd(90m), feeWaived: false, Agent, Now.AddDays(9)));

        booking.Status.ShouldBe(BookingStatus.Cancelled);
        booking.CancellationFee.ShouldBe(Money.Tnd(90m));
        booking.StatusReason.ShouldBe("Changement de programme");
        var row = booking.Events[^1];
        row.Facts["fee"].ShouldBe("90.000");
        row.Facts["feeWaived"].ShouldBe("false");
        booking.DomainEvents[^1].ShouldBeOfType<BookingCancelled>().CancellationFee.ShouldBe(Money.Tnd(90m));

        var waived = Confirmed();
        BookingTestData.Expect(waived.Cancel("Panne du véhicule prévu", Money.Tnd(90m), feeWaived: true, Manager, Now.AddDays(9)));
        waived.CancellationFee.ShouldBe(Money.Zero);
        waived.Events[^1].Facts["computedFee"].ShouldBe("90.000");
        waived.Events[^1].Facts["feeWaived"].ShouldBe("true");
    }

    [Fact]
    public void A_no_show_is_only_recorded_after_the_delay_and_with_a_reason()
    {
        var booking = Confirmed();

        booking.MarkNoShow("Absent", NoShowDelay, Agent, PickupAt + NoShowDelay - TimeSpan.FromMinutes(1)).Error.ShouldBe(BookingErrors.NoShowTooEarly);
        booking.MarkNoShow(null, NoShowDelay, Agent, PickupAt + NoShowDelay).Error.ShouldBe(BookingErrors.ReasonRequired);
        BookingTestData.Expect(booking.MarkNoShow("Client injoignable", NoShowDelay, Agent, PickupAt + NoShowDelay));

        booking.Status.ShouldBe(BookingStatus.NoShow);
        booking.Events[^1].Reason.ShouldBe("Client injoignable");
    }

    [Fact]
    public void A_departure_needs_a_vehicle_and_a_return_comes_after_it()
    {
        var booking = Confirmed();
        booking.StartRental(PickupAt, Agent, PickupAt).Error.ShouldBe(BookingErrors.VehicleRequired);
        BookingTestData.Expect(booking.AssignVehicle(VehicleA, null, null, Agent, Now));
        booking.StartRental(PickupAt.AddMinutes(1), Agent, PickupAt).Error.ShouldBe(BookingErrors.ActualPickupInvalid);
        BookingTestData.Expect(booking.StartRental(PickupAt.AddMinutes(-15), Agent, PickupAt));

        booking.ActualPickupAt.ShouldBe(PickupAt.AddMinutes(-15));
        booking.ToOccupancy().Kind.ShouldBe(OccupancyKind.InProgress);
        booking.ToOccupancy().ActualPickupAt.ShouldBe(PickupAt.AddMinutes(-15));
        booking.IsLateAt(ReturnAt).ShouldBeFalse();
        booking.IsLateAt(ReturnAt.AddMinutes(1)).ShouldBeTrue();
        booking.DomainEvents[^1].ShouldBeOfType<BookingRentalStarted>().VehicleId.ShouldBe(VehicleA);

        booking.CompleteReturn(PickupAt.AddMinutes(-20), Agent, ReturnAt).Error.ShouldBe(BookingErrors.ActualReturnInvalid);
        booking.CompleteReturn(ReturnAt.AddMinutes(1), Agent, ReturnAt).Error.ShouldBe(BookingErrors.ActualReturnInvalid);
        BookingTestData.Expect(booking.CompleteReturn(ReturnAt.AddMinutes(-30), Agent, ReturnAt));
        booking.ActualReturnAt.ShouldBe(ReturnAt.AddMinutes(-30));
        booking.ToOccupancy().Kind.ShouldBe(OccupancyKind.Returned);

        BookingTestData.Expect(booking.Close(Agent, ReturnAt.AddHours(1)));
        booking.ClosedAtUtc.ShouldBe(ReturnAt.AddHours(1));
        booking.ToOccupancy().Kind.ShouldBe(OccupancyKind.Returned);
        booking.DomainEvents[^1].ShouldBeOfType<BookingClosed>();
    }

    [Fact]
    public void Every_command_numbers_its_timeline_row_after_the_previous_one()
    {
        var booking = Draft();
        BookingTestData.Expect(booking.Submit(Parties, Hold, Customer, Now));
        BookingTestData.Expect(booking.RequestInformation(InformationRequestReason.MissingDocuments, "Verso du permis manquant.", Hold, Agent, Now.AddHours(1)));
        BookingTestData.Expect(booking.ProvideInformation(Customer, Now.AddHours(2)));
        BookingTestData.Expect(booking.AssignVehicle(VehicleA, null, null, Agent, Now.AddHours(3)));
        BookingTestData.Expect(booking.Validate(null, Agent, Now.AddHours(4)));

        booking.Events.Select(e => e.Sequence).ShouldBe([1, 2, 3, 4, 5, 6]);
        booking.Events.Select(e => e.Type).ShouldBe(
        [
            BookingEventType.Created,
            BookingEventType.Submitted,
            BookingEventType.InformationRequested,
            BookingEventType.InformationProvided,
            BookingEventType.VehicleAssigned,
            BookingEventType.Validated,
        ]);
        booking.LastEventSequence.ShouldBe(6);
        booking.Events.Select(e => e.Id).Distinct().Count().ShouldBe(6);
        booking.DomainEvents.Count.ShouldBe(6);
    }

    [Fact]
    public void Escalation_is_recorded_once_per_pending_period()
    {
        var booking = Pending();

        booking.MarkEscalated(Platform, Now.AddHours(2)).Value.ShouldBeTrue();
        booking.MarkEscalated(Platform, Now.AddHours(3)).Value.ShouldBeFalse();
        booking.EscalatedAtUtc.ShouldBe(Now.AddHours(2));
        booking.Events[^1].Type.ShouldBe(BookingEventType.Escalated);
        booking.Events[^1].StatusAfter.ShouldBe(BookingStatus.PendingValidation);

        BookingTestData.Expect(booking.RequestInformation(InformationRequestReason.MissingDocuments, "Permis manquant.", Hold, Agent, Now.AddHours(4)));
        booking.MarkEscalated(Platform, Now.AddHours(5)).Error.ShouldBe(BookingErrors.InvalidTransition);
        BookingTestData.Expect(booking.ProvideInformation(Customer, Now.AddHours(6)));
        booking.EscalatedAtUtc.ShouldBeNull();
        booking.MarkEscalated(Platform, Now.AddHours(9)).Value.ShouldBeTrue();
    }

    [Fact]
    public void An_actor_without_name_is_the_system()
    {
        new BookingActor(null, "  ").Name.ShouldBe("system");
        BookingActor.System().Name.ShouldBe("system");
        new BookingActor(Guid.CreateVersion7(), new string('a', 300)).Name.Length.ShouldBe(256);
    }

    [Fact]
    public void Extras_are_normalized_and_stored_in_code_order()
    {
        var choices = ExtraChoice.Normalize([ExtraChoice.Create("gps", 1).Value, ExtraChoice.Create("CHILD_SEAT", 1).Value, ExtraChoice.Create("child_seat", 2).Value]);

        choices.IsSuccess.ShouldBeTrue();
        ExtraChoice.Format(choices.Value).ShouldBe("CHILD_SEAT:3;GPS:1");
        ExtraChoice.Parse("CHILD_SEAT:3;GPS:1").ShouldBe(choices.Value);
        ExtraChoice.Parse(null).ShouldBeEmpty();
        ExtraChoice.Create("1X", 1).Error.ShouldBe(BookingErrors.ExtrasInvalid);
        ExtraChoice.Create("GPS", 0).Error.ShouldBe(BookingErrors.ExtrasInvalid);
        ExtraChoice.Create("GPS", 11).Error.ShouldBe(BookingErrors.ExtrasInvalid);
        ExtraChoice.Normalize([ExtraChoice.Create("GPS", 6).Value, ExtraChoice.Create("GPS", 5).Value]).Error.ShouldBe(BookingErrors.ExtrasInvalid);
    }

    [Fact]
    public void Attribution_fields_are_trimmed_and_bounded()
    {
        BookingAttribution.Create(" whatsapp ", "facebook", null, "ete2027").Value.Source.ShouldBe("whatsapp");
        BookingAttribution.Create(null, " ", "").Value.ShouldBeSameAs(BookingAttribution.None);
        BookingAttribution.None.IsEmpty.ShouldBeTrue();
        BookingAttribution.Create(new string('s', 101)).Error.ShouldBe(BookingErrors.AttributionInvalid);
    }
}
