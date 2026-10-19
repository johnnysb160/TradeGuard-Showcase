using TradeGuard.Domain;

namespace TradeGuard.Tests;

public class PortfolioRiskCoordinatorTests
{
    private static readonly DateOnly Day = new(2026, 9, 28);
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 13, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Floating_pnl_is_aggregated_across_wdo_and_win()
    {
        var p = new PortfolioRiskCoordinator();
        p.StartDay("WDO", Day);
        p.StartDay("WIN", Day);
        Assert.Equal(-700m, p.UpdatePosition("WDO", Day, -700m, true));
        Assert.Equal(-1400m, p.UpdatePosition("WIN", Day, -700m, true));
        Assert.Equal(-700m, p.UpdatePosition("WDO", Day, 0, false));
    }

    [Fact]
    public void Lock_is_requested_once_and_carries_the_origin()
    {
        var p = new PortfolioRiskCoordinator();
        var events = new List<(LockReason Reason, string Origin)>();
        p.LockRequested += (_, reason, _, origin) => events.Add((reason, origin));

        Assert.True(p.TryRequestLock(Day, LockReason.DailyLoss, Now, "WIN"));
        Assert.False(p.TryRequestLock(Day, LockReason.Manual, Now, "WDO"));
        Assert.Equal([(LockReason.DailyLoss, "WIN")], events);
    }

    [Fact]
    public void Both_channels_and_recent_quotes_are_required()
    {
        var p = new PortfolioRiskCoordinator();
        p.StartDay("WDO", Day);
        p.StartDay("WIN", Day);
        p.UpdateHealth("WDO", Day, true, true, Now);
        Assert.False(p.Health(Day, Now, TimeSpan.FromSeconds(10)).Healthy);
        p.UpdateHealth("WIN", Day, true, true, Now);
        Assert.True(p.Health(Day, Now, TimeSpan.FromSeconds(10)).Healthy);
        Assert.False(p.Health(Day, Now.AddSeconds(11), TimeSpan.FromSeconds(10)).Healthy);
    }

    [Fact]
    public void History_is_ready_only_after_both_daily_exports()
    {
        var p = new PortfolioRiskCoordinator();
        p.BeginHistoryExport(Day);
        Assert.False(p.HistoryStatus(Day).Ready);

        p.MarkHistoryExported(Day, "WDO");
        Assert.False(p.HistoryStatus(Day).Ready);

        p.MarkHistoryExported(Day, "WIN");
        var complete = p.HistoryStatus(Day);
        Assert.True(complete.Ready);
        Assert.Equal(1, complete.Revision);

        p.BeginHistoryExport(Day.AddDays(1));
        Assert.False(p.HistoryStatus(Day.AddDays(1)).Ready);
    }
}
