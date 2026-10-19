namespace TradeGuard.Domain;

public sealed record ReplayTrade(DateTimeOffset EntryUtc, string Setup, Side Side, decimal Entry, decimal Points, string Exit);

public sealed record ReplayResult(IReadOnlyList<ReplayTrade> Trades, int Sessions, DateOnly? From, DateOnly? To)
{
    public decimal NetPoints => Trades.Sum(t => t.Points);
    public int Wins => Trades.Count(t => t.Points > 0);
}

/// <summary>
/// "E se o robô estivesse ligado?": passa os candles reais pelo MESMO <see cref="SignalEngine"/> do robô ao vivo
/// (mesmos setups, filtros e validade do gatilho) e simula cada sinal com o stop, o alvo, o break-even e os
/// horários das suas regras. Dentro de cada candle o preço é percorrido abertura → extremo mais perto → outro
/// extremo → fechamento; se stop e alvo cabem no mesmo candle, conta o stop (conservador). O preço de compra
/// (ask) é considerado meio ponto acima do negociado, como no book do mini dólar.
/// </summary>
public static class SetupReplay
{
    public static ReplayResult Run(IReadOnlyList<Candle> candles, RiskSettings s, decimal tickSize = 0.5m, TimeSpan? barLength = null)
    {
        var bar = barLength ?? TimeSpan.FromMinutes(5);
        var o = new SignalEngine.Options(s.EnabledSetups, s.TargetPoints, s.MaxStopPoints, tickSize, bar);
        var engine = new SignalEngine();
        var trades = new List<ReplayTrade>();
        var busyUntil = -1;

        for (var i = 30; i < candles.Count - 1; i++)
        {
            var closeUtc = candles[i].OpenUtc + bar;
            engine.OnBarClosed(candles.Take(i + 1).ToList(), closeUtc, o);
            if (i + 1 <= busyUntil) continue;

            // Próximo candle: o robô vê o preço andar e pode disparar.
            var next = candles[i + 1];
            var day = DateOnly.FromDateTime(B3Clock.ToB3(next.OpenUtc).DateTime);
            var levels = DayLevels.From(candles.Take(i + 1).ToList(), day);
            var path = Path(next);
            // A abertura acontece antes de o setup armar (o candle anterior acabou de fechar): não conta como rompimento.
            for (var p = 1; p < path.Length; p++)
            {
                var when = next.OpenUtc + bar * (p / (double)path.Length);
                var price = path[p];
                if (price == next.Open && p < 3) continue; // extremo igual à abertura = ainda o preço da abertura
                var signal = engine.OnPrice(price, price + tickSize, when, levels, o, out _);
                if (signal is null) continue;

                var time = TimeOnly.FromDateTime(B3Clock.ToB3(when).DateTime);
                if (time < s.WindowStart || time >= s.WindowEnd) break;
                var entry = signal.Side == Side.Buy ? price + tickSize : price;
                var (points, exit, lastBar) = Simulate(candles, i + 1, p, path, signal.Side, entry, s, bar);
                trades.Add(new ReplayTrade(when, signal.Setup, signal.Side, entry, points, exit));
                busyUntil = lastBar; // uma posição por vez, como no robô
                break;
            }
        }

        var days = candles.Select(c => DateOnly.FromDateTime(B3Clock.ToB3(c.OpenUtc).DateTime)).Distinct().Order().ToList();
        return new ReplayResult(trades, days.Count, days.FirstOrDefault(), days.LastOrDefault());
    }

    private static decimal[] Path(Candle c)
    {
        var lowFirst = c.Open - c.Low <= c.High - c.Open;
        return lowFirst ? [c.Open, c.Low, c.High, c.Close] : [c.Open, c.High, c.Low, c.Close];
    }

    private static (decimal Points, string Exit, int LastBar) Simulate(
        IReadOnlyList<Candle> candles, int startBar, int startStep, decimal[] startPath, Side side, decimal entry, RiskSettings s, TimeSpan bar)
    {
        var sign = side.Sign();
        var stop = entry - sign * s.StopPoints;
        var target = entry + sign * s.TargetPoints;
        var beDone = false;
        for (var b = startBar; b < candles.Count; b++)
        {
            var c = candles[b];
            var time = TimeOnly.FromDateTime(B3Clock.ToB3(c.OpenUtc).DateTime);
            var sameDay = B3Clock.ToB3(c.OpenUtc).Date == B3Clock.ToB3(candles[startBar].OpenUtc).Date;
            if (!sameDay || time >= s.FlattenAt)
                return ((candles[b - 1].Close - entry) * sign, "zerado no horário", b - 1);

            var path = b == startBar ? startPath[(startStep + 1)..] : Path(c);
            foreach (var price in path)
            {
                if ((price - stop) * sign <= 0) return ((stop - entry) * sign, beDone ? "break-even" : "stop", b);
                if ((price - target) * sign >= 0) return (s.TargetPoints, "alvo", b);
                if (s.BreakevenEnabled && !beDone && (price - entry) * sign >= s.BreakevenTriggerPoints)
                {
                    stop = entry + sign * s.BreakevenOffsetPoints;
                    beDone = true;
                }
            }
        }
        return ((candles[^1].Close - entry) * sign, "em aberto", candles.Count - 1);
    }
}
