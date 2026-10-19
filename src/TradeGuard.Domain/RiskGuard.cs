namespace TradeGuard.Domain;

public enum BlockReason
{
    NotConnected,
    RealAccountNotAllowed,
    DayLocked,
    OutsideWindow,
    AfterFlatten,
    NewsBlackout,
    Cooldown,
    SpreadTooWide,
    ContractExpiring,
    PositionAlreadyOpen,
    ChecklistPending,
    BrokerOffline,
    AlgoTradingOff,
    PortfolioChannelUnhealthy,
    AuditStorageUnavailable,
    RealTradingNotQualified
}

public sealed record Block(BlockReason Reason, string Message);

public sealed record GuardContext(
    DateTimeOffset NowB3,
    RiskSettings Settings,
    DayLedger Day,
    decimal WeekPnl,
    decimal MonthPnl,
    IReadOnlyList<NewsEvent> News,
    decimal? SpreadPoints,
    bool Connected,
    bool IsDemoAccount,
    bool HasOpenPosition,
    InstrumentSpec? Instrument,
    bool ChecklistDone = true,
    bool BrokerConnected = true,
    bool AlgoAllowed = true,
    bool PortfolioHealthy = true,
    string PortfolioHealthMessage = "",
    bool AuditStorageHealthy = true,
    string AuditStorageMessage = "",
    bool RealTradingQualified = false);

/// <summary>
/// Regras que decidem se uma NOVA entrada é permitida e se o dia deve ser travado.
/// Puro: sem I/O, sem relógio próprio. Tudo que ele sabe vem do <see cref="GuardContext"/>.
/// </summary>
public static class RiskGuard
{
    public static IReadOnlyList<Block> EntryBlocks(GuardContext ctx)
    {
        var s = ctx.Settings;
        var blocks = new List<Block>();
        var time = TimeOnly.FromDateTime(ctx.NowB3.DateTime);

        if (!ctx.Connected)
            blocks.Add(new(BlockReason.NotConnected, "MT5 desconectado."));
        if (ctx.Connected && !ctx.BrokerConnected)
            blocks.Add(new(BlockReason.BrokerOffline, "MT5 sem conexão com a corretora (internet ou login)."));
        if (ctx.Connected && !ctx.AlgoAllowed)
            blocks.Add(new(BlockReason.AlgoTradingOff, "Algotrading desligado no MT5 (botão na barra de cima)."));
        if (!ctx.PortfolioHealthy)
            blocks.Add(new(BlockReason.PortfolioChannelUnhealthy,
                string.IsNullOrWhiteSpace(ctx.PortfolioHealthMessage) ? "Outro canal do portfólio não está saudável." : ctx.PortfolioHealthMessage));
        if (!ctx.AuditStorageHealthy)
            blocks.Add(new(BlockReason.AuditStorageUnavailable,
                string.IsNullOrWhiteSpace(ctx.AuditStorageMessage) ? "Armazenamento de auditoria indisponível." : ctx.AuditStorageMessage));
        if (ctx.Connected && !ctx.IsDemoAccount && !s.AllowRealAccount)
            blocks.Add(new(BlockReason.RealAccountNotAllowed, "Conta REAL bloqueada nas configurações. Só simulador."));
        if (ctx.Connected && !ctx.IsDemoAccount && !ctx.RealTradingQualified)
            blocks.Add(new(BlockReason.RealTradingNotQualified,
                "Conta REAL bloqueada: a validação operacional ainda não atingiu os critérios mínimos."));
        if (ctx.Day.IsLocked)
            blocks.Add(new(BlockReason.DayLocked, $"Dia travado: {ctx.Day.Lock.Describe()}."));
        if (!ctx.ChecklistDone)
            blocks.Add(new(BlockReason.ChecklistPending, "Faça o checklist do dia antes de operar."));
        if (ctx.HasOpenPosition)
            blocks.Add(new(BlockReason.PositionAlreadyOpen, "Já existe posição aberta."));

        if (time >= s.FlattenAt)
            blocks.Add(new(BlockReason.AfterFlatten, $"Depois de {s.FlattenAt:HH\\:mm} só zeragem."));
        else if (time < s.WindowStart || time >= s.WindowEnd)
            blocks.Add(new(BlockReason.OutsideWindow, $"Fora da janela {s.WindowStart:HH\\:mm}–{s.WindowEnd:HH\\:mm}."));

        if (s.CooldownAfterLossMinutes > 0 && ctx.Day.LastLossAt is { } lastLoss)
        {
            var until = lastLoss.AddMinutes(s.CooldownAfterLossMinutes);
            if (ctx.NowB3 < until)
                blocks.Add(new(BlockReason.Cooldown, $"Pausa após perda até {B3Clock.ToB3(until):HH\\:mm}."));
        }

        if (s.NewsFilterEnabled && ActiveNews(ctx) is { } news)
            blocks.Add(new(BlockReason.NewsBlackout,
                $"Notícia de alto impacto: {news.Currency} {news.Name} às {B3Clock.ToB3(news.TimeUtc):HH\\:mm}."));

        if (s.MaxSpreadPoints > 0 && ctx.SpreadPoints is { } spread && spread > s.MaxSpreadPoints)
            blocks.Add(new(BlockReason.SpreadTooWide, $"Spread {spread} pts acima do máximo {s.MaxSpreadPoints}."));

        if (ctx.Instrument?.Expiration is { } exp
            && DateOnly.FromDateTime(B3Clock.ToB3(exp).DateTime) <= DateOnly.FromDateTime(ctx.NowB3.DateTime))
            blocks.Add(new(BlockReason.ContractExpiring, $"{ctx.Instrument.Symbol} vence hoje. Troque para o próximo contrato."));

        return blocks;
    }

    /// <summary>Qual trava deve ser acionada agora, considerando também o resultado flutuante.</summary>
    public static LockReason LockToApply(GuardContext ctx, decimal floatingPnl)
    {
        var s = ctx.Settings;
        var day = ctx.Day.RealizedPnl + floatingPnl;

        if (s.MaxDailyLoss > 0 && day <= -s.MaxDailyLoss) return LockReason.DailyLoss;
        if (s.MaxWeeklyLoss > 0 && ctx.WeekPnl + floatingPnl <= -s.MaxWeeklyLoss) return LockReason.WeeklyLoss;
        if (s.MaxMonthlyLoss > 0 && ctx.MonthPnl + floatingPnl <= -s.MaxMonthlyLoss) return LockReason.MonthlyLoss;
        if (s.DailyProfitTarget > 0 && day >= s.DailyProfitTarget) return LockReason.DailyProfitTarget;

        // Proteção do lucro do dia: chegou a +R$ X e devolveu mais que N% do pico → para.
        var peak = Math.Max(ctx.Day.PeakPnl, day);
        if (s.GiveBackPercent > 0 && peak >= s.GiveBackActivationMoney && s.GiveBackActivationMoney > 0
            && day <= peak * (1 - s.GiveBackPercent / 100m))
            return LockReason.ProfitGiveBack;

        // Estas duas só fazem sentido com a posição zerada (contam trades fechados).
        if (floatingPnl == 0 && !ctx.HasOpenPosition)
        {
            if (s.MaxTradesPerDay > 0 && ctx.Day.TradesCount >= s.MaxTradesPerDay) return LockReason.MaxTrades;
            if (s.MaxConsecutiveLosses > 0 && ctx.Day.ConsecutiveLosses >= s.MaxConsecutiveLosses)
                return LockReason.ConsecutiveLosses;
        }
        return LockReason.None;
    }

    public static NewsEvent? ActiveNews(GuardContext ctx)
    {
        var s = ctx.Settings;
        var nowUtc = ctx.NowB3.ToUniversalTime();
        return ctx.News
            .Where(n => n.Importance >= s.NewsMinImportance)
            .Where(n => nowUtc >= n.TimeUtc.AddMinutes(-s.NewsBlockMinutesBefore)
                        && nowUtc <= n.TimeUtc.AddMinutes(s.NewsBlockMinutesAfter))
            .OrderBy(n => n.TimeUtc)
            .FirstOrDefault();
    }
}
