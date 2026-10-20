using CarRental.Domain.Common;
using CarRental.Domain.Payments;
using static CarRental.Domain.Tests.Payments.PaymentTestData;

namespace CarRental.Domain.Tests.Payments;

/// <summary>Security deposits (invariant 8): a liability to the customer, released or retained exactly once.</summary>
public sealed class DepositHoldTests
{
    private static DepositHold Held(DepositMode mode = DepositMode.CardImprint, decimal amount = 500m) =>
        DepositHold.Take(Deposit(mode, amount), Agent, Now).Value;

    [Fact]
    public void A_card_imprint_is_held_with_its_authorization_number()
    {
        var details = Deposit(DepositMode.CardImprint, 500m, " AUT-778812 ") with { Notes = " Visa se terminant par 4242 " };

        var deposit = DepositHold.Take(details, Agent, Now).Value;

        deposit.Status.ShouldBe(DepositStatus.Held);
        deposit.IsHeld.ShouldBeTrue();
        deposit.Mode.ShouldBe(DepositMode.CardImprint);
        deposit.Amount.ShouldBe(Tnd(500m));
        deposit.OpenLiability.ShouldBe(Tnd(500m));
        deposit.RetainedAmount.ShouldBe(Money.Zero);
        deposit.ReturnedAmount.ShouldBe(Money.Zero);
        deposit.ExternalReference.ShouldBe("AUT-778812");
        deposit.Notes.ShouldBe("Visa se terminant par 4242");
        deposit.TakenInCashSessionId.ShouldBeNull();
        deposit.TakenAtUtc.ShouldBe(Now);
        deposit.TakenByUserId.ShouldBe(Agent.UserId);
        deposit.TakenByName.ShouldBe("agent@demo.local");
        deposit.DomainEvents.ShouldHaveSingleItem().ShouldBe(new DepositTaken(deposit.Id, details.BookingId, AgencyId, DepositMode.CardImprint));
    }

    [Theory]
    [InlineData(DepositMode.CardImprint)]
    [InlineData(DepositMode.Cheque)]
    public void A_card_imprint_or_a_cheque_needs_its_reference(DepositMode mode)
    {
        DepositHold.Take(Deposit(mode, 500m, reference: " "), Agent, Now).Error.ShouldBe(PaymentsErrors.DepositReferenceRequired);
    }

    [Fact]
    public void A_cash_deposit_is_taken_in_the_cash_session_of_the_agent()
    {
        var deposit = Held(DepositMode.Cash);

        deposit.TakenInCashSessionId.ShouldBe(SessionId);
        deposit.ExternalReference.ShouldBeNull();
        DepositHold.Take(Deposit(DepositMode.Cash, 500m) with { CashSessionId = null }, Agent, Now).Error.ShouldBe(PaymentsErrors.CashSessionRequired);
        Should.Throw<ArgumentException>(() => DepositHold.Take(Deposit(DepositMode.Cheque, 500m) with { CashSessionId = SessionId }, Agent, Now));
    }

    [Fact]
    public void Deposits_are_validated()
    {
        var valid = Deposit(DepositMode.CardImprint, 500m);

        DepositHold.Take(valid with { AgencyId = Guid.Empty }, Agent, Now).Error.ShouldBe(PaymentsErrors.AgencyRequired);
        DepositHold.Take(valid with { BookingId = Guid.Empty }, Agent, Now).Error.ShouldBe(PaymentsErrors.BookingRequired);
        DepositHold.Take(valid with { Mode = (DepositMode)9 }, Agent, Now).Error.ShouldBe(PaymentsErrors.DepositModeInvalid);
        DepositHold.Take(valid with { Amount = Money.Zero }, Agent, Now).Error.ShouldBe(PaymentsErrors.AmountInvalid);
        DepositHold.Take(valid with { Amount = Tnd(-1m) }, Agent, Now).Error.ShouldBe(PaymentsErrors.AmountInvalid);
        DepositHold.Take(valid with { ExternalReference = new string('x', DepositHold.ReferenceMaxLength + 1) }, Agent, Now).Error.ShouldBe(PaymentsErrors.ReferenceTooLong);
        DepositHold.Take(valid with { Notes = new string('x', DepositHold.NotesMaxLength + 1) }, Agent, Now).Error.ShouldBe(PaymentsErrors.NotesTooLong);
    }

    [Fact]
    public void Releasing_returns_the_whole_deposit()
    {
        var deposit = Held();
        deposit.DequeueDomainEvents();
        var returnedAt = Now.AddDays(5);

        deposit.Release(null, Manager, returnedAt).IsSuccess.ShouldBeTrue();

        deposit.Status.ShouldBe(DepositStatus.Released);
        deposit.ReturnedAmount.ShouldBe(Tnd(500m));
        deposit.RetainedAmount.ShouldBe(Money.Zero);
        deposit.OpenLiability.ShouldBe(Money.Zero);
        deposit.RetentionJustification.ShouldBeNull();
        deposit.SettledAtUtc.ShouldBe(returnedAt);
        deposit.SettledByUserId.ShouldBe(Manager.UserId);
        deposit.SettledByName.ShouldBe("gerant@demo.local");
        deposit.DomainEvents.ShouldHaveSingleItem().ShouldBe(new DepositReleased(deposit.Id, deposit.BookingId));
    }

    [Fact]
    public void A_partial_retention_releases_the_rest()
    {
        var deposit = Held(amount: 500m);
        deposit.DequeueDomainEvents();

        deposit.Retain(Tnd(150m), "  Rayure portière avant droite ", " devis-carrosserie-0042.pdf ", null, Agent, Now.AddDays(5)).IsSuccess.ShouldBeTrue();

        deposit.Status.ShouldBe(DepositStatus.PartiallyRetained);
        deposit.RetainedAmount.ShouldBe(Tnd(150m));
        deposit.ReturnedAmount.ShouldBe(Tnd(350m));
        deposit.OpenLiability.ShouldBe(Money.Zero);
        deposit.RetentionJustification.ShouldBe("Rayure portière avant droite");
        deposit.AttachmentReference.ShouldBe("devis-carrosserie-0042.pdf");
        deposit.DomainEvents.ShouldHaveSingleItem().ShouldBe(new DepositRetained(deposit.Id, deposit.BookingId, DepositStatus.PartiallyRetained));
    }

    [Fact]
    public void Retaining_the_whole_amount_retains_the_deposit()
    {
        var deposit = Held(amount: 500m);

        deposit.Retain(Tnd(500m), "Sinistre responsable, franchise", null, null, Agent, Now).IsSuccess.ShouldBeTrue();

        deposit.Status.ShouldBe(DepositStatus.Retained);
        deposit.ReturnedAmount.ShouldBe(Money.Zero);
        deposit.AttachmentReference.ShouldBeNull();
    }

    [Fact]
    public void A_deposit_is_released_or_retained_exactly_once()
    {
        var released = Held();
        released.Release(null, Agent, Now).IsSuccess.ShouldBeTrue();
        released.Release(null, Agent, Now).Error.ShouldBe(PaymentsErrors.DepositAlreadySettled);
        released.Retain(Tnd(10m), "Carburant manquant", null, null, Agent, Now).Error.ShouldBe(PaymentsErrors.DepositAlreadySettled);

        var retained = Held();
        retained.Retain(Tnd(10m), "Carburant manquant", null, null, Agent, Now).IsSuccess.ShouldBeTrue();
        retained.Retain(Tnd(10m), "Encore", null, null, Agent, Now).Error.ShouldBe(PaymentsErrors.DepositAlreadySettled);
        retained.Release(null, Agent, Now).Error.ShouldBe(PaymentsErrors.DepositAlreadySettled);
        retained.RetainedAmount.ShouldBe(Tnd(10m));
    }

    [Fact]
    public void A_retention_needs_a_justification_and_an_amount_within_the_deposit()
    {
        var deposit = Held(amount: 500m);

        deposit.Retain(Tnd(10m), null, null, null, Agent, Now).Error.ShouldBe(PaymentsErrors.DepositJustificationRequired);
        deposit.Retain(Tnd(10m), " ab ", null, null, Agent, Now).Error.ShouldBe(PaymentsErrors.DepositJustificationRequired);
        deposit.Retain(Tnd(10m), new string('x', DepositHold.JustificationMaxLength + 1), null, null, Agent, Now).Error.ShouldBe(PaymentsErrors.DepositJustificationTooLong);
        deposit.Retain(Tnd(10m), "Carburant", new string('x', DepositHold.AttachmentReferenceMaxLength + 1), null, Agent, Now).Error.ShouldBe(PaymentsErrors.DepositAttachmentTooLong);
        deposit.Retain(Money.Zero, "Carburant", null, null, Agent, Now).Error.ShouldBe(PaymentsErrors.DepositRetainedAmountInvalid);
        deposit.Retain(Tnd(-1m), "Carburant", null, null, Agent, Now).Error.ShouldBe(PaymentsErrors.DepositRetainedAmountInvalid);
        deposit.Retain(Tnd(500.001m), "Carburant", null, null, Agent, Now).Error.ShouldBe(PaymentsErrors.DepositRetainedAmountInvalid);
        deposit.Retain(Money.Of(10m, Currency.Eur), "Carburant", null, null, Agent, Now).Error.ShouldBe(PaymentsErrors.DepositRetainedAmountInvalid);
        deposit.IsHeld.ShouldBeTrue("a rejected retention changes nothing");
        deposit.OpenLiability.ShouldBe(Tnd(500m));
    }

    [Fact]
    public void A_cash_deposit_is_settled_from_a_cash_session()
    {
        var deposit = Held(DepositMode.Cash);
        var returnSession = Guid.CreateVersion7();

        deposit.Release(null, Agent, Now).Error.ShouldBe(PaymentsErrors.CashSessionRequired);
        deposit.Retain(Tnd(10m), "Carburant", null, null, Agent, Now).Error.ShouldBe(PaymentsErrors.CashSessionRequired);
        deposit.IsHeld.ShouldBeTrue();

        deposit.Retain(Tnd(10m), "Carburant", null, returnSession, Agent, Now).IsSuccess.ShouldBeTrue();

        deposit.TakenInCashSessionId.ShouldBe(SessionId);
        deposit.SettledInCashSessionId.ShouldBe(returnSession);
    }

    [Fact]
    public void A_card_imprint_is_never_settled_through_a_cash_session()
    {
        Should.Throw<ArgumentException>(() => Held().Release(Guid.CreateVersion7(), Agent, Now));
    }
}
