namespace TradeGuard.Domain;

/// <summary>Um negócio executado no MT5 (compra ou venda), como vem do histórico.</summary>
public sealed record DealRecord(
    ulong Ticket, long PositionId, string Symbol, DateTimeOffset TimeUtc, Side Side, int Volume,
    decimal Price, decimal Profit, string Entry);

/// <summary>
/// Remonta trades completos (da primeira entrada até zerar) a partir dos negócios do MT5.
/// Usado para completar o diário com trades feitos enquanto o TradeGuard estava fechado.
/// Posição ainda aberta (volume não voltou a zero) é ignorada.
/// </summary>
public static class TradeReconstructor
{
    public static IReadOnlyList<TradeRecord> FromDeals(IEnumerable<DealRecord> deals, string source)
    {
        var trades = new List<TradeRecord>();
        foreach (var group in deals.Where(d => d.PositionId != 0).GroupBy(d => d.PositionId))
        {
            var ordered = group.OrderBy(d => d.TimeUtc).ThenBy(d => d.Ticket).ToList();
            var first = ordered[0];
            if (first.Entry != "in") continue; // histórico começou no meio da posição

            var side = first.Side;
            var net = 0;
            foreach (var d in ordered) net += d.Side == side ? d.Volume : -d.Volume;
            if (net != 0) continue; // ainda aberta

            var ins = ordered.Where(d => d.Side == side).ToList();
            var outs = ordered.Where(d => d.Side != side).ToList();
            var inVolume = ins.Sum(d => d.Volume);
            var outVolume = outs.Sum(d => d.Volume);
            if (inVolume == 0 || outVolume == 0) continue;

            var entry = ins.Sum(d => d.Price * d.Volume) / inVolume;
            var exit = outs.Sum(d => d.Price * d.Volume) / outVolume;
            var points = (exit - entry) * side.Sign();
            var last = ordered[^1];

            trades.Add(new TradeRecord
            {
                PositionId = group.Key,
                TradingDay = DateOnly.FromDateTime(B3Clock.ToB3(last.TimeUtc).DateTime),
                Symbol = first.Symbol,
                Side = side,
                OpenedAtUtc = first.TimeUtc,
                ClosedAtUtc = last.TimeUtc,
                Contracts = inVolume,
                EntryPrice = Math.Round(entry, 3),
                ExitPrice = Math.Round(exit, 3),
                PnlMoney = ordered.Sum(d => d.Profit),
                PnlPoints = Math.Round(points, 2),
                Source = source,
                ExitReason = "importado do histórico do MT5"
            });
        }
        return trades.OrderBy(t => t.ClosedAtUtc).ToList();
    }
}
