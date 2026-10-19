namespace TradeGuard.Domain;

public sealed record StudyPart(int Trades, decimal NetR, decimal ProfitFactor, decimal WinRate);

/// <summary>Resultado de uma estratégia de horário nos candles reais, em R (múltiplos do risco): calibragem (60% mais antigos) e validação (40% mais novos).</summary>
public sealed record StudyResult(string Setup, int Sessions, DateOnly From, DateOnly To, StudyPart Calibration, StudyPart Validation, decimal MaxDrawdownR)
{
    /// <summary>Aprovada: lucro nos dois períodos, fator de lucro ≥ 1,2 na validação e amostra ≥ 30 trades.</summary>
    public bool Validated => Calibration.NetR > 0 && Validation.NetR > 0 && Validation.ProfitFactor >= 1.2m && Validation.Trades >= 30;
}

/// <summary>
/// Amplitude média diária (ADR): média de máxima−mínima dos 20 pregões anteriores, a partir dos candles de 5 min
/// (horário de Brasília "gravado" como UTC, como o MT5 da Clear exporta).
/// </summary>
public static class DailyRange
{
    public static decimal Adr(IEnumerable<Candle> candlesB3Time, DateOnly before, int days = 20) =>
        Adr(Ranges(candlesB3Time, before, days), days);

    public static decimal Adr(IReadOnlyList<DayRange> ranges, int days = 20) =>
        ranges.Count < days / 2 ? 0 : ranges.Average(r => r.Range);

    /// <summary>Máxima e mínima de cada um dos <paramref name="days"/> pregões anteriores (mais recente primeiro).</summary>
    public static IReadOnlyList<DayRange> Ranges(IEnumerable<Candle> candlesB3Time, DateOnly before, int days = 20) =>
        candlesB3Time.Where(c => DateOnly.FromDateTime(B3Clock.ToB3(c.OpenUtc).DateTime) < before)
            .GroupBy(c => DateOnly.FromDateTime(B3Clock.ToB3(c.OpenUtc).DateTime)).Where(g => g.Count() >= 80)
            .OrderByDescending(g => g.Key).Take(days).Select(g => new DayRange(g.Key, g.Max(c => c.High), g.Min(c => c.Low))).ToList();
}

public sealed record DayRange(DateOnly Day, decimal High, decimal Low)
{
    public decimal Range => High - Low;
}

/// <summary>
/// Refaz o teste de uma <see cref="TimeRule"/> nos candles de 5 min da série contínua (~2 anos):
/// no horário, mede o movimento; entra na abertura do candle; stop pela ADR; sai no horário de saída. Custo por trade
/// incluso. Roda sozinho todo dia com os candles que o MT5 manda como monitor de deriva. A carteira é congelada e
/// versionada; o monitor não inclui nem remove regras automaticamente. Mesma conta da varredura que escolheu a carteira.
/// </summary>
public static class TimeRuleStudy
{
    public static StudyResult? Run(IReadOnlyList<Candle> candlesB3Time, TimeRule rule, decimal tick, decimal cost)
    {
        var days = candlesB3Time.GroupBy(c => B3Clock.ToB3(c.OpenUtc).Date)
            .Where(g => g.Count() >= 80 && g.Min(c => B3Clock.ToB3(c.OpenUtc).TimeOfDay) <= new TimeSpan(9, 5, 0))
            .Select(g => g.OrderBy(c => c.OpenUtc).ToList()).ToList();
        if (days.Count < 80) return null;
        var adr = new decimal[days.Count];
        for (var i = 20; i < days.Count; i++) adr[i] = days.Skip(i - 20).Take(20).Average(d => d.Max(c => c.High) - d.Min(c => c.Low));
        TimeOnly T(Candle c) => TimeOnly.FromDateTime(B3Clock.ToB3(c.OpenUtc).DateTime);

        var results = new List<(int Index, decimal R)>();
        for (var i = 20; i < days.Count; i++)
        {
            var d = days[i];
            var at = d.FindIndex(c => T(c) == rule.At);
            if (at < 1 || T(d[at - 1]) != rule.At.AddMinutes(-5)) continue;
            var side = rule.SideFor(d.Take(at).ToList(), adr[i], out _);
            if (side == 0) continue;
            var stop = rule.StopPoints(adr[i], tick);
            var entry = d[at].Open;
            var pts = (d[^1].Close - entry) * side;
            for (var k = at; k < d.Count; k++)
            {
                if (T(d[k]) >= rule.Exit) { pts = (d[k].Open - entry) * side; break; }
                if (side > 0 ? d[k].Low <= entry - stop : d[k].High >= entry + stop) { pts = -stop; break; }
            }
            results.Add((i, (pts - cost) / stop));
        }

        var split = 20 + (int)((days.Count - 20) * 0.6);
        decimal acc = 0, peak = 0, dd = 0;
        foreach (var r in results) { acc += r.R; peak = Math.Max(peak, acc); dd = Math.Max(dd, peak - acc); }
        return new StudyResult(rule.Name, days.Count - 20,
            DateOnly.FromDateTime(B3Clock.ToB3(days[20][0].OpenUtc).DateTime),
            DateOnly.FromDateTime(B3Clock.ToB3(days[^1][0].OpenUtc).DateTime),
            Part(results.Where(r => r.Index < split).Select(r => r.R)), Part(results.Where(r => r.Index >= split).Select(r => r.R)), dd);
    }

    private static StudyPart Part(IEnumerable<decimal> r)
    {
        var l = r.ToList();
        var gain = l.Where(x => x > 0).Sum();
        var loss = -l.Where(x => x < 0).Sum();
        return new StudyPart(l.Count, Math.Round(l.Sum(), 1), loss == 0 ? 99 : Math.Round(gain / loss, 2), l.Count == 0 ? 0 : Math.Round(100m * l.Count(x => x > 0) / l.Count, 0));
    }
}
