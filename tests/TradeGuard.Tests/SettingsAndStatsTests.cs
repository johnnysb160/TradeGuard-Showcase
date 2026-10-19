using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradeGuard.Domain;
using TradeGuard.Infrastructure;

namespace TradeGuard.Tests;

public class SettingsTests
{
    private static readonly InstrumentSpec Wdo = new("WDOV26", 0.5m, 5m, null);

    [Fact]
    public void Tightening_is_not_loosening()
    {
        var tighter = RiskSettings.Default with { MaxDailyLoss = 100, MaxTradesPerDay = 2, StopPoints = 4 };
        Assert.Empty(RiskSettings.Default.LoosenedBy(tighter));
    }

    [Fact]
    public void Loosening_is_detected()
    {
        var looser = RiskSettings.Default with { MaxDailyLoss = 500, AllowRealAccount = true, NewsFilterEnabled = false };
        var loosened = RiskSettings.Default.LoosenedBy(looser);
        Assert.Contains("Perda diária", loosened);
        Assert.Contains("Conta real", loosened);
        Assert.Contains("Filtro de notícias", loosened);
    }

    [Fact]
    public void Turning_on_automatic_mode_is_loosening_and_back_is_not()
    {
        Assert.Contains("Modo automático", RiskSettings.Default.LoosenedBy(RiskSettings.Default with { Mode = TradingMode.Auto }));
        Assert.Empty(RiskSettings.Default.LoosenedBy(RiskSettings.Default with { Mode = TradingMode.SemiAuto })); // semi: você confirma cada entrada
        var auto = RiskSettings.Default with { Mode = TradingMode.Auto };
        Assert.Empty(auto.LoosenedBy(RiskSettings.Default));
    }

    [Fact]
    public void Disabling_a_limit_counts_as_loosening() =>
        Assert.Contains("Perda semanal", RiskSettings.Default.LoosenedBy(RiskSettings.Default with { MaxWeeklyLoss = 0 }));

    [Theory]
    [InlineData(0, 1, 1)]      // contratos fixos
    [InlineData(100, 5, 2)]    // R$100 ÷ (5 pts × R$10) = 2
    [InlineData(100, 1, 1)]    // limitado ao máximo
    [InlineData(40, 5, 0)]     // não paga 1 contrato
    public void Contracts_by_risk(decimal risk, int max, int expected)
    {
        var s = RiskSettings.Default with { RiskPerTradeMoney = risk, MaxContracts = max };
        Assert.Equal(expected, s.ContractsFor(Wdo));
    }

    [Fact]
    public void Presets_are_valid()
    {
        Assert.Empty(RiskSettings.Beginner.Validate());
        Assert.Empty(RiskSettings.Conservative.Validate());
        Assert.Empty(RiskSettings.TwoContracts.Validate());
    }

    [Fact]
    public void Flatten_time_must_follow_last_entry_and_end_before_the_close()
    {
        var late = RiskSettings.Default with { WindowEnd = new(17, 40), FlattenAt = new(17, 55) };
        Assert.Contains(late.Validate(), e => e.Contains("17:50"));
        var inverted = RiskSettings.Default with { WindowEnd = new(17, 40), FlattenAt = new(17, 35) };
        Assert.Contains(inverted.Validate(), e => e.Contains("última entrada"));
        var ok = RiskSettings.Default with { WindowEnd = new(17, 40), FlattenAt = new(17, 50) };
        Assert.Empty(ok.Validate());
        Assert.Equal(new TimeOnly(17, 54), ok.EmergencyFlatten); // limitada a antes do fim do pregão
    }

    [Fact]
    public void Stop_is_mandatory() =>
        Assert.NotEmpty((RiskSettings.Default with { StopPoints = 0 }).Validate());
}

public class SettingsStoreTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly SettingsStore _store;

    public SettingsStoreTests()
    {
        _conn.Open();
        var options = new DbContextOptionsBuilder<TradeGuardDb>().UseSqlite(_conn).Options;
        var factory = new TestFactory(options);
        using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();
        _store = new SettingsStore(factory);
    }

    public void Dispose() => _conn.Dispose();

    [Fact]
    public async Task Tightening_applies_today_loosening_next_weekday()
    {
        var friday = new DateOnly(2026, 9, 25);

        var tight = await _store.SaveAsync(RiskSettings.Default with { MaxDailyLoss = 100 }, friday);
        Assert.True(tight.AppliedNow);
        Assert.Equal(100, (await _store.EffectiveAsync(friday)).MaxDailyLoss);

        var loose = await _store.SaveAsync(RiskSettings.Default with { MaxDailyLoss = 300 }, friday);
        Assert.False(loose.AppliedNow);
        Assert.Equal(new DateOnly(2026, 9, 28), loose.EffectiveFrom); // segunda
        Assert.Equal(100, (await _store.EffectiveAsync(friday)).MaxDailyLoss);
        Assert.Equal(300, (await _store.EffectiveAsync(new DateOnly(2026, 9, 28))).MaxDailyLoss);
    }

    [Fact]
    public async Task Mixed_save_applies_the_tightening_now_and_schedules_only_the_loosening()
    {
        var thursday = new DateOnly(2026, 9, 24);
        var mixed = RiskSettings.Default with { MaxTradesPerDay = 5, MaxDailyLoss = 100, WindowStart = new(9, 5) };

        var r = await _store.SaveAsync(mixed, thursday);
        Assert.False(r.AppliedNow);
        Assert.Equal(["Trades por dia", "Começar às"], r.Loosened);

        var today = await _store.EffectiveAsync(thursday);
        Assert.Equal(100, today.MaxDailyLoss);               // apertou: vale já
        Assert.Equal(4, today.MaxTradesPerDay);              // afrouxou: ainda não
        Assert.Equal(new TimeOnly(9, 15), today.WindowStart);

        var pending = await _store.PendingAsync(thursday);
        Assert.Equal(new DateOnly(2026, 9, 25), pending!.EffectiveFrom);
        Assert.Equal(5, (await _store.DesiredAsync(thursday)).MaxTradesPerDay); // a tela mostra o que você salvou

        var friday = await _store.EffectiveAsync(new DateOnly(2026, 9, 25));
        Assert.Equal(5, friday.MaxTradesPerDay);
        Assert.Equal(new TimeOnly(9, 5), friday.WindowStart);
        Assert.Equal(100, friday.MaxDailyLoss);
        Assert.Null(await _store.PendingAsync(new DateOnly(2026, 9, 25)));
    }

    [Fact]
    public async Task Saving_again_keeps_the_schedule_and_a_later_tightening_survives_tomorrow()
    {
        var thursday = new DateOnly(2026, 9, 24);
        await _store.SaveAsync(RiskSettings.Default with { MaxTradesPerDay = 5 }, thursday);
        // Reabre a tela (vê o agendado) e aperta outra coisa.
        var desired = await _store.DesiredAsync(thursday);
        await _store.SaveAsync(desired with { StopPoints = 4 }, thursday);

        Assert.Equal(4, (await _store.EffectiveAsync(thursday)).StopPoints);
        Assert.Equal(4, (await _store.EffectiveAsync(thursday)).MaxTradesPerDay);
        var friday = await _store.EffectiveAsync(new DateOnly(2026, 9, 25));
        Assert.Equal(4, friday.StopPoints);       // antes o agendado antigo apagava esse aperto
        Assert.Equal(5, friday.MaxTradesPerDay);
    }

    [Fact]
    public async Task Undoing_a_scheduled_loosening_cancels_it()
    {
        var thursday = new DateOnly(2026, 9, 24);
        await _store.SaveAsync(RiskSettings.Default with { MaxTradesPerDay = 5 }, thursday);
        await _store.SaveAsync(RiskSettings.Default, thursday);
        Assert.Null(await _store.PendingAsync(thursday));
        Assert.Equal(4, (await _store.EffectiveAsync(new DateOnly(2026, 9, 25))).MaxTradesPerDay);
    }

    [Fact]
    public async Task Invalid_settings_are_rejected()
    {
        var r = await _store.SaveAsync(RiskSettings.Default with { StopPoints = 0 }, new DateOnly(2026, 9, 25));
        Assert.NotEmpty(r.Errors);
    }

    [Fact]
    public async Task Settings_roundtrip_keeps_lists()
    {
        var day = new DateOnly(2026, 9, 25);
        await _store.SaveAsync(RiskSettings.TwoContracts with { MaxDailyLoss = 100, MaxWeeklyLoss = 300, MaxMonthlyLoss = 800 }, day);
        var loaded = await _store.EffectiveAsync(day.AddDays(3));
        Assert.Single(loaded.Partials);
        Assert.Single(loaded.ProfitLocks);
    }

    private sealed class TestFactory(DbContextOptions<TradeGuardDb> options) : IDbContextFactory<TradeGuardDb>
    {
        public TradeGuardDb CreateDbContext() => new(options);
    }
}

public class StatisticsTests
{
    private static TradeRecord T(decimal pnl, decimal risk = 50) => new()
    {
        PnlMoney = pnl, InitialRiskMoney = risk, OpenedAtUtc = DateTimeOffset.UtcNow, ClosedAtUtc = DateTimeOffset.UtcNow
    };

    [Fact]
    public void Computes_core_metrics()
    {
        var s = TradeStatistics.From([T(100), T(-50), T(100), T(-50)]);
        Assert.Equal(50m, s.WinRate);
        Assert.Equal(2m, s.Payoff);
        Assert.Equal(2m, s.ProfitFactor);
        Assert.Equal(25m, s.Expectancy);
        Assert.Equal(0.5m, s.ExpectancyR);
        Assert.Equal(50m, s.MaxDrawdown);
        Assert.Contains("insuficiente", s.Verdict);
    }

    [Fact]
    public void Empty_journal_is_safe() => Assert.Equal(0, TradeStatistics.From([]).Count);

    [Fact]
    public void Excluded_trades_leave_statistics()
    {
        var absurd = T(4440);
        absurd.Excluded = true;
        var s = TradeStatistics.From([T(100), T(-50), absurd]);
        Assert.Equal(2, s.Count);
        Assert.Equal(50m, s.NetPnl);
    }

    [Fact]
    public void Excluding_never_erases_a_loss_from_the_limits()
    {
        var win = T(4440); win.Excluded = true;
        var loss = T(-120); loss.Excluded = true;
        Assert.False(win.CountsForLimits);  // lucro desconsiderado sai dos limites
        Assert.True(loss.CountsForLimits);  // prejuízo continua valendo nas travas
    }
}
