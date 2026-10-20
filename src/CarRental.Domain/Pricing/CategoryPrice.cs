using CarRental.Domain.Common;

namespace CarRental.Domain.Pricing;

/// <summary>
/// A cell of the price grid: the daily price, VAT included, of a vehicle category for a period (base price, season level
/// or priority event). A missing season or event cell inherits the next level (event, then season, then base price).
/// </summary>
public sealed class CategoryPrice : Entity
{
    private CategoryPrice()
    {
    }

    private CategoryPrice(Guid id, CategoryPriceDefinition definition)
        : base(id)
    {
        CategoryId = definition.CategoryId;
        PeriodKind = definition.Period.Kind;
        EventId = definition.Period.EventId;
        DailyPrice = definition.DailyPrice;
    }

    /// <summary>Vehicle category (Fleet module), referenced by id.</summary>
    public Guid CategoryId { get; private set; }

    public RatePeriodKind PeriodKind { get; private set; }

    /// <summary>The priority event of an <see cref="RatePeriodKind.Event"/> price.</summary>
    public Guid? EventId { get; private set; }

    /// <summary>Daily price, VAT included (TTC).</summary>
    public Money DailyPrice { get; private set; }

    public RatePeriodKey Period =>
        RatePeriodKey.From(PeriodKind, EventId) ?? throw new InvalidOperationException($"Inconsistent price period {PeriodKind}/{EventId}.");

    internal static CategoryPrice Create(CategoryPriceDefinition definition) => new(NewId(), definition);

    internal CategoryPriceDefinition ToDefinition() => new(CategoryId, Period, DailyPrice);

    internal void ChangePrice(Money dailyPrice) => DailyPrice = dailyPrice;
}

/// <summary>A cell of the price grid: daily price TTC of <paramref name="CategoryId"/> for <paramref name="Period"/>.</summary>
public sealed record CategoryPriceDefinition(Guid CategoryId, RatePeriodKey Period, Money DailyPrice);
