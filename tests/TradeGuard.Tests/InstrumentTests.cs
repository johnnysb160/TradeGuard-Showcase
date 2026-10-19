using TradeGuard.Domain;

namespace TradeGuard.Tests;

public class InstrumentTests
{
    [Fact]
    public void Estimated_round_trip_cost_uses_the_research_cost_per_contract()
    {
        var wdo = new InstrumentSpec("WDOX26", 0.5m, 5m, null);
        var win = new InstrumentSpec("WINV26", 5m, 1m, null);
        var other = new InstrumentSpec("OTHER", 1m, 1m, null);

        Assert.Equal(20m, wdo.EstimatedRoundTripCost(4));
        Assert.Equal(4m, win.EstimatedRoundTripCost(2));
        Assert.Equal(0m, other.EstimatedRoundTripCost(3));
    }
}
