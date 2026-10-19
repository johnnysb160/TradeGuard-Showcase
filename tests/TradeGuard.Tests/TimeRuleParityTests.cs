using TradeGuard.Domain;

namespace TradeGuard.Tests;

public class TimeRuleParityTests
{
    [Fact]
    public void Matching_live_decisions_pass_every_intermediate_value()
    {
        var day = new DateOnly(2026, 9, 28);
        var candles = History(day);
        var observed = Observed(candles, day, "WDO");

        var result = TimeRuleParity.Audit(candles, day, "WDO", observed);

        Assert.True(result.Matches, string.Join(" | ", result.Issues.Select(x => x.Detail)));
        Assert.Equal(5, result.ExpectedEvaluations);
        Assert.Equal(result.ExpectedSignals, result.CompletedSignals);
    }

    [Fact]
    public void Different_adr_is_detected_even_when_side_matches()
    {
        var day = new DateOnly(2026, 9, 28);
        var candles = History(day);
        var observed = Observed(candles, day, "WDO").ToDictionary();
        var first = observed.First();
        observed[first.Key] = first.Value with { Adr = first.Value.Adr + 1 };

        var result = TimeRuleParity.Audit(candles, day, "WDO", observed);

        Assert.Contains(result.Issues, x => x.Code == "adr-mismatch" && x.Setup == first.Key);
    }

    [Fact]
    public void Missing_live_evaluation_is_detected()
    {
        var day = new DateOnly(2026, 9, 28);
        var candles = History(day);
        var observed = Observed(candles, day, "WDO").ToDictionary();
        observed.Remove(observed.Keys.First());

        var result = TimeRuleParity.Audit(candles, day, "WDO", observed);

        Assert.Contains(result.Issues, x => x.Code == "missing-live-evaluation");
    }

    [Fact]
    public void Blocked_signal_keeps_parity_but_does_not_qualify_for_real_gate()
    {
        var day = new DateOnly(2026, 9, 28);
        var candles = History(day);
        var observed = Observed(candles, day, "WDO").ToDictionary();
        var signal = observed.First(x => x.Value.Side != 0);
        observed[signal.Key] = signal.Value with
        {
            ExecutionRequested = true,
            OrderSent = false,
            PositionOpened = false,
            PositionClosed = false
        };

        var result = TimeRuleParity.Audit(candles, day, "WDO", observed);

        Assert.True(result.Matches, string.Join(" | ", result.Issues.Select(x => x.Detail)));
        Assert.Equal(result.ExpectedSignals - 1, result.CompletedSignals);
    }

    [Fact]
    public void Sent_order_without_open_position_is_not_qualified()
    {
        var day = new DateOnly(2026, 9, 28);
        var candles = History(day);
        var observed = Observed(candles, day, "WDO").ToDictionary();
        var signal = observed.First(x => x.Value.Side != 0);
        observed[signal.Key] = signal.Value with { PositionOpened = false, PositionClosed = false };

        var result = TimeRuleParity.Audit(candles, day, "WDO", observed);

        Assert.Contains(result.Issues, x => x.Code == "order-without-position" && x.Setup == signal.Key);
        Assert.Equal(result.ExpectedSignals - 1, result.CompletedSignals);
    }

    [Fact]
    public void Open_position_without_structured_close_is_not_qualified()
    {
        var day = new DateOnly(2026, 9, 28);
        var candles = History(day);
        var observed = Observed(candles, day, "WDO").ToDictionary();
        var signal = observed.First(x => x.Value.Side != 0);
        observed[signal.Key] = signal.Value with { PositionClosed = false };

        var result = TimeRuleParity.Audit(candles, day, "WDO", observed);

        Assert.Contains(result.Issues, x => x.Code == "position-not-closed" && x.Setup == signal.Key);
        Assert.Equal(result.ExpectedSignals - 1, result.CompletedSignals);
    }

    [Fact]
    public void Rollover_rescaled_history_passes_with_frozen_adr_and_live_candles()
    {
        // 30/09/2026: WDO$ virou para o X26 e o MT5 multiplicou o histórico contínuo por ~1,006.
        var day = new DateOnly(2026, 9, 28);
        var live = History(day);
        var observed = Observed(live, day, "WDO");
        var rescaled = live.Select(c => new Candle(c.OpenUtc, c.Open * 1.006m, c.High * 1.006m, c.Low * 1.006m, c.Close * 1.006m)).ToList();

        Assert.Contains(TimeRuleParity.Audit(rescaled, day, "WDO", observed).Issues, x => x.Code == "adr-mismatch");
        var result = TimeRuleParity.Audit(rescaled, day, "WDO", observed,
            liveAdrDays: DailyRange.Ranges(live, day), liveDayCandles: live);

        Assert.True(result.Matches, string.Join(" | ", result.Issues.Select(x => x.Detail)));
    }

    [Fact]
    public void Frozen_adr_from_stale_file_is_detected()
    {
        var day = new DateOnly(2026, 9, 28);
        var live = History(day);
        var stale = DailyRange.Ranges(live, day.AddDays(-3)); // sexta de fora: faltou o último pregão (bug de 28/09)

        var result = TimeRuleParity.Audit(live, day, "WDO", Observed(live, day, "WDO"), liveAdrDays: stale, liveDayCandles: live);

        Assert.Contains(result.Issues, x => x.Code == "adr-days-mismatch");
    }

    private static IReadOnlyDictionary<string, ObservedRuleDecision> Observed(IReadOnlyList<Candle> candles, DateOnly day, string instrument)
    {
        var adr = DailyRange.Adr(candles, day);
        var dayBars = candles.Where(c => DateOnly.FromDateTime(B3Clock.ToB3(c.OpenUtc).DateTime) == day).ToList();
        return SetupNames.TimeRules.Where(r => r.Instrument == instrument).ToDictionary(r => r.Name, r =>
        {
            var upTo = dayBars.Where(c => TimeOnly.FromDateTime(B3Clock.ToB3(c.OpenUtc).DateTime) <= r.At.AddMinutes(-5)).ToList();
            var side = r.SideFor(upTo, adr, out var move);
            return new ObservedRuleDecision(r.Name, side, adr, move, r.MinMoveAdr * adr,
                side != 0, side != 0, side != 0, side != 0);
        });
    }

    private static List<Candle> History(DateOnly day)
    {
        var result = new List<Candle>();
        for (var d = day.AddDays(-25); d <= day; d = d.AddDays(1))
        {
            if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            var i = 0;
            for (var time = new TimeOnly(9, 0); time <= new TimeOnly(17, 45); time = time.AddMinutes(5), i++)
            {
                var open = 5000m + (day.DayNumber - d.DayNumber) + i * (d == day ? 0.4m : 0.05m);
                var local = new DateTimeOffset(d.ToDateTime(time), TimeSpan.FromHours(-3));
                result.Add(new(local.ToUniversalTime(), open, open + 20, open - 20, open + 0.3m));
            }
        }
        return result;
    }
}
