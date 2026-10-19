using TradeGuard.Domain;

namespace TradeGuard.Tests;

public class RiskGuardTests
{
    private static readonly InstrumentSpec Wdo = new("WDOV26", 0.5m, 5m, new DateTimeOffset(2026, 9, 30, 21, 0, 0, TimeSpan.Zero));

    private static DateTimeOffset B3(int h, int m) => new(2026, 9, 24, h, m, 0, TimeSpan.FromHours(-3));

    private static GuardContext Ctx(
        DateTimeOffset? now = null, RiskSettings? s = null, DayLedger? day = null, bool demo = true,
        IReadOnlyList<NewsEvent>? news = null, decimal? spread = 0.5m, bool checklist = true, decimal week = 0) =>
        new(now ?? B3(10, 0), s ?? RiskSettings.Default, day ?? new DayLedger(new DateOnly(2026, 9, 24)),
            week, 0, news ?? [], spread, true, demo, false, Wdo, checklist);

    [Fact]
    public void Clean_context_allows_entry() => Assert.Empty(RiskGuard.EntryBlocks(Ctx()));

    [Theory]
    [InlineData(9, 0, BlockReason.OutsideWindow)]
    [InlineData(17, 35, BlockReason.OutsideWindow)]
    [InlineData(17, 50, BlockReason.AfterFlatten)]
    public void Time_window_is_enforced(int h, int m, BlockReason expected) =>
        Assert.Contains(RiskGuard.EntryBlocks(Ctx(B3(h, m))), b => b.Reason == expected);

    [Fact]
    public void Real_account_blocked_by_default() =>
        Assert.Contains(RiskGuard.EntryBlocks(Ctx(demo: false)), b => b.Reason == BlockReason.RealAccountNotAllowed);

    [Fact]
    public void Real_account_stays_blocked_until_operational_evidence_is_complete()
    {
        var ctx = Ctx(demo: false, s: RiskSettings.Default with { AllowRealAccount = true });
        Assert.Contains(RiskGuard.EntryBlocks(ctx), b => b.Reason == BlockReason.RealTradingNotQualified);
        Assert.DoesNotContain(RiskGuard.EntryBlocks(ctx with { RealTradingQualified = true }),
            b => b.Reason is BlockReason.RealTradingNotQualified or BlockReason.RealAccountNotAllowed);
    }

    [Fact]
    public void Audit_storage_failure_blocks_new_entry() =>
        Assert.Contains(RiskGuard.EntryBlocks(Ctx() with { AuditStorageHealthy = false, AuditStorageMessage = "disco cheio" }),
            b => b.Reason == BlockReason.AuditStorageUnavailable && b.Message == "disco cheio");

    [Fact]
    public void Broker_offline_and_algotrading_off_block()
    {
        var ctx = Ctx() with { BrokerConnected = false, AlgoAllowed = false };
        var blocks = RiskGuard.EntryBlocks(ctx);
        Assert.Contains(blocks, b => b.Reason == BlockReason.BrokerOffline);
        Assert.Contains(blocks, b => b.Reason == BlockReason.AlgoTradingOff);
    }

    [Fact]
    public void Checklist_pending_blocks() =>
        Assert.Contains(RiskGuard.EntryBlocks(Ctx(checklist: false)), b => b.Reason == BlockReason.ChecklistPending);

    [Fact]
    public void Wide_spread_blocks() =>
        Assert.Contains(RiskGuard.EntryBlocks(Ctx(spread: 3m)), b => b.Reason == BlockReason.SpreadTooWide);

    [Fact]
    public void News_blackout_blocks_around_event()
    {
        var news = new[] { new NewsEvent(B3(10, 30).ToUniversalTime(), "USD", 3, "Payroll") };
        Assert.Contains(RiskGuard.EntryBlocks(Ctx(B3(10, 27), news: news)), b => b.Reason == BlockReason.NewsBlackout);
        Assert.DoesNotContain(RiskGuard.EntryBlocks(Ctx(B3(10, 40), news: news)), b => b.Reason == BlockReason.NewsBlackout);
    }

    [Fact]
    public void Cooldown_after_loss()
    {
        var day = new DayLedger(new DateOnly(2026, 9, 24));
        day.RegisterClosedTrade(-50, B3(10, 0));
        Assert.Contains(RiskGuard.EntryBlocks(Ctx(B3(10, 5), day: day)), b => b.Reason == BlockReason.Cooldown);
        Assert.DoesNotContain(RiskGuard.EntryBlocks(Ctx(B3(10, 11), day: day)), b => b.Reason == BlockReason.Cooldown);
    }

    [Fact]
    public void Contract_expiring_today_blocks() =>
        Assert.Contains(RiskGuard.EntryBlocks(Ctx(new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.FromHours(-3)))),
            b => b.Reason == BlockReason.ContractExpiring);

    [Fact]
    public void Daily_loss_counts_floating_pnl()
    {
        var day = new DayLedger(new DateOnly(2026, 9, 24));
        day.RegisterClosedTrade(-100, B3(9, 30));
        Assert.Equal(LockReason.None, RiskGuard.LockToApply(Ctx(day: day), -40));
        Assert.Equal(LockReason.DailyLoss, RiskGuard.LockToApply(Ctx(day: day), -50));
    }

    [Fact]
    public void Max_trades_and_consecutive_losses_lock()
    {
        var day = new DayLedger(new DateOnly(2026, 9, 24));
        day.RegisterClosedTrade(-20, B3(9, 30));
        day.RegisterClosedTrade(-20, B3(9, 50));
        Assert.Equal(LockReason.ConsecutiveLosses, RiskGuard.LockToApply(Ctx(day: day), 0));

        var day2 = new DayLedger(new DateOnly(2026, 9, 24));
        for (var i = 0; i < 4; i++) day2.RegisterClosedTrade(i % 2 == 0 ? 10 : -5, B3(9, 30 + i));
        Assert.Equal(LockReason.MaxTrades, RiskGuard.LockToApply(Ctx(day: day2), 0));
    }

    [Fact]
    public void Profit_give_back_locks_after_returning_half()
    {
        var day = new DayLedger(new DateOnly(2026, 9, 24));
        day.RegisterClosedTrade(200, B3(9, 30));
        day.RegisterClosedTrade(-60, B3(10, 0));
        Assert.Equal(LockReason.None, RiskGuard.LockToApply(Ctx(day: day), 0));
        Assert.Equal(LockReason.ProfitGiveBack, RiskGuard.LockToApply(Ctx(day: day), -40));
    }

    [Fact]
    public void Weekly_loss_locks()
    {
        Assert.Equal(LockReason.WeeklyLoss, RiskGuard.LockToApply(Ctx(week: -390), -10));
    }

    [Fact]
    public void Day_lock_is_permanent()
    {
        var day = new DayLedger(new DateOnly(2026, 9, 24));
        Assert.True(day.TryLock(LockReason.DailyLoss, B3(10, 0)));
        Assert.False(day.TryLock(LockReason.Manual, B3(11, 0)));
        Assert.Equal(LockReason.DailyLoss, day.Lock);
        Assert.Contains(RiskGuard.EntryBlocks(Ctx(day: day)), b => b.Reason == BlockReason.DayLocked);
    }
}
