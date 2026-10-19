namespace TradeGuard.Domain;

/// <summary>Uma barra (candle) fechada do gráfico.</summary>
public sealed record Candle(DateTimeOffset OpenUtc, decimal Open, decimal High, decimal Low, decimal Close);

/// <summary>Referências do dia usadas como "regiões" e como obstáculos para o alvo.</summary>
public sealed record DayLevels(decimal Open, decimal High, decimal Low, decimal? PreviousClose, decimal? RangeHigh, decimal? RangeLow)
{
    /// <summary>Faixa de abertura: 9:00 até 9:30 (horário de Brasília).</summary>
    public static readonly TimeOnly RangeStart = new(9, 0), RangeEnd = new(9, 30);

    public static DayLevels? From(IReadOnlyList<Candle> bars, DateOnly today)
    {
        var todayBars = bars.Where(b => Day(b) == today).ToList();
        if (todayBars.Count == 0) return null;
        var previous = bars.LastOrDefault(b => Day(b) < today);
        var range = todayBars.Where(b =>
        {
            var t = TimeOnly.FromDateTime(B3Clock.ToB3(b.OpenUtc).DateTime);
            return t >= RangeStart && t < RangeEnd;
        }).ToList();
        var rangeComplete = todayBars.Any(b => TimeOnly.FromDateTime(B3Clock.ToB3(b.OpenUtc).DateTime) >= RangeEnd);

        return new DayLevels(
            todayBars[0].Open, todayBars.Max(b => b.High), todayBars.Min(b => b.Low), previous?.Close,
            range.Count > 0 && rangeComplete ? range.Max(b => b.High) : null,
            range.Count > 0 && rangeComplete ? range.Min(b => b.Low) : null);
    }

    private static DateOnly Day(Candle b) => DateOnly.FromDateTime(B3Clock.ToB3(b.OpenUtc).DateTime);
}

public enum Trend { Sideways, Up, Down }

public static class Indicators
{
    /// <summary>
    /// Tendência pela média lenta (MM 21): ALTA = média subindo (vs. 5 candles atrás) e preço acima dela;
    /// BAIXA = o contrário; senão LATERAL. É a mesma regra usada no gráfico e nos setups.
    /// </summary>
    public static Trend TrendOf(IReadOnlyList<Candle> bars)
    {
        if (bars.Count < 26) return Trend.Sideways;
        var sma = Sma(bars, 21);
        var n = bars.Count - 1;
        if (sma[n] > sma[n - 5] && bars[n].Close > sma[n]) return Trend.Up;
        if (sma[n] < sma[n - 5] && bars[n].Close < sma[n]) return Trend.Down;
        return Trend.Sideways;
    }

    /// <summary>Média móvel exponencial (MME) dos fechamentos. Mesmo tamanho da entrada; início semeado com a média simples.</summary>
    public static decimal[] Ema(IReadOnlyList<Candle> bars, int period)
    {
        var result = new decimal[bars.Count];
        if (bars.Count == 0) return result;
        var k = 2m / (period + 1);
        var seed = Math.Min(period, bars.Count);
        var sma = bars.Take(seed).Average(b => b.Close);
        for (var i = 0; i < bars.Count; i++)
            result[i] = i < seed ? sma : bars[i].Close * k + result[i - 1] * (1 - k);
        return result;
    }

    /// <summary>Média móvel simples (MM) dos fechamentos. Posições sem dados suficientes ficam 0.</summary>
    public static decimal[] Sma(IReadOnlyList<Candle> bars, int period)
    {
        var result = new decimal[bars.Count];
        decimal sum = 0;
        for (var i = 0; i < bars.Count; i++)
        {
            sum += bars[i].Close;
            if (i >= period) sum -= bars[i - period].Close;
            if (i >= period - 1) result[i] = sum / period;
        }
        return result;
    }
}

/// <summary>Setup armado: entra se o preço romper o gatilho antes de expirar.</summary>
public enum TimeRuleKind
{
    /// <summary>Movimento do dia: abertura das 9:00 → fechamento do candle anterior ao horário.</summary>
    DayMove,
    /// <summary>Movimento da última hora.</summary>
    LastHour,
    /// <summary>Movimento dos últimos 30 minutos.</summary>
    Last30Min,
    /// <summary>Distância do preço até o preço médio do dia (média dos candles desde as 9:00).</summary>
    AwayFromAverage
}

/// <summary>
/// Estratégia de horário. Limiar e stop em fração da amplitude média diária (ADR, média de máxima−mínima dos 20 pregões
/// anteriores): assim a regra se ajusta à volatilidade de cada época. Em <see cref="At"/>, se o movimento passou de
/// <see cref="MinMoveAdr"/>×ADR, entra a favor (<see cref="Follow"/>) ou contra; stop <see cref="StopAdr"/>×ADR; sem alvo;
/// sai em <see cref="Exit"/>.
/// </summary>
public sealed record TimeRule(string Name, string Instrument, TimeOnly At, TimeRuleKind Kind, bool Follow, decimal MinMoveAdr, decimal StopAdr, TimeOnly Exit)
{
    public bool AppliesTo(string symbol) => symbol.StartsWith(Instrument, StringComparison.OrdinalIgnoreCase);

    /// <summary>Stop em pontos para a ADR do dia, arredondado ao tick (mínimo 2 ticks).</summary>
    public decimal StopPoints(decimal adr, decimal tick) => Math.Max(tick * 2, Math.Round(StopAdr * adr / tick) * tick);

    /// <summary>
    /// Lado da entrada (1 compra, -1 venda, 0 nada). Só no candle que acabou de fechar logo antes do horário
    /// (ex.: o das 10:25 para a regra das 10:30) — uma vez por dia.
    /// </summary>
    public int SideFor(IReadOnlyList<Candle> dayBars, decimal adr, out decimal move)
    {
        move = 0;
        if (dayBars.Count == 0 || adr <= 0) return 0;
        static TimeOnly T(Candle c) => TimeOnly.FromDateTime(B3Clock.ToB3(c.OpenUtc).DateTime);
        var last = dayBars[^1];
        if (T(last) != At.AddMinutes(-5)) return 0;
        switch (Kind)
        {
            case TimeRuleKind.AwayFromAverage:
                move = last.Close - dayBars.Average(b => (b.High + b.Low + b.Close) / 3);
                break;
            default:
                var from = Kind switch
                {
                    TimeRuleKind.DayMove => new TimeOnly(9, 0),
                    TimeRuleKind.LastHour => At.AddHours(-1),
                    _ => At.AddMinutes(-30)
                };
                var start = dayBars.FirstOrDefault(b => T(b) == from);
                if (start is null) return 0;
                move = last.Close - start.Open;
                break;
        }
        if (Math.Abs(move) < MinMoveAdr * adr) return 0;
        return Math.Sign(move) * (Follow ? 1 : -1);
    }

    /// <summary>
    /// Quanto o movimento da regra vale AGORA (sem esperar o horário), para a agenda mostrar se ela está sinalizando.
    /// Null se ainda não dá para medir (ex.: antes das 9:00).
    /// </summary>
    public decimal? MeasureNow(IReadOnlyList<Candle> dayBars)
    {
        if (dayBars.Count == 0) return null;
        var last = dayBars[^1];
        switch (Kind)
        {
            case TimeRuleKind.AwayFromAverage:
                return last.Close - dayBars.Average(b => (b.High + b.Low + b.Close) / 3);
            case TimeRuleKind.DayMove:
                return last.Close - dayBars[0].Open;
            default:
                var window = Kind == TimeRuleKind.LastHour ? 12 : 6;
                return dayBars.Count < window ? null : last.Close - dayBars[^window].Open;
        }
    }

    public string Explain()
    {
        var what = Kind switch
        {
            TimeRuleKind.DayMove => "o dia (desde as 9:00) andou",
            TimeRuleKind.LastHour => "a última hora andou",
            TimeRuleKind.Last30Min => "os últimos 30 min andaram",
            _ => "o preço está longe do preço médio do dia"
        };
        return $"{Instrument}: às {At:HH\\:mm}, se {what} ≥ {MinMoveAdr:0.##}× a amplitude média diária, entra " +
               $"{(Follow ? "a favor" : "contra")}. Stop {StopAdr:0.##}× a amplitude média, sem alvo, sai {Exit:HH\\:mm}. " +
               "Regra da carteira experimental v2; o histórico é monitorado, mas não garante resultado futuro.";
    }
}

public sealed record SetupCandidate(string Setup, Side Side, decimal Trigger, decimal TechnicalStop, DateTimeOffset ExpiresUtc, string Reason);

public static class SetupNames
{
    public const string NineOne = "9.1";
    public const string ContinuousPoint = "Ponto Contínuo";
    public const string OpeningRange = "Rompimento da abertura";
    /// <summary>
    /// Carteira experimental v2, derivada de 7.776 variações em cerca de 2 anos de WDO e WIN. A auditoria constatou
    /// que o conjunto de candidatas consultava a validação; portanto o resultado histórico não é chamado de prova
    /// fora da amostra. Entram a mercado, sem alvo/break-even, e saem no horário. Uma posição por ativo por vez.
    /// </summary>
    public static readonly IReadOnlyList<TimeRule> TimeRules =
    [
        new("WDO · Arrancada das 10h", "WDO", new(10, 0), TimeRuleKind.Last30Min, true, 0.18m, 0.15m, new(17, 45)),
        new("WDO · Dia forte 11:30", "WDO", new(11, 30), TimeRuleKind.DayMove, true, 0.5m, 0.15m, new(17, 45)),
        new("WDO · Embalo do meio-dia", "WDO", new(12, 0), TimeRuleKind.LastHour, true, 0.1m, 0.1m, new(13, 0)),
        new("WDO · Longe do preço médio 15h", "WDO", new(15, 0), TimeRuleKind.AwayFromAverage, true, 0.3m, 0.25m, new(16, 0)),
        new("WDO · Esticada das 16h", "WDO", new(16, 0), TimeRuleKind.DayMove, false, 0.25m, 0.15m, new(17, 0)),
        new("WIN · Abertura forte 10:30", "WIN", new(10, 30), TimeRuleKind.DayMove, true, 0.25m, 0.1m, new(17, 45)),
        new("WIN · Dia forte 11h", "WIN", new(11, 0), TimeRuleKind.DayMove, true, 0.5m, 0.1m, new(17, 45)),
        new("WIN · Longe do preço médio 13:30", "WIN", new(13, 30), TimeRuleKind.AwayFromAverage, true, 0.3m, 0.1m, new(14, 30)),
        new("WIN · Devolução das 15h", "WIN", new(15, 0), TimeRuleKind.Last30Min, false, 0.08m, 0.15m, new(17, 45)),
        new("WIN · Devolução das 16h", "WIN", new(16, 0), TimeRuleKind.LastHour, false, 0.1m, 0.15m, new(17, 45)),
        new("WIN · Longe do preço médio 16h", "WIN", new(16, 0), TimeRuleKind.AwayFromAverage, true, 0.3m, 0.15m, new(17, 45)),
    ];

    public static TimeRule? TimeRuleOf(string? setup) => TimeRules.FirstOrDefault(r => r.Name == setup);

    /// <summary>Comentário curto que sobrevive no MT5 e permite restaurar a regra após reiniciar o app.</summary>
    public static string TradeTag(string? setup)
    {
        for (var i = 0; i < TimeRules.Count; i++)
            if (TimeRules[i].Name == setup) return $"TG|R{i:00}";
        return "TG|APP";
    }

    public static string? SetupFromTradeTag(string? comment)
    {
        if (comment is null || !comment.StartsWith("TG|R", StringComparison.OrdinalIgnoreCase)
                            || !int.TryParse(comment.AsSpan(4, Math.Min(2, comment.Length - 4)), out var index)
                            || index < 0 || index >= TimeRules.Count)
            return null;
        return TimeRules[index].Name;
    }

    /// <summary>Catálogo completo, inclusive legado, para ler diários e reproduzir testes antigos.</summary>
    public static readonly IReadOnlyList<string> All = [.. TimeRules.Select(r => r.Name), NineOne, ContinuousPoint, OpeningRange];
    /// <summary>Únicas regras oferecidas para operação: as estratégias aprovadas da carteira v2.</summary>
    public static readonly IReadOnlyList<string> Operational = [.. TimeRules.Select(r => r.Name)];
    public static readonly IReadOnlyList<string> Legacy = [NineOne, ContinuousPoint, OpeningRange];

    /// <summary>O setup em palavras do dia a dia (o nome técnico vai entre parênteses).</summary>
    public static string Plain(string setup) => setup switch
    {
        _ when TimeRuleOf(setup) is not null => setup,
        NineOne => "média rápida virou (9.1)",
        ContinuousPoint => "voltou até a média e retomou (Ponto Contínuo)",
        OpeningRange => "rompeu a faixa das 9:00–9:30 (Rompimento da abertura)",
        _ => setup
    };

    public static string Explain(string setup) => setup switch
    {
        _ when TimeRuleOf(setup) is { } rule => rule.Explain(),
        NineOne => "Média rápida (MME 9) virou de direção; entra quando o preço passa a máxima/mínima do candle da virada.",
        ContinuousPoint => "Em tendência, o preço voltou até a média de 21 (pullback); entra na retomada.",
        OpeningRange => "Preço rompeu a faixa das 9:00–9:30 a favor da tendência.",
        _ => setup
    };
}

/// <summary>
/// Detecta os setups quando um candle fecha e dispara o sinal quando o preço rompe o gatilho.
/// Puro: recebe candles, preço e hora; não envia ordens.
/// </summary>
public sealed class SignalEngine
{
    private readonly List<SetupCandidate> _armed = [];
    // Preço mais perto do gatilho que cada setup armado já viu (para explicar quando expira sem disparar).
    private readonly Dictionary<SetupCandidate, decimal> _closest = [];
    private readonly List<(SetupCandidate Candidate, decimal? Closest)> _expired = [];
    private readonly HashSet<string> _openingRangeUsed = [];
    private readonly HashSet<SetupCandidate> _flowWaiting = []; // rompeu e espera o fluxo (avisa uma vez só)

    /// <summary>Confirmação pelo fluxo (tape reading) no momento do rompimento. Devolve o motivo quando não confirma.</summary>
    public delegate bool FlowCheck(Side side, out string why);
    private DateOnly _day;

    public IReadOnlyList<SetupCandidate> Armed => _armed;

    /// <summary>Setups que expiraram sem disparar desde a última chamada, com o preço mais perto que chegaram.</summary>
    public IReadOnlyList<(SetupCandidate Candidate, decimal? Closest)> TakeExpired()
    {
        var list = _expired.ToList();
        _expired.Clear();
        return list;
    }

    /// <summary>Descarta um candidato armado sem registrá-lo como expirado (a decisão já foi tomada fora).</summary>
    public void Disarm(SetupCandidate candidate)
    {
        _armed.Remove(candidate);
        _closest.Remove(candidate);
        _flowWaiting.Remove(candidate);
    }

    private void Expire(DateTimeOffset nowUtc)
    {
        foreach (var c in _armed.Where(c => nowUtc >= c.ExpiresUtc).ToList())
        {
            _expired.Add((c, _closest.TryGetValue(c, out var p) ? p : null));
            _armed.Remove(c);
            _closest.Remove(c);
            _flowWaiting.Remove(c);
        }
    }

    /// <summary>
    /// Refaz a leitura dos candles recentes (ex.: o programa foi reaberto) e rearma o que ainda vale.
    /// Gatilho que o preço atual já ultrapassou é descartado: não gera alerta atrasado.
    /// </summary>
    public void Rebuild(IReadOnlyList<Candle> bars, DateTimeOffset nowUtc, Options o, decimal bid, decimal ask)
    {
        _armed.Clear();
        _closest.Clear();
        for (var i = Math.Max(30, bars.Count - 80); i <= bars.Count; i++)
            OnBarClosed(bars.Take(i).ToList(), nowUtc, o);
        _expired.Clear();
        _armed.RemoveAll(c => c.Side == Side.Buy ? ask > 0 && ask >= c.Trigger : bid > 0 && bid <= c.Trigger);
    }

    public sealed record Options(
        IReadOnlyCollection<string> Enabled, decimal TargetPoints, decimal MaxStopPoints, decimal TickSize, TimeSpan BarLength,
        decimal Adr = 0, string Symbol = "");

    /// <summary>Chamado a cada candle fechado (lista em ordem, o último é o que acabou de fechar).</summary>
    public IReadOnlyList<SetupCandidate> OnBarClosed(IReadOnlyList<Candle> bars, DateTimeOffset nowUtc, Options o)
    {
        var today = DateOnly.FromDateTime(B3Clock.ToB3(nowUtc).DateTime);
        if (today != _day) { _day = today; _openingRangeUsed.Clear(); _armed.Clear(); _closest.Clear(); _flowWaiting.Clear(); }
        Expire(nowUtc);
        if (bars.Count < 30) return [];

        var fresh = new List<SetupCandidate>();
        var last = bars[^1];
        var expires = last.OpenUtc + o.BarLength * 3; // vale durante os próximos 2 candles
        var ema9 = Indicators.Ema(bars, 9);
        var sma21 = Indicators.Sma(bars, 21);
        var n = bars.Count - 1;
        var trend = Indicators.TrendOf(bars);
        var trendUp = trend == Trend.Up;
        var trendDown = trend == Trend.Down;

        // Rompimento da abertura vale só a favor da tendência: se ela virou, o gatilho armado cai.
        _armed.RemoveAll(a => a.Setup == SetupNames.OpeningRange && (a.Side == Side.Buy ? !trendUp : !trendDown));

        // 9.1: a MME 9 vira. Só a favor da média de 21 (filtro de qualidade).
        if (o.Enabled.Contains(SetupNames.NineOne))
        {
            if (ema9[n] > ema9[n - 1] && ema9[n - 1] <= ema9[n - 2] && trendUp)
                fresh.Add(new(SetupNames.NineOne, Side.Buy, last.High + o.TickSize, last.Low - o.TickSize, expires,
                    $"MME 9 virou para cima; compra acima de {last.High + o.TickSize:0.0}."));
            if (ema9[n] < ema9[n - 1] && ema9[n - 1] >= ema9[n - 2] && trendDown)
                fresh.Add(new(SetupNames.NineOne, Side.Sell, last.Low - o.TickSize, last.High + o.TickSize, expires,
                    $"MME 9 virou para baixo; venda abaixo de {last.Low - o.TickSize:0.0}."));
        }

        // Ponto Contínuo: tendência pela MM 21 e o candle tocou a média.
        if (o.Enabled.Contains(SetupNames.ContinuousPoint))
        {
            if (trendUp && last.Low <= sma21[n] && last.Close >= sma21[n])
                fresh.Add(new(SetupNames.ContinuousPoint, Side.Buy, last.High + o.TickSize, last.Low - o.TickSize, expires,
                    $"Tendência de alta e o preço tocou a MM 21; compra acima de {last.High + o.TickSize:0.0}."));
            if (trendDown && last.High >= sma21[n] && last.Close <= sma21[n])
                fresh.Add(new(SetupNames.ContinuousPoint, Side.Sell, last.Low - o.TickSize, last.High + o.TickSize, expires,
                    $"Tendência de baixa e o preço tocou a MM 21; venda abaixo de {last.Low - o.TickSize:0.0}."));
        }

        // Rompimento da abertura: uma vez por lado, por dia, a favor da tendência.
        if (o.Enabled.Contains(SetupNames.OpeningRange) && DayLevels.From(bars, today) is { RangeHigh: { } rh, RangeLow: { } rl })
        {
            var endOfDay = new DateTimeOffset(today.ToDateTime(new TimeOnly(17, 30)), B3Clock.Zone.GetUtcOffset(nowUtc));
            if (trendUp && !_openingRangeUsed.Contains("buy") && last.Close <= rh)
                fresh.Add(new(SetupNames.OpeningRange, Side.Buy, rh + o.TickSize, rl, endOfDay,
                    $"Faixa 9:00–9:30 entre {rl:0.0} e {rh:0.0}; compra se romper {rh + o.TickSize:0.0}."));
            if (trendDown && !_openingRangeUsed.Contains("sell") && last.Close >= rl)
                fresh.Add(new(SetupNames.OpeningRange, Side.Sell, rl - o.TickSize, rh, endOfDay,
                    $"Faixa 9:00–9:30 entre {rl:0.0} e {rh:0.0}; venda se romper {rl - o.TickSize:0.0}."));
        }

        // Estratégias de horário: o candle anterior ao horário fechou → mede o movimento e entra a mercado (vale 5 min).
        var todayBars = bars.Where(b => DateOnly.FromDateTime(B3Clock.ToB3(b.OpenUtc).DateTime) == today).ToList();
        foreach (var rule in SetupNames.TimeRules.Where(r => o.Enabled.Contains(r.Name) && r.AppliesTo(o.Symbol)))
        {
            var side = rule.SideFor(todayBars, o.Adr, out var move);
            if (side == 0) continue;
            var buy = side > 0;
            var what = rule.Kind switch
            {
                TimeRuleKind.DayMove => "desde as 9:00",
                TimeRuleKind.LastHour => "na última hora",
                TimeRuleKind.Last30Min => "nos últimos 30 min",
                _ => "do preço médio do dia"
            };
            fresh.Add(new(rule.Name, buy ? Side.Buy : Side.Sell, buy ? 0 : decimal.MaxValue, 0, last.OpenUtc + o.BarLength * 2,
                $"{rule.At:HH\\:mm}: {(rule.Kind == TimeRuleKind.AwayFromAverage ? $"preço {Math.Abs(move):0.#} pts {(move > 0 ? "acima" : "abaixo")}" : $"{(move > 0 ? "subiu" : "caiu")} {Math.Abs(move):0.#} pts")} {what} " +
                $"(limite {rule.MinMoveAdr * o.Adr:0.#}) → {(rule.Follow ? "a favor" : "contra")}: {(buy ? "compra" : "venda")}. " +
                $"Stop {rule.StopPoints(o.Adr, o.TickSize):0.#} pts, sem alvo, sai {rule.Exit:HH\\:mm}."));
        }

        // Um candidato por setup e lado: o mais novo substitui o anterior.
        foreach (var c in fresh)
        {
            foreach (var old in _armed.Where(a => a.Setup == c.Setup && a.Side == c.Side).ToList())
            {
                _armed.Remove(old);
                _closest.Remove(old);
            }
            _armed.Add(c);
        }
        return fresh;
    }

    /// <summary>
    /// Chamado a cada cotação. Devolve o sinal (uma vez) quando o preço rompe o gatilho de um setup armado
    /// e passa nos filtros de risco; <paramref name="skipped"/> explica sinais descartados.
    /// </summary>
    public EntrySignal? OnPrice(decimal bid, decimal ask, DateTimeOffset nowUtc, DayLevels? levels, Options o, out string? skipped,
        FlowCheck? flow = null)
    {
        skipped = null;
        Expire(nowUtc);

        foreach (var c in _armed.ToList())
        {
            var seen = c.Side == Side.Buy ? ask : bid;
            if (seen > 0)
                _closest[c] = _closest.TryGetValue(c, out var prev)
                    ? (c.Side == Side.Buy ? Math.Max(prev, seen) : Math.Min(prev, seen))
                    : seen;

            var hit = c.Side == Side.Buy ? ask >= c.Trigger : bid > 0 && bid <= c.Trigger;
            if (!hit) continue;

            // Momento da abertura: entrada a mercado, sem filtros de rompimento (foi testado assim).
            if (SetupNames.TimeRuleOf(c.Setup) is not null)
            {
                _armed.Remove(c);
                _closest.Remove(c);
                return new EntrySignal(c.Side, c.Setup, c.Reason);
            }

            // Rompeu, mas o fluxo não confirma: continua armado esperando a agressão a favor (até expirar).
            if (flow is not null && !flow(c.Side, out var flowWhy))
            {
                if (_flowWaiting.Add(c)) skipped = $"{c.Setup}: preço rompeu {c.Trigger:0.0}, mas {flowWhy}. Esperando o fluxo confirmar.";
                continue;
            }
            _flowWaiting.Remove(c);
            _armed.Remove(c);
            _closest.Remove(c);
            if (c.Setup == SetupNames.OpeningRange) _openingRangeUsed.Add(c.Side == Side.Buy ? "buy" : "sell");

            var entry = c.Side == Side.Buy ? ask : bid;
            var technicalRisk = Math.Abs(entry - c.TechnicalStop);
            if (technicalRisk > o.MaxStopPoints)
            {
                skipped = $"{c.Setup}: o stop técnico ficaria a {technicalRisk:0.0} pts (máximo {o.MaxStopPoints}). Sinal ignorado.";
                continue;
            }

            // Espaço até o alvo: não entra se a máxima/mínima do dia estiver no caminho.
            if (levels is not null)
            {
                var obstacle = c.Side == Side.Buy ? levels.High : levels.Low;
                var room = (obstacle - entry) * c.Side.Sign();
                if (room > 0 && room < o.TargetPoints)
                {
                    skipped = $"{c.Setup}: só {room:0.0} pts até a {(c.Side == Side.Buy ? "máxima" : "mínima")} do dia (alvo {o.TargetPoints}). Sinal ignorado.";
                    continue;
                }
            }

            return new EntrySignal(c.Side, c.Setup, c.Reason);
        }
        return null;
    }
}
