namespace TradeGuard.Domain;

/// <summary>Decisão observada no arquivo estruturado produzido pelo motor ao vivo.</summary>
public sealed record ObservedRuleDecision(
    string Setup, int Side, decimal Adr, decimal? Move, decimal? Limit,
    bool ExecutionRequested, bool OrderSent, bool PositionOpened, bool PositionClosed);

public sealed record ParityIssue(string Code, string Setup, string Detail);

/// <summary>Resultado reproduzível da comparação entre os candles arquivados e as decisões ao vivo.</summary>
public sealed record DailyParityResult(
    DateOnly Day, string Instrument, int ExpectedEvaluations, int ObservedEvaluations,
    int ExpectedSignals, int ObservedSignals, int CompletedSignals, IReadOnlyList<ParityIssue> Issues)
{
    public bool Matches => Issues.Count == 0;
}

/// <summary>
/// Refaz cada regra usando somente candles que já estavam fechados no horário da decisão. Além do lado,
/// confere os valores intermediários (ADR, movimento e limite) e a cadeia pedido → ordem → posição → saída.
/// </summary>
public static class TimeRuleParity
{
    /// <param name="liveAdrDays">Amplitudes congeladas pelo motor ao vivo no início do dia. A série contínua do MT5
    /// (<c>WDO$</c>/<c>WIN$</c>) é ajustada por razão a cada rollover e reescreve o passado; por isso o ADR é refeito a
    /// partir do congelado e o histórico só confere que os dias e as amplitudes batem (a menos de um fator único).</param>
    /// <param name="liveDayCandles">Candles do dia gravados ao vivo (preço real do contrato), imunes ao reajuste.</param>
    public static DailyParityResult Audit(
        IReadOnlyList<Candle> candles, DateOnly day, string instrument,
        IReadOnlyDictionary<string, ObservedRuleDecision> observed, bool requireClosedPositions = true,
        IReadOnlyList<DayRange>? liveAdrDays = null, IReadOnlyList<Candle>? liveDayCandles = null)
    {
        var issues = new List<ParityIssue>();
        var rules = SetupNames.TimeRules.Where(r => r.Instrument.Equals(instrument, StringComparison.OrdinalIgnoreCase)).ToList();
        var adr = DailyRange.Adr(candles, day);
        if (liveAdrDays is { Count: > 0 })
        {
            CheckAdrDays(liveAdrDays, DailyRange.Ranges(candles, day), instrument, issues);
            adr = DailyRange.Adr(liveAdrDays);
        }
        var dayBars = (liveDayCandles is { Count: > 0 } ? liveDayCandles : candles)
            .Where(c => DateOnly.FromDateTime(B3Clock.ToB3(c.OpenUtc).DateTime) == day)
            .OrderBy(c => c.OpenUtc).ToList();
        var expectedSignals = 0;
        var observedSignals = 0;
        var completedSignals = 0;

        foreach (var rule in rules)
        {
            var cutoff = rule.At.AddMinutes(-5);
            var bars = dayBars.Where(c => TimeOnly.FromDateTime(B3Clock.ToB3(c.OpenUtc).DateTime) <= cutoff).ToList();
            if (bars.Count == 0 || TimeOnly.FromDateTime(B3Clock.ToB3(bars[^1].OpenUtc).DateTime) != cutoff)
            {
                issues.Add(new("missing-candle", rule.Name, $"Não existe candle fechado de {cutoff:HH\\:mm} para refazer a decisão."));
                continue;
            }

            var side = rule.SideFor(bars, adr, out var move);
            var limit = rule.MinMoveAdr * adr;
            if (side != 0) expectedSignals++;

            if (!observed.TryGetValue(rule.Name, out var live))
            {
                issues.Add(new("missing-live-evaluation", rule.Name, "A regra não deixou registro de avaliação ao vivo."));
                continue;
            }

            if (live.Side != 0) observedSignals++;
            if (live.Side != side)
                issues.Add(new("side-mismatch", rule.Name, $"Replay={side}; ao vivo={live.Side}."));
            Compare("adr-mismatch", rule.Name, "ADR", adr, live.Adr, issues);
            if (live.Move is not { } liveMove)
                issues.Add(new("missing-live-value", rule.Name, "Movimento não foi registrado."));
            else
                Compare("move-mismatch", rule.Name, "Movimento", move, liveMove, issues);
            if (live.Limit is not { } liveLimit)
                issues.Add(new("missing-live-value", rule.Name, "Limite não foi registrado."));
            else
                Compare("limit-mismatch", rule.Name, "Limite", limit, liveLimit, issues);

            if (side != 0 && !live.ExecutionRequested)
                issues.Add(new("signal-not-executed", rule.Name, "O replay encontrou sinal, mas o motor não iniciou a execução."));
            if (side == 0 && live.ExecutionRequested)
                issues.Add(new("unexpected-execution", rule.Name, "O motor iniciou execução sem sinal no replay."));
            if (live.OrderSent && !live.PositionOpened)
                issues.Add(new("order-without-position", rule.Name, "A ordem foi enviada, mas não há confirmação de posição aberta."));
            if (requireClosedPositions && live.PositionOpened && !live.PositionClosed)
                issues.Add(new("position-not-closed", rule.Name, "A posição abriu e não há confirmação estruturada de encerramento."));
            if (side != 0 && live.OrderSent && live.PositionOpened && (!requireClosedPositions || live.PositionClosed))
                completedSignals++;
        }

        return new(day, instrument, rules.Count, observed.Count, expectedSignals, observedSignals, completedSignals, issues);
    }

    /// <summary>
    /// Os dias do ADR congelado precisam ser exatamente os pregões anteriores do histórico, e as amplitudes só podem
    /// diferir por um fator único (ajuste do rollover). Arquivo velho ou dado errado aparece aqui.
    /// </summary>
    private static void CheckAdrDays(IReadOnlyList<DayRange> live, IReadOnlyList<DayRange> history, string instrument, List<ParityIssue> issues)
    {
        if (!live.Select(x => x.Day).SequenceEqual(history.Select(x => x.Day)))
        {
            issues.Add(new("adr-days-mismatch", instrument,
                $"ADR ao vivo usou {live.Count} pregões até {live.FirstOrDefault()?.Day:dd/MM}; histórico tem {history.Count} até {history.FirstOrDefault()?.Day:dd/MM}."));
            return;
        }
        var ratios = live.Zip(history, (l, h) => h.Range == 0 ? 0 : l.Range / h.Range).ToList();
        if (ratios.Count == 0 || ratios.Min() <= 0) return;
        if ((ratios.Max() - ratios.Min()) / ratios.Average() > 0.002m)
            issues.Add(new("adr-data-mismatch", instrument,
                $"Amplitudes do ADR ao vivo não batem com o histórico nem por ajuste de rollover (razão {ratios.Min():0.####}–{ratios.Max():0.####})."));
    }

    private static void Compare(string code, string setup, string label, decimal expected, decimal actual, List<ParityIssue> issues)
    {
        if (Math.Abs(expected - actual) > 0.001m)
            issues.Add(new(code, setup, $"{label}: replay={expected:0.###}; ao vivo={actual:0.###}."));
    }
}
