namespace TradeGuard.Domain;

public enum LockReason
{
    None,
    DailyLoss,
    DailyProfitTarget,
    WeeklyLoss,
    MonthlyLoss,
    MaxTrades,
    ConsecutiveLosses,
    ProfitGiveBack,
    Manual
}

public static class LockReasonText
{
    public static string Describe(this LockReason reason) => reason switch
    {
        LockReason.DailyLoss => "Perda máxima do dia atingida",
        LockReason.DailyProfitTarget => "Meta do dia atingida",
        LockReason.WeeklyLoss => "Perda máxima da semana atingida",
        LockReason.MonthlyLoss => "Perda máxima do mês atingida",
        LockReason.MaxTrades => "Número máximo de trades do dia",
        LockReason.ConsecutiveLosses => "Perdas seguidas demais",
        LockReason.ProfitGiveBack => "Devolveu lucro demais do dia",
        LockReason.Manual => "Botão de pânico / trava manual",
        _ => "Sem trava"
    };
}

/// <summary>Contabilidade do pregão. Reconstruída do diário se o app reiniciar no meio do dia.</summary>
public sealed class DayLedger(DateOnly tradingDay)
{
    public DateOnly TradingDay { get; } = tradingDay;
    public decimal RealizedPnl { get; private set; }
    /// <summary>Maior resultado realizado do dia (para a proteção de devolução de lucro).</summary>
    public decimal PeakPnl { get; private set; }
    public int TradesCount { get; private set; }
    public int ConsecutiveLosses { get; private set; }
    public DateTimeOffset? LastLossAt { get; private set; }
    public LockReason Lock { get; private set; } = LockReason.None;
    public DateTimeOffset? LockedAt { get; private set; }

    public bool IsLocked => Lock != LockReason.None;

    public void RegisterClosedTrade(decimal pnl, DateTimeOffset closedAt)
    {
        RealizedPnl += pnl;
        PeakPnl = Math.Max(PeakPnl, RealizedPnl);
        TradesCount++;
        if (pnl < 0)
        {
            ConsecutiveLosses++;
            LastLossAt = closedAt;
        }
        else if (pnl > 0)
        {
            ConsecutiveLosses = 0;
        }
    }

    /// <summary>A trava do dia é definitiva: uma vez travado, só destrava no próximo pregão.</summary>
    public bool TryLock(LockReason reason, DateTimeOffset at)
    {
        if (IsLocked || reason == LockReason.None) return false;
        Lock = reason;
        LockedAt = at;
        return true;
    }
}
