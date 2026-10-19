namespace TradeGuard.Domain;

/// <summary>Um trade fechado, como fica no diário.</summary>
public sealed class TradeRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Identificador da posição no MT5: evita gravar o mesmo trade duas vezes.</summary>
    public long? PositionId { get; set; }
    public DateOnly TradingDay { get; set; }
    public string Symbol { get; set; } = "";
    public Side Side { get; set; }
    public DateTimeOffset OpenedAtUtc { get; set; }
    public DateTimeOffset ClosedAtUtc { get; set; }
    public int Contracts { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal ExitPrice { get; set; }
    public decimal InitialStop { get; set; }
    public decimal PnlMoney { get; set; }
    public decimal PnlPoints { get; set; }
    public decimal InitialRiskMoney { get; set; }
    public decimal MfePoints { get; set; }
    public decimal MaePoints { get; set; }
    public string Source { get; set; } = "";
    public string ExitReason { get; set; } = "";
    public string? Setup { get; set; }
    public string? Notes { get; set; }

    /// <summary>
    /// Fora das estatísticas (ex.: execução absurda do simulador no leilão).
    /// Prejuízo continua contando nas travas de perda: desconsiderar não é apagar.
    /// </summary>
    public bool Excluded { get; set; }

    /// <summary>Conta nas travas de perda e no limite de trades do dia?</summary>
    public bool CountsForLimits => !Excluded || PnlMoney < 0;

    /// <summary>Resultado em múltiplos do risco inicial (R). 1R = perdeu exatamente o stop planejado.</summary>
    public decimal RMultiple => InitialRiskMoney == 0 ? 0 : Math.Round(PnlMoney / InitialRiskMoney, 2);
}
