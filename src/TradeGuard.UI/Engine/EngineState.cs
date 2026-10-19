using TradeGuard.Domain;

namespace TradeGuard.UI.Engine;

public sealed record ConnectionInfo(long Account, string Server, string Company, bool IsDemo, bool IsNetting, string Symbol);

public sealed record QuoteInfo(decimal Bid, decimal Ask, decimal Last, DateTimeOffset TimeUtc)
{
    public decimal Spread => Ask > 0 && Bid > 0 ? Ask - Bid : 0;
}

public sealed record PositionInfo(Side Side, int Contracts, decimal Entry, decimal Stop, decimal Target,
    bool BreakevenDone, decimal MfePoints, decimal MaePoints, decimal FloatingPnl, decimal EstimatedCosts,
    decimal InitialRiskMoney, string Source)
{
    public decimal EstimatedNetPnl => FloatingPnl - EstimatedCosts;
}

public sealed record FlowCapsInfo(int Trades30m, int WithAggressor, int BookDepth, bool TickHistory)
{
    public bool AggressorOk => Trades30m > 0 && WithAggressor * 10 >= Trades30m * 9;
}

/// <summary>Uma regra na agenda de hoje. State: "aguardando", "sinal", "sem sinal", "operou", "fora".</summary>
public sealed record ScheduleItem(string Rule, string Instrument, TimeOnly At, TimeOnly Exit, string State, string Detail,
    string Tooltip = "");

public sealed record LogLine(DateTimeOffset AtUtc, string Level, string Text);

/// <summary>Foto imutável do motor para a interface. Trocada inteira a cada mudança.</summary>
public sealed record EngineState
{
    public bool Connected { get; init; }
    public bool CanAct { get; init; }
    public ConnectionInfo? Connection { get; init; }
    public InstrumentSpec? Instrument { get; init; }
    public QuoteInfo? Quote { get; init; }
    public decimal Balance { get; init; }
    public decimal Equity { get; init; }
    public PositionInfo? Position { get; init; }
    public DateOnly TradingDay { get; init; }
    public decimal DayRealized { get; init; }
    public int DayTrades { get; init; }
    public int ConsecutiveLosses { get; init; }
    public LockReason Lock { get; init; }
    public decimal WeekPnl { get; init; }
    public decimal MonthPnl { get; init; }
    public IReadOnlyList<Block> Blocks { get; init; } = [];
    public IReadOnlyList<NewsEvent> UpcomingNews { get; init; } = [];
    public RiskSettings Settings { get; init; } = RiskSettings.Default;
    /// <summary>Mudança que afrouxa, salva e agendada para o próximo pregão.</summary>
    public TradeGuard.Infrastructure.PendingSettings? PendingSettings { get; init; }
    public string? ContractWarning { get; init; }
    public bool ChecklistDone { get; init; }
    /// <summary>MT5 logado e conectado à corretora.</summary>
    public bool BrokerConnected { get; init; } = true;
    /// <summary>Botão Algotrading do MT5 ligado (sem ele o robô não envia ordens).</summary>
    public bool AlgoAllowed { get; init; } = true;
    public int RejectedHandshakes { get; init; }
    /// <summary>Evidências objetivas necessárias antes de sequer permitir uma conta real.</summary>
    public OperationalReadiness Readiness { get; init; } = new(true, "aguardando", ParitySummary.Empty, 0, 0);
    /// <summary>Agenda do automático para este ativo hoje.</summary>
    public IReadOnlyList<ScheduleItem> Schedule { get; init; } = [];
    /// <summary>Tape reading agora (agressões do Times &amp; Trades e book).</summary>
    public FlowReading Flow { get; init; } = FlowReading.Empty;
    /// <summary>O que a corretora entrega para fluxo (diagnóstico do EA).</summary>
    public FlowCapsInfo? FlowCaps { get; init; }
    public DateTimeOffset? LastPrintUtc { get; init; }
    public int NextContracts { get; init; }
    /// <summary>Setups que já provaram resultado (liberados para o automático).</summary>
    public IReadOnlySet<string> ValidatedSetups { get; init; } = new HashSet<string>();
    /// <summary>Teste de cada estratégia de horário nos ~2 anos de candles (refeito todo dia).</summary>
    public IReadOnlyList<StudyResult> Studies { get; init; } = [];
    /// <summary>Leitura do mercado pela média lenta (mesma regra do gráfico e dos setups).</summary>
    public Trend Trend { get; init; }
    /// <summary>"O que o robô está esperando", etapa por etapa.</summary>
    public MarketReading Reading { get; init; } = MarketReading.Empty;
    /// <summary>Horário (UTC) do último candle recebido do MT5.</summary>
    public DateTimeOffset? LastCandleUtc { get; init; }
    /// <summary>Setups armados esperando o preço romper o gatilho.</summary>
    public IReadOnlyList<SetupCandidate> ArmedSetups { get; init; } = [];
    public PendingSignal? PendingSignal { get; init; }
    public IReadOnlyList<LogLine> Log { get; init; } = [];
}
