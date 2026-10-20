using System.Globalization;
using CarRental.Domain.Common;

namespace CarRental.Domain.Pricing;

/// <summary>
/// The pricing engine (docs/conception/phase-1-exploitation.md §4): the only place where a rental price is computed.
/// Pure and deterministic: no I/O, no clock (the booking instant is part of the request).
/// </summary>
/// <remarks>
/// Steps: rental days (24 h units from the pick-up, late tolerance) → price of each day (Tunis date of pick-up + i × 24 h:
/// highest-priority event, else season level, else base price) → channel rule then dinar rounding → minimum duration of
/// the touched periods → duration tier discount → promotions on the subtotal after the tier → guaranteed model and
/// extras (chosen and automatic) → VAT split of the TTC lines per rate with group rounding → daily contribution entered
/// excluding VAT → deposit. All amounts are rounded to the millime away from zero.
/// </remarks>
public static class PricingEngine
{
    public const int MaximumRentalDays = 365;
    public const int MaximumExtraQuantity = 10;
    public const int MaximumDrivers = 5;

    public static Result<PriceCalculation> Calculate(
        RatePlan plan,
        ChannelPricing channelPricing,
        PricingRequest request,
        PricingParameters parameters,
        DepositTerms deposits)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(channelPricing);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(deposits);

        var days = RentalDays.Count(request.RentalPeriod, parameters.LateReturnTolerance);
        if (days > MaximumRentalDays)
        {
            return PricingErrors.RentalTooLong;
        }

        var daily = PriceDays(plan, channelPricing, request, parameters, days);
        if (daily.IsFailure)
        {
            return daily.Error;
        }

        var (dailyRates, requiredMinimum) = daily.Value;
        if (days < requiredMinimum)
        {
            return PricingErrors.MinimumDurationNotMet(requiredMinimum);
        }

        var selections = MergeSelections(plan, request.Extras);
        if (selections.IsFailure)
        {
            return selections.Error;
        }

        var pickupDate = TunisTime.DateOf(request.RentalPeriod.Start);
        var driverAges = DriverAges(request.DriverBirthDates, pickupDate);
        if (driverAges.IsFailure)
        {
            return driverAges.Error;
        }

        var lines = new List<DraftLine>();
        lines.AddRange(RentalLines(plan, dailyRates));

        var rentalSubtotal = dailyRates.Select(r => r.Price).Sum();
        var tierDiscount = DurationDiscount(plan, request.CategoryId, days, rentalSubtotal);
        if (tierDiscount is { } tierLine)
        {
            lines.Add(tierLine);
        }

        var afterTier = rentalSubtotal + (tierDiscount?.InclVat ?? Money.Zero);
        var promotions = Promotions.Apply(plan, request, days, afterTier);
        lines.AddRange(promotions.Lines);

        if (request.GuaranteedModel is { } guaranteed)
        {
            lines.Add(new DraftLine(
                PriceLineKind.GuaranteedModel,
                QuoteLineCodes.GuaranteedModel,
                QuoteLabels.GuaranteedModel,
                [guaranteed.VehicleLabel],
                days,
                guaranteed.DailySupplement,
                guaranteed.DailySupplement * days));
        }

        var youngDrivers = driverAges.Value.Count(age => age < parameters.YoungDriverAgeBelow);
        lines.AddRange(ExtraLines(plan, request, parameters, selections.Value, youngDrivers, days));

        var priced = Taxes.Split(lines, parameters.VatRate);
        if (Taxes.DailyContribution(parameters, days) is { } contribution)
        {
            priced.Add(contribution);
        }

        var franchiseWaiver = plan.Extras.Any(e => e.IsActive && e.Kind == ExtraKind.FranchiseWaiver && selections.Value.ContainsKey(e.Code));
        return new PriceCalculation(
            plan,
            days,
            dailyRates,
            priced,
            franchiseWaiver ? deposits.ReducedDeposit : deposits.Deposit,
            franchiseWaiver,
            promotions.Applied,
            promotions.CodeStatus,
            driverAges.Value,
            request,
            parameters,
            channelPricing,
            deposits);
    }

    /// <summary>Price of every rental day and the largest minimum duration of the seasons and events they touch.</summary>
    private static Result<(IReadOnlyList<DailyRate> Rates, int RequiredMinimum)> PriceDays(
        RatePlan plan,
        ChannelPricing channelPricing,
        PricingRequest request,
        PricingParameters parameters,
        int days)
    {
        var rates = new List<DailyRate>(days);
        var requiredMinimum = 0;
        for (var i = 0; i < days; i++)
        {
            var date = TunisTime.DateOf(request.RentalPeriod.Start.AddDays(i));
            var events = plan.FindEvents(date);
            var season = plan.FindSeason(date);
            requiredMinimum = Math.Max(requiredMinimum, season?.MinimumDays ?? 0);
            foreach (var priorityEvent in events)
            {
                requiredMinimum = Math.Max(requiredMinimum, priorityEvent.MinimumDays);
            }

            if (ResolvePrice(plan, request.CategoryId, events, season) is not { } resolved)
            {
                return PricingErrors.CategoryNotPriced;
            }

            rates.Add(new DailyRate(date, resolved.Period, resolved.Price, ApplyChannel(resolved.Price, channelPricing, parameters.RoundDailyPricesToDinar)));
        }

        return Result.Success<(IReadOnlyList<DailyRate>, int)>((rates, requiredMinimum));
    }

    /// <summary>The highest-priority event priced for the category, else the season level, else the base price.</summary>
    private static (RatePeriodKey Period, Money Price)? ResolvePrice(
        RatePlan plan,
        Guid categoryId,
        IReadOnlyList<PriorityEvent> events,
        Season? season)
    {
        foreach (var priorityEvent in events)
        {
            var key = RatePeriodKey.ForEvent(priorityEvent.Id);
            if (plan.FindPrice(categoryId, key) is { } eventPrice)
            {
                return (key, eventPrice);
            }
        }

        if (season is not null)
        {
            var key = RatePeriodKey.ForSeason(season.Level);
            if (plan.FindPrice(categoryId, key) is { } seasonPrice)
            {
                return (key, seasonPrice);
            }
        }

        return plan.FindPrice(categoryId, RatePeriodKey.Base) is { } basePrice ? (RatePeriodKey.Base, basePrice) : null;
    }

    /// <summary>Channel rule on a daily price, then rounding (to the dinar when enabled) in a single rounding step.</summary>
    private static Money ApplyChannel(Money price, ChannelPricing channelPricing, bool roundToDinar)
    {
        var raw = channelPricing.Mode == ChannelPricingMode.Markup
            ? price.Amount * (1m + (channelPricing.MarkupPercent / 100m))
            : price.Amount;
        return Money.Tnd(roundToDinar ? Math.Round(raw, 0, MidpointRounding.AwayFromZero) : raw);
    }

    /// <summary>One line per period (base price, season level, event) and daily price, in the order of the first day.</summary>
    private static IEnumerable<DraftLine> RentalLines(RatePlan plan, IReadOnlyList<DailyRate> dailyRates)
    {
        foreach (var group in dailyRates.GroupBy(r => (r.Period, r.Price)))
        {
            var (period, price) = group.Key;
            var (labelKey, arguments) = period.Kind switch
            {
                RatePeriodKind.LowSeason => (QuoteLabels.RentalLowSeason, Array.Empty<string>()),
                RatePeriodKind.MidSeason => (QuoteLabels.RentalMidSeason, []),
                RatePeriodKind.HighSeason => (QuoteLabels.RentalHighSeason, []),
                RatePeriodKind.Event => (QuoteLabels.RentalEvent, [plan.FindEvent(period.EventId!.Value)!.Name]),
                _ => (QuoteLabels.RentalBase, []),
            };
            var quantity = group.Count();
            yield return new DraftLine(PriceLineKind.Rental, QuoteLineCodes.Rental, labelKey, arguments, quantity, price, price * quantity);
        }
    }

    private static DraftLine? DurationDiscount(RatePlan plan, Guid categoryId, int days, Money rentalSubtotal)
    {
        if (plan.FindTier(days) is not { } tier)
        {
            return null;
        }

        var percent = plan.TierDiscountPercent(tier, categoryId);
        if (percent <= 0m)
        {
            return null;
        }

        var discount = -Money.Tnd(rentalSubtotal.Amount * percent / 100m);
        var (labelKey, arguments) = plan.NextTierStart(tier) is { } next
            ? (QuoteLabels.DurationDiscount, new[] { Invariant(tier.MinimumDays), Invariant(next - 1), Invariant(percent) })
            : (QuoteLabels.DurationDiscountOpen, [Invariant(tier.MinimumDays), Invariant(percent)]);
        return new DraftLine(PriceLineKind.DurationDiscount, QuoteLineCodes.DurationDiscount, labelKey, arguments, 1, discount, discount);
    }

    /// <summary>Chosen extras by normalized code with their total quantity, checked against the catalogue.</summary>
    private static Result<IReadOnlyDictionary<string, int>> MergeSelections(RatePlan plan, IReadOnlyList<ExtraSelection>? selections)
    {
        var quantities = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var selection in selections ?? [])
        {
            ArgumentNullException.ThrowIfNull(selection);
            if (selection.Quantity <= 0)
            {
                return PricingErrors.ExtraQuantityInvalid;
            }

            var code = Extra.NormalizeCode(selection.Code);
            quantities[code] = quantities.GetValueOrDefault(code) + selection.Quantity;
        }

        foreach (var (code, quantity) in quantities)
        {
            var extra = plan.Extras.FirstOrDefault(e => e.IsActive && e.Code == code);
            if (extra is null)
            {
                return PricingErrors.ExtraUnknown;
            }

            if (!extra.IsSelectable)
            {
                return PricingErrors.ExtraNotSelectable;
            }

            if (quantity > MaximumExtraQuantity || (extra.Kind == ExtraKind.FranchiseWaiver && quantity != 1))
            {
                return PricingErrors.ExtraQuantityInvalid;
            }
        }

        return quantities;
    }

    private static Result<IReadOnlyList<int>> DriverAges(IReadOnlyList<DateOnly>? birthDates, DateOnly pickupDate)
    {
        var dates = birthDates ?? [];
        if (dates.Count > MaximumDrivers || dates.Any(d => d > pickupDate || d.Year < 1900))
        {
            return PricingErrors.DriversInvalid;
        }

        return dates.Select(birthDate => AgeAt(birthDate, pickupDate)).ToList();
    }

    private static int AgeAt(DateOnly birthDate, DateOnly date)
    {
        var age = date.Year - birthDate.Year;
        return date < birthDate.AddYears(age) ? age - 1 : age;
    }

    /// <summary>Lines of the catalogue extras in catalogue order: chosen ones with their quantity, automatic ones when triggered.</summary>
    private static IEnumerable<DraftLine> ExtraLines(
        RatePlan plan,
        PricingRequest request,
        PricingParameters parameters,
        IReadOnlyDictionary<string, int> selections,
        int youngDrivers,
        int days)
    {
        var period = request.RentalPeriod;
        var afterHours = (IsAfterHours(period.Start, parameters) ? 1 : 0) + (IsAfterHours(period.End, parameters) ? 1 : 0);
        foreach (var extra in plan.Extras.Where(e => e.IsActive))
        {
            var units = extra.Kind switch
            {
                ExtraKind.Optional or ExtraKind.FranchiseWaiver => selections.GetValueOrDefault(extra.Code),
                ExtraKind.YoungDriver => youngDrivers,
                ExtraKind.LocationDelivery =>
                    (request.PickupLocationId == extra.LocationId ? 1 : 0) + (request.ReturnLocationId == extra.LocationId ? 1 : 0),
                ExtraKind.AfterHours => afterHours,
                ExtraKind.OneWay => request.PickupLocationId != request.ReturnLocationId ? 1 : 0,
                _ => 0,
            };
            if (units > 0)
            {
                yield return ExtraLine(extra, units, days);
            }
        }
    }

    private static DraftLine ExtraLine(Extra extra, int units, int days)
    {
        var kind = extra.Kind switch
        {
            ExtraKind.FranchiseWaiver => PriceLineKind.FranchiseWaiver,
            ExtraKind.YoungDriver => PriceLineKind.YoungDriver,
            ExtraKind.LocationDelivery => PriceLineKind.Delivery,
            ExtraKind.AfterHours => PriceLineKind.AfterHours,
            ExtraKind.OneWay => PriceLineKind.OneWay,
            _ => PriceLineKind.Extra,
        };
        string[] names = [extra.Name, extra.NameEn ?? extra.Name];
        if (extra.Mode == ExtraPricingMode.PerRental)
        {
            return new DraftLine(kind, extra.Code, QuoteLabels.Extra, names, units, extra.Price, extra.Price * units);
        }

        if (extra.CapPerUnit is { } cap && extra.Price * days > cap)
        {
            return new DraftLine(kind, extra.Code, QuoteLabels.ExtraCapped, [.. names, Invariant(cap)], units, cap, cap * units);
        }

        var quantity = units * days;
        return new DraftLine(kind, extra.Code, QuoteLabels.Extra, names, quantity, extra.Price, extra.Price * quantity);
    }

    /// <summary>Before the opening or after the closing time of the agency, in Tunis (both bounds are opening hours).</summary>
    private static bool IsAfterHours(DateTimeOffset instant, PricingParameters parameters)
    {
        var time = TimeOnly.FromDateTime(TunisTime.ToTunis(instant).DateTime);
        return time < parameters.OpensAt || time > parameters.ClosesAt;
    }

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(decimal percent) => percent.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Invariant(Money amount) => amount.Amount.ToString("F3", CultureInfo.InvariantCulture);

    /// <summary>A line before the VAT split: amounts VAT included.</summary>
    internal sealed record DraftLine(
        PriceLineKind Kind,
        string Code,
        string LabelKey,
        IReadOnlyList<string> LabelArguments,
        int Quantity,
        Money UnitPrice,
        Money InclVat);

    /// <summary>Promotion selection and application.</summary>
    private static class Promotions
    {
        public static (IReadOnlyList<DraftLine> Lines, IReadOnlyList<AppliedPromotion> Applied, PromoCodeStatus CodeStatus) Apply(
            RatePlan plan,
            PricingRequest request,
            int days,
            Money subtotal)
        {
            var code = string.IsNullOrWhiteSpace(request.PromoCode) ? null : Promotion.NormalizeCode(request.PromoCode);
            var codeStatus = code is null ? PromoCodeStatus.None : PromoCodeStatus.Unknown;
            Promotion? codePromotion = null;
            var bookingDate = TunisTime.DateOf(request.QuotedAt);
            var pickupDate = TunisTime.DateOf(request.RentalPeriod.Start);

            var candidates = new List<(Promotion Promotion, Money Amount)>();
            foreach (var promotion in plan.Promotions.Where(p => p.IsActive))
            {
                if (promotion.Kind == PromotionKind.Code)
                {
                    if (code is null || promotion.Code != code)
                    {
                        continue;
                    }

                    if (!PassesFilters(promotion, request, bookingDate, pickupDate))
                    {
                        codeStatus = PromoCodeStatus.NotApplicable;
                        continue;
                    }

                    codePromotion = promotion;
                }
                else if (!IsTriggered(promotion, days, bookingDate, pickupDate) || !PassesFilters(promotion, request, bookingDate, pickupDate))
                {
                    continue;
                }

                var amount = promotion.DiscountType == PromotionDiscountType.Percentage
                    ? Money.Tnd(subtotal.Amount * promotion.DiscountPercent!.Value / 100m)
                    : promotion.DiscountAmount!.Value;
                candidates.Add((promotion, amount));
            }

            // The best non-combinable promotion (the first of the catalogue on a tie), then every combinable one.
            (Promotion Promotion, Money Amount)? best = null;
            foreach (var candidate in candidates.Where(c => !c.Promotion.IsCombinable))
            {
                if (best is null || candidate.Amount > best.Value.Amount)
                {
                    best = candidate;
                }
            }

            var retained = candidates.Where(c => c.Promotion.IsCombinable).ToList();
            if (best is { } kept)
            {
                retained.Insert(0, kept);
            }

            var lines = new List<DraftLine>();
            var applied = new List<AppliedPromotion>();
            var remaining = subtotal;
            foreach (var (promotion, amount) in retained)
            {
                var granted = amount < remaining ? amount : remaining;
                if (!granted.IsPositive)
                {
                    continue;
                }

                remaining -= granted;
                applied.Add(new AppliedPromotion(promotion.Id, promotion.Kind, promotion.Code, promotion.Name, granted, promotion.IsCombinable));
                var (labelKey, arguments) = Label(promotion);
                lines.Add(new DraftLine(PriceLineKind.Promotion, QuoteLineCodes.Promotion, labelKey, arguments, 1, -granted, -granted));
            }

            if (codePromotion is not null)
            {
                codeStatus = applied.Exists(a => a.PromotionId == codePromotion.Id) ? PromoCodeStatus.Applied : PromoCodeStatus.NotRetained;
            }

            return (lines, applied, codeStatus);
        }

        private static bool IsTriggered(Promotion promotion, int days, DateOnly bookingDate, DateOnly pickupDate) => promotion.Kind switch
        {
            PromotionKind.EarlyBooking => pickupDate.DayNumber - bookingDate.DayNumber >= promotion.MinimumDays,
            PromotionKind.LongStay => days >= promotion.MinimumDays,
            _ => false,
        };

        private static bool PassesFilters(Promotion promotion, PricingRequest request, DateOnly bookingDate, DateOnly pickupDate) =>
            (promotion.BookableFrom is not { } bookableFrom || bookingDate >= bookableFrom)
            && (promotion.BookableUntil is not { } bookableUntil || bookingDate <= bookableUntil)
            && (promotion.PickupFrom is not { } pickupFrom || pickupDate >= pickupFrom)
            && (promotion.PickupUntil is not { } pickupUntil || pickupDate <= pickupUntil)
            && (promotion.CategoryIds.Count == 0 || promotion.CategoryIds.Contains(request.CategoryId))
            && (promotion.Channels.Count == 0 || promotion.Channels.Contains(request.Channel));

        private static (string LabelKey, IReadOnlyList<string> Arguments) Label(Promotion promotion)
        {
            var isPercentage = promotion.DiscountType == PromotionDiscountType.Percentage;
            var percent = isPercentage ? Invariant(promotion.DiscountPercent!.Value) : null;
            return promotion.Kind switch
            {
                PromotionKind.Code => isPercentage
                    ? (QuoteLabels.PromotionCodePercent, [promotion.Code!, percent!])
                    : (QuoteLabels.PromotionCodeAmount, [promotion.Code!]),
                PromotionKind.EarlyBooking => isPercentage
                    ? (QuoteLabels.PromotionEarlyBookingPercent, [Invariant(promotion.MinimumDays!.Value), percent!])
                    : (QuoteLabels.PromotionEarlyBookingAmount, [Invariant(promotion.MinimumDays!.Value)]),
                _ => isPercentage
                    ? (QuoteLabels.PromotionLongStayPercent, [Invariant(promotion.MinimumDays!.Value), percent!])
                    : (QuoteLabels.PromotionLongStayAmount, [Invariant(promotion.MinimumDays!.Value)]),
            };
        }
    }

    /// <summary>VAT: TTC lines split per rate with group rounding; the daily contribution is entered excluding VAT.</summary>
    private static class Taxes
    {
        /// <summary>
        /// HT of the group = round(TTC of the group / (1 + rate)), spread over the lines (each line rounded, the rounding
        /// remainder on the largest line) so that the line HT add up to the group HT; VAT = TTC − HT.
        /// </summary>
        public static List<PriceLine> Split(List<DraftLine> lines, decimal vatRate)
        {
            var divisor = 1m + vatRate;
            var exclVat = lines.Select(l => Money.Tnd(l.InclVat.Amount / divisor)).ToArray();
            var groupExclVat = Money.Tnd(lines.Select(l => l.InclVat).Sum().Amount / divisor);
            var remainder = groupExclVat - exclVat.Sum();
            if (!remainder.IsZero)
            {
                var largest = 0;
                for (var i = 1; i < lines.Count; i++)
                {
                    if (Math.Abs(lines[i].InclVat.Amount) > Math.Abs(lines[largest].InclVat.Amount))
                    {
                        largest = i;
                    }
                }

                exclVat[largest] += remainder;
            }

            return
            [
                .. lines.Select((line, i) => new PriceLine(
                    line.Kind,
                    line.Code,
                    line.LabelKey,
                    line.LabelArguments,
                    line.Quantity,
                    line.UnitPrice,
                    exclVat[i],
                    line.InclVat - exclVat[i],
                    line.InclVat,
                    vatRate)),
            ];
        }

        /// <summary>Days × contribution HT, VAT = round(HT × rate), TTC = HT + VAT; no line when the contribution is zero.</summary>
        public static PriceLine? DailyContribution(PricingParameters parameters, int days)
        {
            var exclVat = parameters.DailyContributionExclVat * days;
            if (!exclVat.IsPositive)
            {
                return null;
            }

            var vat = exclVat * parameters.VatRate;
            return new PriceLine(
                PriceLineKind.DailyContribution,
                QuoteLineCodes.DailyContribution,
                QuoteLabels.DailyContribution,
                [Invariant(parameters.DailyContributionExclVat)],
                days,
                parameters.DailyContributionExclVat * (1m + parameters.VatRate),
                exclVat,
                vat,
                exclVat + vat,
                parameters.VatRate);
        }
    }
}

/// <summary>Charged rental days: 24 h units from the pick-up, plus one day when the remainder exceeds the tolerance.</summary>
public static class RentalDays
{
    /// <summary><c>max(1, ⌊D / 24 h⌋ + (D mod 24 h &gt; tolerance ? 1 : 0))</c> with <c>D = return − pick-up</c>.</summary>
    public static int Count(Period rentalPeriod, TimeSpan lateReturnTolerance)
    {
        ArgumentNullException.ThrowIfNull(rentalPeriod);
        var duration = rentalPeriod.Duration;
        var wholeDays = duration.Ticks / TimeSpan.TicksPerDay;
        var remainder = TimeSpan.FromTicks(duration.Ticks % TimeSpan.TicksPerDay);
        var days = wholeDays + (remainder > lateReturnTolerance ? 1 : 0);
        return (int)Math.Clamp(days, 1, int.MaxValue);
    }
}
