namespace TradeGuard.Domain;

public sealed record TradeStatistics(
    int Count,
    int Wins,
    int Losses,
    decimal WinRate,
    decimal AvgWin,
    decimal AvgLoss,
    decimal Payoff,
    decimal ProfitFactor,
    decimal Expectancy,
    decimal ExpectancyR,
    decimal NetPnl,
    decimal MaxDrawdown,
    decimal AvgMfePoints,
    decimal AvgMaePoints,
    IReadOnlyDictionary<int, decimal> PnlByHour,
    string Verdict)
{
    /// <summary>Abaixo disso qualquer estatística é ruído.</summary>
    public const int MinimumSample = 30;

    /// <summary>Amostra suficiente e resultado positivo: critério para liberar um setup no automático.</summary>
    public bool IsValidated => Count >= MinimumSample && Expectancy > 0 && ProfitFactor >= 1.2m;

    public static TradeStatistics From(IEnumerable<TradeRecord> trades)
    {
        var list = trades.Where(t => !t.Excluded).OrderBy(t => t.ClosedAtUtc).ToList();
        var wins = list.Where(t => t.PnlMoney > 0).ToList();
        var losses = list.Where(t => t.PnlMoney < 0).ToList();

        var grossWin = wins.Sum(t => t.PnlMoney);
        var grossLoss = -losses.Sum(t => t.PnlMoney);
        var avgWin = wins.Count == 0 ? 0 : grossWin / wins.Count;
        var avgLoss = losses.Count == 0 ? 0 : grossLoss / losses.Count;
        var net = list.Sum(t => t.PnlMoney);

        decimal peak = 0, cumulative = 0, maxDd = 0;
        foreach (var t in list)
        {
            cumulative += t.PnlMoney;
            peak = Math.Max(peak, cumulative);
            maxDd = Math.Max(maxDd, peak - cumulative);
        }

        var byHour = list
            .GroupBy(t => B3Clock.ToB3(t.OpenedAtUtc).Hour)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Sum(t => t.PnlMoney));

        var count = list.Count;
        var expectancy = count == 0 ? 0 : net / count;
        // R só existe com risco inicial conhecido (trades importados do MT5 não têm): não entram na média como 0R.
        var withRisk = list.Where(t => t.InitialRiskMoney > 0).ToList();
        var expectancyR = withRisk.Count == 0 ? 0 : withRisk.Average(t => t.RMultiple);
        var pf = grossLoss == 0 ? (grossWin > 0 ? decimal.MaxValue : 0) : grossWin / grossLoss;

        var verdict = count < MinimumSample
            ? $"Amostra insuficiente ({count}/{MinimumSample} trades). Nada aqui prova vantagem ainda."
            : expectancy > 0 && pf > 1.2m
                ? "Expectativa positiva na amostra. Continue medindo antes de aumentar tamanho."
                : "Sem vantagem na amostra. Não aumente tamanho; revise o método.";

        return new TradeStatistics(
            count, wins.Count, losses.Count,
            count == 0 ? 0 : Math.Round((decimal)wins.Count / count * 100, 1),
            Math.Round(avgWin, 2), Math.Round(avgLoss, 2),
            avgLoss == 0 ? (avgWin > 0 ? decimal.MaxValue : 0) : Math.Round(avgWin / avgLoss, 2), // sem perda: ∞
            pf == decimal.MaxValue ? pf : Math.Round(pf, 2),
            Math.Round(expectancy, 2), Math.Round(expectancyR, 2), net, maxDd,
            count == 0 ? 0 : Math.Round(list.Average(t => t.MfePoints), 1),
            count == 0 ? 0 : Math.Round(list.Average(t => t.MaePoints), 1),
            byHour, verdict);
    }
}
