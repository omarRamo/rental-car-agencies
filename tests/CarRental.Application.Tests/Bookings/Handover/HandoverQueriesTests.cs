using CarRental.Application.Bookings.Contracts;
using CarRental.Application.Common.Files;
using CarRental.Application.Identity.Contracts;
using CarRental.Domain.Bookings;
using CarRental.Domain.Bookings.Handover;
using CarRental.Domain.Common;
using static CarRental.Application.Tests.Bookings.Handover.HandoverHarness;

namespace CarRental.Application.Tests.Bookings.Handover;

/// <summary>The day list of the hand-over screen, the summary of a file and the files streamed by the back office.</summary>
public sealed class HandoverQueriesTests
{
    [Fact]
    public async Task The_day_lists_departures_with_their_readiness()
    {
        var harness = new HandoverHarness();
        var booking = await harness.ConfirmedAsync();
        var day = TunisTime.DateOf(harness.Bookings.PickupAt);

        var before = Ok(await harness.Queries.GetDayAsync(harness.Bookings.AgencyId, day, Ct));

        var item = before.Departures.ShouldHaveSingleItem();
        (item.BookingId, item.Number, item.IsDone, item.Inspection).ShouldBe((booking.Id, booking.Number, false, (InspectionStatus?)null));
        item.Readiness.VehicleAssigned.ShouldBeTrue();
        item.Readiness.VehicleCompliant.ShouldBeTrue();
        item.Readiness.Paid.ShouldBeFalse();
        item.Readiness.AmountDue.ShouldBe(booking.TotalInclVat);
        item.Readiness.DepositHeld.ShouldBeFalse();
        item.Readiness.IsReady.ShouldBeFalse();
        before.Returns.ShouldBeEmpty();

        harness.Settle(booking);
        Ok(await harness.Departures.StartAsync(booking.Id, Ct));
        var after = Ok(await harness.Queries.GetDayAsync(harness.Bookings.AgencyId, day, Ct)).Departures.ShouldHaveSingleItem();
        (after.Readiness.Paid, after.Readiness.DepositHeld, after.Inspection).ShouldBe((true, true, (InspectionStatus?)InspectionStatus.Draft));
        after.Readiness.Documents.ShouldBe(DocumentsStateInfo.Complete);
        after.Readiness.IsReady.ShouldBeTrue();
    }

    [Fact]
    public async Task Late_returns_are_listed_whatever_their_day()
    {
        var harness = new HandoverHarness();
        var booking = await harness.InProgressAsync();
        harness.Bookings.Clock.SetUtcNow(harness.Bookings.ReturnAt.AddHours(5));

        var today = Ok(await harness.Queries.GetDayAsync(harness.Bookings.AgencyId, TunisTime.DateOf(harness.Bookings.ReturnAt).AddDays(1), Ct));

        var late = today.LateReturns.ShouldHaveSingleItem();
        (late.BookingId, late.IsLate, late.CategoryCode).ShouldBe((booking.Id, true, "ECMR"));
        today.Returns.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_day_needs_the_hand_over_permission()
    {
        var harness = new HandoverHarness().As(Roles.Accountant);

        (await harness.Queries.GetDayAsync(harness.Bookings.AgencyId, new DateOnly(2027, 6, 11), Ct)).Error.ShouldBe(BookingErrors.Forbidden);
        (await harness.Queries.GetAgenciesAsync(Ct)).Error.ShouldBe(BookingErrors.Forbidden);
    }

    [Fact]
    public async Task The_summary_shows_the_inspections_the_contract_and_the_balance()
    {
        var harness = new HandoverHarness();
        var booking = await harness.InProgressAsync();

        var summary = Ok(await harness.Queries.GetSummaryAsync(booking.Id, Ct));

        summary.Departure.ShouldNotBeNull().Status.ShouldBe(InspectionStatus.Completed);
        summary.Return.ShouldBeNull();
        var contract = summary.Contracts.ShouldHaveSingleItem();
        (contract.Number, contract.Version, contract.FileName).ShouldBe((booking.Number, 1, $"{booking.Number}-v1.pdf"));
        summary.Balance.ShouldNotBeNull().Balance.ShouldBe(Money.Zero);
    }

    [Fact]
    public async Task Photos_and_contracts_are_streamed_to_readers_of_the_files_and_signatures_to_hand_over_staff_only()
    {
        var harness = new HandoverHarness();
        var booking = await harness.InProgressAsync();
        var inspection = harness.Bookings.Db.Rows<Inspection>().Single();
        var photo = inspection.Photos[0];
        var signature = inspection.Signatures[0];
        var contract = harness.Bookings.Db.Rows<RentalContract>().Single();

        var full = Ok(await harness.Queries.FindPhotoAsync(photo.Id, thumbnail: false, Ct));
        var thumb = Ok(await harness.Queries.FindPhotoAsync(photo.Id, thumbnail: true, Ct));
        (full.Container, full.Path, full.ContentType).ShouldBe((FileContainers.Inspections, photo.StoragePath, HandoverFile.Jpeg));
        thumb.Path.ShouldBe(photo.ThumbnailPath);
        thumb.ETag.ShouldNotBe(full.ETag);
        var pdf = Ok(await harness.Queries.FindContractAsync(contract.Id, Ct));
        (pdf.Container, pdf.ContentType, pdf.FileName).ShouldBe((FileContainers.Contracts, HandoverFile.Pdf, contract.FileName));
        await using (var download = await harness.Queries.OpenAsync(pdf, Ct))
        {
            download.ShouldNotBeNull().Length.ShouldBeGreaterThan(0);
        }

        Ok(await harness.Queries.FindSignatureAsync(signature.Id, Ct)).ContentType.ShouldBe(HandoverFile.Png);
        (await harness.Queries.FindPhotoAsync(Guid.CreateVersion7(), false, Ct)).Error.ShouldBe(HandoverErrors.PhotoNotFound);
        (await harness.Queries.FindContractAsync(Guid.CreateVersion7(), Ct)).Error.ShouldBe(HandoverErrors.ContractNotFound);

        harness.As(Roles.Accountant);
        Ok(await harness.Queries.FindPhotoAsync(photo.Id, false, Ct));
        Ok(await harness.Queries.FindContractAsync(contract.Id, Ct));
        (await harness.Queries.FindSignatureAsync(signature.Id, Ct)).Error.ShouldBe(BookingErrors.Forbidden, "a signature is personal data");
        _ = booking;
    }

    [Fact]
    public async Task Other_modules_read_the_charges_contracts_and_balance_of_a_file()
    {
        var harness = new HandoverHarness();
        var booking = await harness.InProgressAsync();

        (await harness.Contracts.GetReturnChargesAsync(booking.Id, Ct)).Lines.ShouldBeEmpty("not returned yet");
        (await harness.Contracts.GetContractsAsync(booking.Id, Ct)).ShouldHaveSingleItem().Language.ShouldBe(ContractLanguageInfo.French);
        var balance = (await harness.Contracts.GetBalanceAsync(booking.Id, Ct)).ShouldNotBeNull();
        (balance.QuoteTotalInclVat, balance.NetPaid, balance.DepositRequired, balance.DepositsHeld, balance.DepositsHeldCount)
            .ShouldBe((booking.TotalInclVat, booking.TotalInclVat, booking.Deposit, booking.Deposit, 1));
        (await harness.Contracts.GetBalanceAsync(Guid.CreateVersion7(), Ct)).ShouldBeNull();
        ReturnChargeInfo.LabelKeyOf(ReturnChargeKindInfo.FranchiseCap).ShouldBe("ReturnCharge.Label.FranchiseCap");
    }
}
