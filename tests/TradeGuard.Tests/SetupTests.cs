using TradeGuard.Domain;

namespace TradeGuard.Tests;

public class IndicatorTests
{
    private static List<Candle> Closes(params decimal[] closes) =>
        closes.Select((c, i) => new Candle(DateTimeOffset.UnixEpoch.AddMinutes(5 * i), c, c, c, c)).ToList();

    [Fact]
    public void Sma_is_the_plain_average()
    {
        var sma = Indicators.Sma(Closes(1, 2, 3, 4, 5), 3);
        Assert.Equal(0m, sma[1]);
        Assert.Equal(2m, sma[2]);
        Assert.Equal(4m, sma[4]);
    }

    [Fact]
    public void Ema_reacts_faster_than_sma()
    {
        var bars = Closes([.. Enumerable.Repeat(10m, 20), 20m]);
        Assert.True(Indicators.Ema(bars, 9)[^1] > Indicators.Sma(bars, 9)[^1]);
    }
}

public class SetupDetectionTests
{
    // 12:00 UTC = 09:00 em Brasília; candles de 5 min.
    private static readonly DateTimeOffset Start = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly SignalEngine.Options Opts = new(SetupNames.All, 10m, 8m, 0.5m, TimeSpan.FromMinutes(5));

    private static Candle C(int i, decimal o, decimal h, decimal l, decimal c) => new(Start.AddMinutes(5 * i), o, h, l, c);

    /// <summary>Alta firme e depois uma correção curta: MM 21 subindo, MME 9 caindo por 3 candles.</summary>
    private static List<Candle> UptrendWithDip()
    {
        var bars = new List<Candle>();
        decimal p = 5000;
        for (var i = 0; i < 40; i++) { bars.Add(C(i, p, p + 2, p - 1, p + 1.5m)); p += 1.5m; }
        for (var i = 40; i < 43; i++) { bars.Add(C(i, p, p + 0.5m, p - 2.5m, p - 2m)); p -= 2m; }
        return bars;
    }

    [Fact]
    public void Nine_one_arms_a_buy_when_ema9_turns_up_in_an_uptrend()
    {
        var bars = UptrendWithDip();
        var p = bars[^1].Close;
        bars.Add(C(43, p, p + 4, p - 0.5m, p + 3.5m)); // candle de virada

        var engine = new SignalEngine();
        var armed = engine.OnBarClosed(bars, bars[^1].OpenUtc.AddMinutes(5), Opts);

        var buy = Assert.Single(armed, a => a.Setup == SetupNames.NineOne);
        Assert.Equal(Side.Buy, buy.Side);
        Assert.Equal(bars[^1].High + 0.5m, buy.Trigger);
        Assert.Equal(bars[^1].Low - 0.5m, buy.TechnicalStop);
    }

    [Fact]
    public void Continuous_point_arms_when_price_touches_the_rising_ma21()
    {
        var bars = new List<Candle>();
        decimal p = 5000;
        for (var i = 0; i < 40; i++) { bars.Add(C(i, p, p + 1, p - 0.5m, p + 0.8m)); p += 0.8m; }
        var sma = Indicators.Sma(bars, 21)[^1];
        bars.Add(C(40, p, p + 0.5m, sma - 0.5m, sma + 1m)); // mínima encosta na média e fecha acima

        var armed = new SignalEngine().OnBarClosed(bars, bars[^1].OpenUtc.AddMinutes(5), Opts);
        var pc = Assert.Single(armed, a => a.Setup == SetupNames.ContinuousPoint);
        Assert.Equal(Side.Buy, pc.Side);
    }

    [Fact]
    public void Armed_setup_fires_once_when_price_breaks_the_trigger()
    {
        var bars = UptrendWithDip();
        var p = bars[^1].Close;
        bars.Add(C(43, p, p + 4, p - 0.5m, p + 3.5m));
        var engine = new SignalEngine();
        var now = bars[^1].OpenUtc.AddMinutes(5);
        var trigger = engine.OnBarClosed(bars, now, Opts).First(a => a.Setup == SetupNames.NineOne).Trigger;

        Assert.Null(engine.OnPrice(trigger - 1, trigger - 0.5m, now.AddSeconds(10), null, Opts, out _));
        var signal = engine.OnPrice(trigger, trigger + 0.5m, now.AddSeconds(20), null, Opts, out _);
        Assert.Equal(Side.Buy, signal!.Side);
        Assert.Equal(SetupNames.NineOne, signal.Setup);
        Assert.Null(engine.OnPrice(trigger, trigger + 0.5m, now.AddSeconds(30), null, Opts, out _));
    }

    [Fact]
    public void Signal_is_skipped_when_day_high_blocks_the_target()
    {
        var bars = UptrendWithDip();
        var p = bars[^1].Close;
        bars.Add(C(43, p, p + 4, p - 0.5m, p + 3.5m));
        var engine = new SignalEngine();
        var now = bars[^1].OpenUtc.AddMinutes(5);
        var trigger = engine.OnBarClosed(bars, now, Opts).First(a => a.Setup == SetupNames.NineOne).Trigger;

        var levels = new DayLevels(5000, trigger + 3, 4990, null, null, null); // máxima do dia 3 pts acima
        Assert.Null(engine.OnPrice(trigger - 0.5m, trigger, now.AddSeconds(5), levels, Opts, out var why));
        Assert.Contains("máxima do dia", why);
    }

    [Fact]
    public void Armed_setup_expires()
    {
        var bars = UptrendWithDip();
        var p = bars[^1].Close;
        bars.Add(C(43, p, p + 4, p - 0.5m, p + 3.5m));
        var engine = new SignalEngine();
        var now = bars[^1].OpenUtc.AddMinutes(5);
        var trigger = engine.OnBarClosed(bars, now, Opts).First(a => a.Setup == SetupNames.NineOne).Trigger;

        Assert.Null(engine.OnPrice(trigger, trigger + 1, now.AddMinutes(20), null, Opts, out _));
    }

    [Fact]
    public void Expired_setup_reports_how_close_price_got()
    {
        var bars = UptrendWithDip();
        var p = bars[^1].Close;
        bars.Add(C(43, p, p + 4, p - 0.5m, p + 3.5m));
        var engine = new SignalEngine();
        var now = bars[^1].OpenUtc.AddMinutes(5);
        var trigger = engine.OnBarClosed(bars, now, Opts).First(a => a.Setup == SetupNames.NineOne).Trigger;

        engine.OnPrice(trigger - 1.5m, trigger - 1m, now.AddSeconds(5), null, Opts, out _);
        engine.OnPrice(trigger - 1m, trigger - 0.5m, now.AddSeconds(9), null, Opts, out _); // chegou a 0,5 do gatilho
        engine.OnPrice(trigger - 3m, trigger - 2.5m, now.AddMinutes(20), null, Opts, out _); // já expirou

        var expired = Assert.Single(engine.TakeExpired(), e => e.Candidate.Setup == SetupNames.NineOne);
        Assert.Equal(trigger - 0.5m, expired.Closest);
        Assert.Empty(engine.TakeExpired());
    }

    [Fact]
    public void Rebuild_rearms_valid_setups_but_never_a_trigger_already_passed()
    {
        var bars = UptrendWithDip();
        var p = bars[^1].Close;
        bars.Add(C(43, p, p + 4, p - 0.5m, p + 3.5m));
        var now = bars[^1].OpenUtc.AddMinutes(6);
        var trigger = bars[^1].High + 0.5m;

        var reopened = new SignalEngine();
        reopened.Rebuild(bars, now, Opts, trigger - 2, trigger - 1.5m);
        Assert.Contains(reopened.Armed, a => a.Setup == SetupNames.NineOne && a.Trigger == trigger);

        var late = new SignalEngine();
        late.Rebuild(bars, now, Opts, trigger + 1, trigger + 1.5m); // já rompeu enquanto estava fechado
        Assert.DoesNotContain(late.Armed, a => a.Setup == SetupNames.NineOne);
    }

    [Fact]
    public void Disabled_setup_is_not_armed()
    {
        var bars = UptrendWithDip();
        var p = bars[^1].Close;
        bars.Add(C(43, p, p + 4, p - 0.5m, p + 3.5m));
        var only = Opts with { Enabled = [SetupNames.ContinuousPoint] };
        Assert.DoesNotContain(new SignalEngine().OnBarClosed(bars, bars[^1].OpenUtc.AddMinutes(5), only), a => a.Setup == SetupNames.NineOne);
    }

    [Fact]
    public void Day_levels_include_the_opening_range()
    {
        var bars = new List<Candle>();
        for (var i = 0; i < 12; i++) bars.Add(C(i, 5000 + i, 5002 + i, 4999 + i, 5001 + i)); // 9:00–10:00
        var levels = DayLevels.From(bars, new DateOnly(2026, 9, 25))!;
        Assert.Equal(5000m, levels.Open);
        Assert.Equal(5007m, levels.RangeHigh); // 6 candles de 9:00 a 9:25
        Assert.Equal(4999m, levels.RangeLow);
    }

    [Fact]
    public void Setup_validation_needs_30_positive_trades()
    {
        TradeRecord T(decimal pnl) => new() { PnlMoney = pnl, InitialRiskMoney = 50, OpenedAtUtc = Start, ClosedAtUtc = Start };
        Assert.False(TradeStatistics.From(Enumerable.Range(0, 10).Select(_ => T(100))).IsValidated);
        var thirty = Enumerable.Range(0, 30).Select(i => T(i % 3 == 0 ? -50 : 100));
        Assert.True(TradeStatistics.From(thirty).IsValidated);
    }

    [Fact]
    public void Time_rule_trade_tag_roundtrips_after_restart()
    {
        foreach (var rule in SetupNames.TimeRules)
            Assert.Equal(rule.Name, SetupNames.SetupFromTradeTag(SetupNames.TradeTag(rule.Name)));
        Assert.Null(SetupNames.SetupFromTradeTag("TradeGuard"));
    }
}
