namespace TradeGuard.Domain;

public enum StepState { Ok, Waiting, Blocked, Idle }

/// <summary>Tom da frase principal: para onde o robô está olhando.</summary>
public enum ReadingTone { Neutral, Buy, Sell, Blocked }

public sealed record ReadingStep(string Title, StepState State, string Detail);

/// <summary>
/// "O que o robô está esperando", em linguagem do dia a dia: uma frase principal (como está e o que vem)
/// e as etapas que levam a um alerta.
/// </summary>
public sealed record MarketReading(Trend Trend, ReadingTone Tone, string Headline, string? Subline, IReadOnlyList<ReadingStep> Steps)
{
    public static readonly MarketReading Empty = new(Trend.Sideways, ReadingTone.Neutral,
        "Carregando o gráfico do MT5…", null, []);
}

public static class MarketReader
{
    private const string Where = "Para onde vai";
    private const string Entry = "Ponto de entrada";
    private const string Room = "Espaço para lucrar";
    private const string Free = "Travas";

    public static MarketReading Read(
        IReadOnlyList<Candle> candles, DayLevels? levels, IReadOnlyList<SetupCandidate> armed,
        decimal bid, decimal ask, decimal targetPoints, IReadOnlyList<Block> blocks)
    {
        if (candles.Count < 26) return MarketReading.Empty;

        var trend = Indicators.TrendOf(candles);
        var slow = Indicators.Sma(candles, 21)[^1];
        var price = ask > 0 ? (bid + ask) / 2 : candles[^1].Close;
        var side = trend == Trend.Up ? Side.Buy : Side.Sell;
        var action = side == Side.Buy ? "COMPRAR" : "VENDER";

        var roomStep = RoomStep(trend, levels, bid, ask, targetPoints);
        var lockStep = LockStep(blocks);
        var steps = new List<ReadingStep>
        {
            WhereStep(trend),
            EntryStep(trend, armed, slow, price, bid, ask),
            roomStep,
            lockStep
        };

        // Frase principal.
        string headline;
        string? subline;
        var tone = trend switch { Trend.Up => ReadingTone.Buy, Trend.Down => ReadingTone.Sell, _ => ReadingTone.Neutral };
        var nearest = armed
            .Where(a => trend != Trend.Sideways && a.Side == side)
            .OrderBy(a => Distance(a, bid, ask))
            .FirstOrDefault();

        if (trend == Trend.Sideways)
        {
            headline = "Sem sinal agora. Mercado sem direção: não entre.";
            subline = "O robô avisa quando o mercado escolher um lado e aparecer um ponto de entrada.";
        }
        else if (nearest is not null)
        {
            var dir = side == Side.Buy ? "subir acima de" : "cair abaixo de";
            headline = $"Sem sinal agora. {Mood(trend)} Se o preço {dir} {nearest.Trigger:0.0}, o robô avisa para {action}.";
            subline = $"Faltam {Distance(nearest, bid, ask):0.0} pts · vale até {B3Clock.ToB3(nearest.ExpiresUtc):HH:mm} · {SetupNames.Plain(nearest.Setup)}. Até o aviso, não entre.";
        }
        else
        {
            headline = $"Sem sinal agora. {Mood(trend)} Esperando um ponto bom para {action}.";
            subline = side == Side.Buy
                ? $"Costuma aparecer quando o preço volta perto de {slow:0.0} (faltam {Math.Abs(price - slow):0.0} pts) e volta a subir."
                : $"Costuma aparecer quando o preço volta perto de {slow:0.0} (faltam {Math.Abs(price - slow):0.0} pts) e volta a cair.";
        }

        var obstacle = new[] { lockStep, roomStep }.FirstOrDefault(s => s.State == StepState.Blocked);
        if (obstacle is not null && trend != Trend.Sideways)
        {
            tone = ReadingTone.Blocked;
            subline = "Mas agora não: " + obstacle.Detail;
        }

        return new MarketReading(trend, tone, headline, subline, steps);
    }

    private static string Mood(Trend t) => t == Trend.Up ? "Mercado subindo." : "Mercado caindo.";

    private static decimal Distance(SetupCandidate a, decimal bid, decimal ask) =>
        Math.Max(0, a.Side == Side.Buy ? a.Trigger - ask : bid - a.Trigger);

    private static ReadingStep WhereStep(Trend trend) => trend switch
    {
        Trend.Up => new(Where, StepState.Ok, "Subindo ▲ (preço acima da linha branca, e ela subindo). Só compras."),
        Trend.Down => new(Where, StepState.Ok, "Caindo ▼ (preço abaixo da linha branca, e ela caindo). Só vendas."),
        _ => new(Where, StepState.Waiting, "Sem direção ↔. Espere o mercado escolher um lado.")
    };

    private static ReadingStep EntryStep(Trend trend, IReadOnlyList<SetupCandidate> armed, decimal slow, decimal price, decimal bid, decimal ask)
    {
        if (armed.Count > 0)
        {
            var text = string.Join(" · ", armed.Select(a =>
                $"{(a.Side == Side.Buy ? "comprar acima de" : "vender abaixo de")} {a.Trigger:0.0} (faltam {Distance(a, bid, ask):0.0} pts) — {SetupNames.Plain(a.Setup)}"));
            // Armado não é sinal: só vira entrada quando o preço romper o gatilho.
            return new(Entry, StepState.Waiting, "Gatilho armado: " + text + ". Até romper, não entre.");
        }

        return trend switch
        {
            Trend.Up => new(Entry, StepState.Waiting,
                $"Ainda não. Esperando o preço voltar perto de {slow:0.0} e retomar a subida."),
            Trend.Down => new(Entry, StepState.Waiting,
                $"Ainda não. Esperando o preço voltar perto de {slow:0.0} e retomar a queda."),
            _ => new(Entry, StepState.Idle, "Só depois que o mercado tiver direção.")
        };
    }

    private static ReadingStep RoomStep(Trend trend, DayLevels? levels, decimal bid, decimal ask, decimal target)
    {
        if (trend == Trend.Sideways || levels is null || ask <= 0)
            return new(Room, StepState.Idle, "Depende da direção.");

        if (trend == Trend.Up)
        {
            if (ask >= levels.High) return new(Room, StepState.Ok, "Preço acima da máxima do dia: caminho livre.");
            var room = levels.High - ask;
            return room < target
                ? new(Room, StepState.Blocked, $"só {room:0.0} pts até a máxima do dia ({levels.High:0.0}); o alvo precisa de {target}.")
                : new(Room, StepState.Ok, $"{room:0.0} pts até a máxima do dia: cabe o alvo de {target}.");
        }

        if (bid <= levels.Low) return new(Room, StepState.Ok, "Preço abaixo da mínima do dia: caminho livre.");
        var down = bid - levels.Low;
        return down < target
            ? new(Room, StepState.Blocked, $"só {down:0.0} pts até a mínima do dia ({levels.Low:0.0}); o alvo precisa de {target}.")
            : new(Room, StepState.Ok, $"{down:0.0} pts até a mínima do dia: cabe o alvo de {target}.");
    }

    // Todas as travas ativas juntas (antes ficavam também num quadro separado "Por que está bloqueado").
    private static ReadingStep LockStep(IReadOnlyList<Block> blocks)
    {
        var active = blocks.Where(b => b.Reason != BlockReason.PositionAlreadyOpen).Select(b => b.Message).ToList();
        return active.Count > 0
            ? new(Free, StepState.Blocked, string.Join(" · ", active))
            : new(Free, StepState.Ok, "Nenhuma trava ativa. Quando o sinal vier (com som), pode entrar.");
    }
}
