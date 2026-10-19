using TradeGuard.Domain;

namespace TradeGuard.Tests;

public class ManagedPositionTests
{
    private static readonly InstrumentSpec Wdo = new("WDOV26", 0.5m, 5m, null);
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 13, 0, 0, TimeSpan.Zero);

    private static ManagedPosition Long(RiskSettings? s = null, int contracts = 1, decimal? stop = null) =>
        new(Side.Buy, 5000m, contracts, stop, null, s ?? RiskSettings.Default, Wdo, T0, "test");

    private static readonly RiskSettings Pyramid = RiskSettings.Default with
    {
        MaxContracts = 3, ScaleMode = ScaleMode.WithTrend, ScaleAddContracts = 1, ScaleMaxAdds = 2, ScaleStepPoints = 4, BreakevenEnabled = false
    };

    // Perda da posição inteira se o stop for atingido, em R$.
    private static decimal LossAtStop(ManagedPosition p, decimal avg, int contracts) => Wdo.Money(Math.Abs(avg - p.Stop), contracts);

    [Fact]
    public void Pyramid_adds_only_with_flow_and_never_raises_the_max_loss()
    {
        var p = Long(Pyramid);
        Assert.Empty(p.OnPrice(5004m, T0, null, (_, _) => false).OfType<AddContracts>()); // fluxo não confirma
        var add = Assert.Single(p.OnPrice(5004m, T0, null, (_, _) => true).OfType<AddContracts>());
        Assert.Equal(1, add.Contracts);
        p.SyncFromBroker(2, 5002m, add.NewStop, 5010m);
        Assert.True(LossAtStop(p, 5002m, 2) <= 50m, $"perda {LossAtStop(p, 5002m, 2)}"); // igual ou menor que a inicial (R$ 50)
        Assert.True(p.Stop > 4995m); // o stop apertou

        var second = Assert.Single(p.OnPrice(5008m, T0, null, (_, _) => true).OfType<AddContracts>());
        p.SyncFromBroker(3, (5000m + 5004m + 5008m) / 3, second.NewStop, 5010m);
        Assert.True(LossAtStop(p, (5000m + 5004m + 5008m) / 3, 3) <= 50m,
            $"perda {LossAtStop(p, (5000m + 5004m + 5008m) / 3, 3)}, stop {p.Stop}");
        Assert.Empty(p.OnPrice(5012m, T0, null, (_, _) => true).OfType<AddContracts>()); // máximo de 2 aumentos
    }

    [Fact]
    public void Averaging_adds_against_only_with_absorption_and_tightens_the_stop()
    {
        var s = Pyramid with { ScaleMode = ScaleMode.Average, ScaleMaxAdds = 1, ScaleStepPoints = 2 };
        var p = Long(s);
        Assert.Empty(p.OnPrice(4999m, T0, null, (_, _) => true).OfType<AddContracts>()); // ainda não andou 2 contra
        var add = Assert.Single(p.OnPrice(4998m, T0, null, (_, _) => true).OfType<AddContracts>());
        p.SyncFromBroker(2, 4999m, add.NewStop, 5010m);
        Assert.True(LossAtStop(p, 4999m, 2) <= 50m, $"perda {LossAtStop(p, 4999m, 2)}");
        Assert.True(add.NewStop > 4995m && add.NewStop < 4998m); // mais perto que o original, abaixo do preço
    }

    [Fact]
    public void No_scaling_without_flow_data_or_when_off()
    {
        Assert.Empty(Long(Pyramid).OnPrice(5004m, T0, null, null).OfType<AddContracts>());
        Assert.Empty(Long(Pyramid with { ScaleMode = ScaleMode.Off }).OnPrice(5004m, T0, null, (_, _) => true).OfType<AddContracts>());
    }

    [Fact]
    public void Scaling_needs_room_in_max_contracts() =>
        Assert.Contains((RiskSettings.Default with { ScaleMode = ScaleMode.WithTrend, MaxContracts = 1 }).Validate(), e => e.Contains("Máximo de contratos"));

    [Fact]
    public void Default_brackets_are_stop_5_and_target_10()
    {
        var p = Long();
        Assert.Equal(4995m, p.Stop);
        Assert.Equal(5010m, p.Target);
        Assert.Equal(50m, p.InitialRiskMoney); // 5 pts × R$ 10
    }

    [Fact]
    public void Short_brackets_are_mirrored()
    {
        var p = new ManagedPosition(Side.Sell, 5000m, 1, null, null, RiskSettings.Default, Wdo, T0, "t");
        Assert.Equal(5005m, p.Stop);
        Assert.Equal(4990m, p.Target);
    }

    [Fact]
    public void Manual_stop_inside_max_is_respected()
    {
        var p = Long(stop: 4993m);
        Assert.Equal(4993m, p.Stop);
    }

    [Fact]
    public void Manual_stop_wider_than_max_is_tightened()
    {
        var p = Long(stop: 4980m); // 20 pts, máximo é 8
        Assert.Equal(4995m, p.Stop);
    }

    [Fact]
    public void Breakeven_moves_stop_to_entry_once()
    {
        var p = Long();
        Assert.Empty(p.OnPrice(5004m, T0));
        var actions = p.OnPrice(5005m, T0);
        var move = Assert.Single(actions.OfType<MoveStop>());
        Assert.Equal(5000m, move.NewStop);
        Assert.Empty(p.OnPrice(5006m, T0));
    }

    [Fact]
    public void Partial_is_skipped_with_single_contract()
    {
        var p = Long();
        Assert.DoesNotContain(p.OnPrice(5006m, T0), a => a is ClosePartial);
    }

    [Fact]
    public void Partial_closes_one_of_two_contracts()
    {
        var p = Long(RiskSettings.TwoContracts, contracts: 2);
        var partial = Assert.Single(p.OnPrice(5005m, T0).OfType<ClosePartial>());
        Assert.Equal(1, partial.Contracts);
        Assert.DoesNotContain(p.OnPrice(5006m, T0), a => a is ClosePartial);
    }

    [Fact]
    public void Profit_lock_step_secures_points()
    {
        var s = RiskSettings.Default with { BreakevenEnabled = false, ProfitLocks = [new ProfitLockLevel(8, 4)] };
        var p = Long(s);
        var move = Assert.Single(p.OnPrice(5008m, T0).OfType<MoveStop>());
        Assert.Equal(5004m, move.NewStop);
    }

    [Fact]
    public void Fixed_trailing_follows_price_and_never_goes_back()
    {
        var s = RiskSettings.Default with
        {
            BreakevenEnabled = false, TrailingEnabled = true,
            TrailingStartPoints = 6, TrailingDistancePoints = 4, TrailingStepPoints = 1
        };
        var p = Long(s);
        Assert.Equal(5004m, Assert.Single(p.OnPrice(5008m, T0).OfType<MoveStop>()).NewStop);
        Assert.Empty(p.OnPrice(5006m, T0)); // voltou: stop não desce
        Assert.Equal(5006m, Assert.Single(p.OnPrice(5010m, T0).OfType<MoveStop>()).NewStop);
    }

    [Fact]
    public void Candle_trailing_uses_previous_bar_low()
    {
        var s = RiskSettings.Default with
        {
            BreakevenEnabled = false, TrailingEnabled = true, TrailingMode = TrailingMode.PreviousCandle,
            TrailingStartPoints = 5, TrailingStepPoints = 0.5m
        };
        var p = Long(s);
        var bar = new Bar(T0, 5003m, 5007m, 5003m, 5006m);
        Assert.Equal(5002.5m, Assert.Single(p.OnPrice(5007m, T0, bar).OfType<MoveStop>()).NewStop);
    }

    [Fact]
    public void Time_exit_closes_after_max_minutes()
    {
        var p = Long(RiskSettings.Default with { MaxHoldMinutes = 15 });
        Assert.Empty(p.OnPrice(5001m, T0.AddMinutes(14)));
        Assert.IsType<CloseAll>(Assert.Single(p.OnPrice(5001m, T0.AddMinutes(15))));
        Assert.Empty(p.OnPrice(5001m, T0.AddMinutes(16)));
    }

    [Fact]
    public void Loosened_stop_at_broker_is_reverted()
    {
        var p = Long();
        Assert.Equal(4995m, p.SyncFromBroker(1, 5000m, 4990m, 5010m));
        Assert.Equal(4995m, p.SyncFromBroker(1, 5000m, 0m, 5010m)); // sem stop
        Assert.Null(p.SyncFromBroker(1, 5000m, 4997m, 5010m)); // apertar é aceito
        Assert.Equal(4997m, p.Stop);
    }

    [Fact]
    public void Mfe_and_mae_are_tracked()
    {
        var p = Long();
        p.OnPrice(4997m, T0);
        p.OnPrice(5003m, T0);
        Assert.Equal(3m, p.MaePoints);
        Assert.Equal(3m, p.MfePoints);
    }
}
