namespace FileReport.Application.Measurements;

public sealed record CostRate(string Component, string Unit, decimal PricePerUnit, decimal IncludedUnits,
    decimal MinimumCharge, decimal AllocationFraction, string SourceUrl, DateOnly EffectiveDate);
public sealed record RateCard(string Version, string Currency, string Provider, string Region, CostRate[] Rates);
public sealed record Usage(string Component, string Unit, decimal? Quantity);
public sealed record CostComponent(string Component, decimal? Amount, string Availability);
public sealed record CostEstimate(string Status, string? Currency, string? RateCardVersion,
    decimal? Total, decimal? PartialSubtotal, decimal? CostPerUniqueGb, CostComponent[] Components);

public static class CostEstimator
{
    public static readonly string[] RequiredComponents =
        ["compute", "database", "broker", "sourceStorage", "reportStorage", "scratch", "network", "observability", "email"];
    public static CostEstimate Estimate(RateCard? card, IReadOnlyList<Usage> usage, long uniqueInputBytes)
    {
        if (uniqueInputBytes < 0) throw new ArgumentOutOfRangeException(nameof(uniqueInputBytes));
        if (card is null) return new("Unavailable", null, null, null, null, null,
            RequiredComponents.Select(c => new CostComponent(c, null, "No rate card")).ToArray());
        if (string.IsNullOrWhiteSpace(card.Version) || card.Currency.Length != 3 || string.IsNullOrWhiteSpace(card.Provider) || string.IsNullOrWhiteSpace(card.Region)
            || card.Rates.GroupBy(r => r.Component).Any(g => g.Count() != 1))
            throw new ArgumentException("Provide a versioned rate card with currency, provider, region, and unique components.");
        var components = RequiredComponents.Select(component =>
        {
            var rate = card.Rates.SingleOrDefault(r => r.Component == component);
            var measured = usage.Where(u => u.Component == component).ToArray();
            if (rate is null || measured.Length == 0 || measured.Any(u => u.Quantity is null))
                return new CostComponent(component, null, "Usage or rate unavailable");
            if (rate.PricePerUnit < 0 || rate.IncludedUnits < 0 || rate.MinimumCharge < 0 || rate.AllocationFraction is < 0 or > 1 ||
                !Uri.TryCreate(rate.SourceUrl, UriKind.Absolute, out var source) || source.Scheme != "https" ||
                measured.Any(u => u.Unit != rate.Unit || u.Quantity < 0))
                throw new ArgumentException("Cost units, rates, provenance, and allocation must be valid.");
            var billable = Math.Max(0, measured.Sum(u => u.Quantity!.Value) - rate.IncludedUnits);
            var amount = Math.Max(rate.MinimumCharge, billable * rate.PricePerUnit) * rate.AllocationFraction;
            return new CostComponent(component, amount, "Estimated; not a provider bill");
        }).ToArray();
        var complete = components.All(c => c.Amount != null);
        var any = components.Any(c => c.Amount != null);
        decimal? subtotal = any ? components.Sum(c => c.Amount ?? 0) : null;
        var total = complete ? subtotal : null;
        return new(complete ? "Estimated" : any ? "Partial" : "Unavailable", card.Currency, card.Version,
            total, complete ? null : subtotal, total.HasValue && uniqueInputBytes > 0 ? total / (uniqueInputBytes / 1_000_000_000m) : null, components);
    }
}
