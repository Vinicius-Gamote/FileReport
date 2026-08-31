using FileReport.Application.Measurements;
namespace FileReport.Application.Tests;

public sealed class CostEstimatorTests
{
    [Fact]
    public void MissingRatesNeverBecomeAFreeTotal()
    {
        var cost = CostEstimator.Estimate(null, [], 1000);
        Assert.Equal("Unavailable", cost.Status); Assert.Null(cost.Total); Assert.Null(cost.CostPerUniqueGb);
    }
    [Fact]
    public void PartialCostsAreLabeledAndUnitsAreValidated()
    {
        var card = new RateCard("test-v1", "USD", "synthetic-test-only", "fixture",
            [new("compute", "seconds", 2, 1, 0, .5m, "https://example.test/test-rate", new(2026, 8, 30))]);
        var cost = CostEstimator.Estimate(card, [new("compute", "seconds", 3)], 0);
        Assert.Equal("Partial", cost.Status); Assert.Equal(2m, cost.PartialSubtotal); Assert.Null(cost.Total);
        Assert.Throws<ArgumentException>(() => CostEstimator.Estimate(card, [new("compute", "hours", 3)], 100));
    }
    [Fact]
    public void RetriesAreBillableButThePerGbDenominatorIsUniqueInput()
    {
        var card = new RateCard("test-v1", "USD", "synthetic", "fixture", CostEstimator.RequiredComponents
            .Select(c => new CostRate(c, "unit", 1, 0, 0, 1, "https://example.test/test", new(2026, 8, 30))).ToArray());
        var usage = CostEstimator.RequiredComponents.Select(c => new Usage(c, "unit", 1)).Append(new("compute", "unit", 1)).ToArray();
        var result = CostEstimator.Estimate(card, usage, 1_000_000_000);
        Assert.Equal(10m, result.Total); Assert.Equal(10m, result.CostPerUniqueGb);
        Assert.Null(CostEstimator.Estimate(card, usage, 0).CostPerUniqueGb);
    }
}
