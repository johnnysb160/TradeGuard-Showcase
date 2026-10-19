namespace TradeGuard.Domain;

/// <summary>
/// Teste tick a tick com os negócios reais: monta os candles de 5 min a partir dos negócios, passa pelo MESMO
/// <see cref="SignalEngine"/> e <see cref="FlowEngine"/> do robô ao vivo e simula cada entrada no preço do negócio
/// (compra no preço de venda, venda no de compra), com stop, alvo, break-even e horários das suas regras.
/// Sem aproximação dentro do candle: cada negócio é um passo.
/// </summary>
public static class FlowReplay
{
    /// <param name="days">Negócios de cada pregão, em ordem (horário UTC).</param>
    /// <param name="useFlowFilter">Liga a confirmação pelo fluxo no rompimento (como o filtro de fluxo das Regras).</param>
    /// <param name="costPoints">Custo por trade em pontos (taxas + derrapagem), descontado de cada resultado.</param>
    public static ReplayResult Run(IEnumerable<IReadOnlyList<TradePrint>> days, RiskSettings s, bool useFlowFilter,
        decimal tickSize = 0.5m, decimal costPoints = 0.5m)
    {
        var bar = TimeSpan.FromMinutes(5);
        var o = new SignalEngine.Options(s.EnabledSetups, s.TargetPoints, s.MaxStopPoints, tickSize, bar);
        var signals = new SignalEngine();
        var flow = new FlowEngine();
        var candles = new List<Candle>();
        var trades = new List<ReplayTrade>();
        var sessions = new List<DateOnly>();

        Candle? current = null;
        Position? pos = null;
        var one = new TradePrint[1];

        foreach (var day in days)
        {
            if (day.Count == 0) continue;
            sessions.Add(DateOnly.FromDateTime(B3Clock.ToB3(day[0].TimeUtc).DateTime));
            foreach (var p in day)
            {
                var now = p.TimeUtc;
                var barOpen = new DateTimeOffset(now.UtcTicks - now.UtcTicks % bar.Ticks, TimeSpan.Zero);

                // Fechou um candle: os setups olham para ele.
                if (current is not null && barOpen > current.OpenUtc)
                {
                    candles.Add(current);
                    signals.OnBarClosed(candles, current.OpenUtc + bar, o);
                    current = null;
                }
                current = current is null
                    ? new Candle(barOpen, p.Price, p.Price, p.Price, p.Price)
                    : current with { High = Math.Max(current.High, p.Price), Low = Math.Min(current.Low, p.Price), Close = p.Price };

                one[0] = p;
                flow.Add(one);

                var bid = p.Side == Aggressor.Sell ? p.Price : p.Price - tickSize;
                var ask = p.Side == Aggressor.Buy ? p.Price : p.Price + tickSize;
                var time = TimeOnly.FromDateTime(B3Clock.ToB3(now).DateTime);

                if (pos is not null)
                {
                    var exitPrice = pos.Side == Side.Buy ? bid : ask;
                    var result = pos.OnPrice(exitPrice, time, s);
                    if (result is { } r)
                    {
                        trades.Add(new ReplayTrade(pos.EntryUtc, pos.Setup, pos.Side, pos.Entry, r.Points - costPoints, r.Exit));
                        pos = null;
                    }
                    continue;
                }

                var today = DateOnly.FromDateTime(B3Clock.ToB3(now).DateTime);
                var levels = DayLevels.From(candles, today);
                SignalEngine.FlowCheck? check = useFlowFilter ? (Side side, out string why) => flow.Confirms(side, now, out why) : null;
                var signal = signals.OnPrice(bid, ask, now, levels, o, out _, check);
                if (signal is null || time < s.WindowStart || time >= s.WindowEnd) continue;

                var entry = signal.Side == Side.Buy ? ask : bid;
                pos = new Position(signal.Setup, signal.Side, entry, now, s);
            }

            // Fim do pregão com posição aberta: zera no último negócio (o robô já teria zerado no horário).
            if (pos is not null)
            {
                var last = day[^1].Price;
                trades.Add(new ReplayTrade(pos.EntryUtc, pos.Setup, pos.Side, pos.Entry, (last - pos.Entry) * pos.Side.Sign() - costPoints, "zerado no fim do dia"));
                pos = null;
            }
        }

        return new ReplayResult(trades, sessions.Count, sessions.FirstOrDefault(), sessions.LastOrDefault());
    }

    private sealed class Position(string setup, Side side, decimal entry, DateTimeOffset entryUtc, RiskSettings s)
    {
        public string Setup { get; } = setup;
        public Side Side { get; } = side;
        public decimal Entry { get; } = entry;
        public DateTimeOffset EntryUtc { get; } = entryUtc;
        private decimal _stop = entry - side.Sign() * s.StopPoints;
        private readonly decimal _target = entry + side.Sign() * s.TargetPoints;
        private bool _be;

        public (decimal Points, string Exit)? OnPrice(decimal price, TimeOnly time, RiskSettings s)
        {
            var sign = Side.Sign();
            if (time >= s.FlattenAt) return ((price - Entry) * sign, "zerado no horário");
            if ((price - _stop) * sign <= 0) return ((_stop - Entry) * sign, _be ? "break-even" : "stop");
            if ((price - _target) * sign >= 0) return (s.TargetPoints, "alvo");
            if (s.BreakevenEnabled && !_be && (price - Entry) * sign >= s.BreakevenTriggerPoints)
            {
                _stop = Entry + sign * s.BreakevenOffsetPoints;
                _be = true;
            }
            return null;
        }
    }
}
