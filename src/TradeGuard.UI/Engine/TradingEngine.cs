using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradeGuard.Bridge;
using TradeGuard.Domain;
using TradeGuard.Infrastructure;

namespace TradeGuard.UI.Engine;

public enum UiAction
{
    Buy,
    Sell,
    CloseAll,
    Panic,
    ConfirmChecklist,
    ConfirmSignal,
    DismissSignal
}

public sealed record PendingSignal(EntrySignal Signal, DateTimeOffset AtUtc, string? ShadowSignalId = null);
internal sealed record ShadowPaperPosition(string SignalId, Side Side, decimal Entry, decimal StopPoints,
    decimal CostPoints, DateTimeOffset ExitAtUtc);

/// <summary>
/// Coração do robô assistente. Tudo (mensagens do MT5, cliques da interface, relógio) passa por
/// UMA fila e é processado em sequência, então não há concorrência entre regras.
/// </summary>
public sealed class TradingEngine(
    BridgeServer bridge,
    JournalStore journal,
    SettingsStore settingsStore,
    B3Clock clock,
    PortfolioRiskCoordinator portfolio,
    ParityAuditStore parity,
    ShadowLearningService shadowLearning,
    ILogger<TradingEngine> logger) : BackgroundService
{
    private static readonly TimeSpan SignalLifetime = TimeSpan.FromSeconds(60);
    private static readonly string ChecklistFile = Path.Combine(TradeGuardServices.DataDirectory, "checklist.txt");

    // Setups clássicos (9.1, Ponto Contínuo, Rompimento da abertura) nunca entram no automático.
    private readonly SignalEngine _signals = new();
    // Tape reading: Times & Trades (agressões) e book, ao vivo; gravados em dados\fluxo para o teste em ticks.
    private readonly FlowEngine _flow = new();
    private FlowRecorder? _flowRecorderLazy;
    private FlowRecorder _flowRecorder => _flowRecorderLazy ??= new(Path.Combine(TradeGuardServices.DataDirectory, IsPrimary ? "fluxo" : "fluxo-" + Label.ToLowerInvariant()));

    /// <summary>Motor principal (WDO): pede os históricos e é o que a tela mostra por padrão. O do WIN é secundário.</summary>
    public bool IsPrimary { get; init; } = true;
    /// <summary>Ativo deste motor (WDO, WIN).</summary>
    public string Label { get; init; } = "WDO";

    /// <summary>Um trade fechou em qualquer motor: os outros recarregam o dia (as travas de perda somam os dois ativos).</summary>
    public static event Action<TradingEngine>? AnyTradeClosed;
    private FlowCapsMessage? _flowCaps;
    private DateTimeOffset _lastPrintUtc;
    // Diferença entre o relógio do PC e o horário dos negócios (relógio da corretora + atraso do feed).
    // As janelas do fluxo (5 s, 30 s, 1 min) usam o relógio do mercado; senão ficariam vazias.
    private TimeSpan _flowSkew;
    private DateTimeOffset FlowNow => clock.UtcNow - _flowSkew;
    private DateTimeOffset _skewWindowStart;
    private TimeSpan _skewWindowMin = TimeSpan.MaxValue;
    private DateOnly _clockWarnedDay;

    /// <summary>
    /// O atraso real do feed fica abaixo de 1–2 s. Se o menor desvio em 1 min passar de 5 s, é o relógio do Windows
    /// que está errado (em 30/09 estava 29 s adiantado, sem sincronizar): horários de log, auditoria e saídas por
    /// horário ficam deslocados. Avisa uma vez por dia; não corrige o relógio (configuração do sistema).
    /// </summary>
    private void WarnClockSkew()
    {
        if (!IsPrimary) return;
        var now = clock.UtcNow;
        if (_skewWindowStart == default) _skewWindowStart = now;
        if (_flowSkew < _skewWindowMin) _skewWindowMin = _flowSkew;
        if (now - _skewWindowStart < TimeSpan.FromMinutes(1)) return;
        var skew = _skewWindowMin;
        _skewWindowStart = now;
        _skewWindowMin = TimeSpan.MaxValue;
        if (Math.Abs(skew.TotalSeconds) <= 5 || _clockWarnedDay == _day.TradingDay) return;
        _clockWarnedDay = _day.TradingDay;
        Log("warn", $"Relógio do Windows {Math.Abs(skew.TotalSeconds):0} s {(skew > TimeSpan.Zero ? "adiantado" : "atrasado")} em relação aos " +
                    "negócios da B3. Sincronize: Configurações → Hora e idioma → Data e hora → Sincronizar agora.");
    }
    private readonly List<Candle> _candles = [];
    private readonly List<Candle> _candleBuffer = [];
    private TimeSpan _barLength = TimeSpan.FromMinutes(5);
    private bool _rebuildPending;
    private string? _expectedSetup, _currentSetup;
    private string? _expectedShadowSignalId, _currentShadowSignalId;
    private string? _entryOrderId;
    private DateTimeOffset _entryOrderSentAt;
    private bool _entryOrderTimeoutAlerted;
    private long _currentPositionId;
    private IReadOnlySet<string> _validatedSetups = new HashSet<string>();
    private PendingSignal? _pendingSignal;
    private bool _scaleExpected;
    private readonly Dictionary<string, ShadowPaperPosition> _shadowPaper = new(StringComparer.Ordinal);
    // No Automático não há checklist: o robô opera sem depender de clique seu.
    private bool ChecklistOk => _checklistDay == _day.TradingDay || _settings.Mode == TradingMode.Auto;

    // O que aconteceu com cada regra hoje (para a agenda): estado e detalhe.
    private readonly Dictionary<string, (string State, string Detail, string Tooltip)> _ruleOutcome = [];
    private DateOnly _ruleOutcomeDay;

    private List<Candle> TodayBars() => [.. _candles.Where(c => DateOnly.FromDateTime(B3Clock.ToB3(c.OpenUtc).DateTime) == _day.TradingDay)];

    private void SetOutcome(string rule, string state, string detail, string? tooltip = null)
    {
        if (_ruleOutcomeDay != _day.TradingDay) { _ruleOutcome.Clear(); _ruleOutcomeDay = _day.TradingDay; }
        _ruleOutcome[rule] = (state, detail, tooltip ?? "");
    }

    private static string TradeTooltip(TradeRecord trade)
    {
        var opened = B3Clock.ToB3(trade.OpenedAtUtc);
        var closed = B3Clock.ToB3(trade.ClosedAtUtc);
        var duration = trade.ClosedAtUtc - trade.OpenedAtUtc;
        var outcome = trade.PnlMoney > 0 ? "LUCRO" : trade.PnlMoney < 0 ? "PREJUÍZO" : "ZERO";
        return $"{outcome}: R$ {trade.PnlMoney:+0.00;-0.00;0.00} · {trade.PnlPoints:+0.0;-0.0;0.0} pts · {trade.RMultiple:+0.0;-0.0;0.0}R\n" +
               $"{(trade.Side == Side.Buy ? "Compra" : "Venda")} · {trade.Contracts} contrato(s) · {opened:HH:mm}–{closed:HH:mm} ({duration.TotalMinutes:0} min)\n" +
               $"Entrada {trade.EntryPrice:0.0##} · stop inicial {trade.InitialStop:0.0##} · saída {trade.ExitPrice:0.0##}\n" +
               $"Máx. a favor +{trade.MfePoints:0.0} pts · contra -{trade.MaePoints:0.0} pts\n" +
               $"Saída: {trade.ExitReason} · origem: {trade.Source}";
    }

    private string AgendaTooltip(TimeRule rule, string state, string detail)
    {
        var limit = rule.MinMoveAdr * Adr;
        var stop = rule.StopPoints(Adr, _instrument?.TickSize ?? Specs(rule.Instrument).Tick);
        var behavior = rule.Follow ? "a favor do movimento" : "contra o movimento";
        var result = state == "sem sinal" ? "Nenhuma ordem · resultado R$ 0,00" : detail;
        return $"{rule.Name}\nEstado: {state} · {result}\n" +
               $"Avalia {rule.At:HH\\:mm} · saída limite {rule.Exit:HH\\:mm} · {behavior}\n" +
               $"ADR {Adr:0.0} pts · gatilho ±{limit:0.0} pts · stop {stop:0.0} pts";
    }

    /// <summary>O diário é a fonte da verdade depois de reiniciar: fato executado sempre vence o replay visual.</summary>
    private void RestoreRuleOutcomes(IEnumerable<TradeRecord> trades, DateOnly day)
    {
        _ruleOutcome.Clear();
        _ruleOutcomeDay = day;
        var restored = 0;
        foreach (var trade in trades.Where(t => SetupNames.TimeRuleOf(t.Setup)?.Instrument.Equals(Label, StringComparison.OrdinalIgnoreCase) == true)
                     .OrderBy(t => t.ClosedAtUtc))
        {
            var side = trade.Side == Side.Buy ? "compra" : "venda";
            var closed = B3Clock.ToB3(trade.ClosedAtUtc);
            _ruleOutcome[trade.Setup!] = ("operou",
                $"EXECUTADO: {side} {trade.Contracts} @ {trade.EntryPrice:0.0##} → {trade.ExitPrice:0.0##}; " +
                $"fechou {closed:HH:mm}; R$ {trade.PnlMoney:+0.00;-0.00} ({trade.RMultiple:0.0}R)",
                TradeTooltip(trade));
            restored++;
        }
        // Regras avaliadas sem trade: o que o robô decidiu no pregão (decisoes\) vale mais que refazer a conta agora.
        var decided = 0;
        foreach (var (setup, state, detail) in RecordedDecisions(day))
        {
            if (_ruleOutcome.ContainsKey(setup)) continue;
            _ruleOutcome[setup] = (state, detail, "");
            decided++;
        }
        if (restored > 0 || decided > 0)
            Log("info", $"Agenda restaurada: {restored} operação(ões) do diário e {decided} decisão(ões) gravada(s) de {Label}.");
    }

    /// <summary>Última decisão de cada regra deste ativo no arquivo dados\decisoes do dia.</summary>
    private IEnumerable<(string Setup, string State, string Detail)> RecordedDecisions(DateOnly day)
    {
        var path = Path.Combine(TradeGuardServices.DataDirectory, "decisoes", $"{day:yyyy-MM-dd}-{Label.ToLowerInvariant()}.csv");
        List<string[]> rows;
        try { rows = File.Exists(path) ? File.ReadLines(path).Skip(1).Select(l => l.Split(';')).Where(x => x.Length >= 13).ToList() : []; }
        catch { yield break; }
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var g in rows.GroupBy(x => x[3]))
        {
            if (SetupNames.TimeRuleOf(g.Key) is null) continue;
            var eval = g.LastOrDefault(x => x[4] == "avaliação");
            var exec = g.LastOrDefault(x => x[4] == "execução");
            if (exec is not null && exec[11] != "ordem enviada")
                yield return (g.Key, "fora", $"{exec[11]}: {exec[12]}");
            else if (exec is not null) // ordem enviada e trade ainda não fechado: posição em andamento
                yield return (g.Key, "operou", $"em operação desde {exec[1][..5]} · " +
                    (g.LastOrDefault(x => x[4] == "posição" && x[11] == "aberta")?[12] ?? "aguardando confirmação da posição"));
            else if (eval is not null && eval[11] == "sem sinal"
                     && decimal.TryParse(eval[7], System.Globalization.NumberStyles.Any, inv, out var mv)
                     && decimal.TryParse(eval[8], System.Globalization.NumberStyles.Any, inv, out var lim))
                yield return (g.Key, "sem sinal", $"movimento {mv:+0.#;-0.#}, precisava ±{lim:0.#}");
        }
    }

    /// <summary>Agenda do automático: cada regra deste ativo, com o que já aconteceu ou como está agora.</summary>
    private IReadOnlyList<ScheduleItem> BuildSchedule()
    {
        if (_instrument is null) return [];
        if (_ruleOutcomeDay != _day.TradingDay) { _ruleOutcome.Clear(); _ruleOutcomeDay = _day.TradingDay; }
        var bars = TodayBars();
        var now = clock.TimeOfDay;
        var items = new List<ScheduleItem>();
        foreach (var rule in SetupNames.TimeRules.Where(r => r.AppliesTo(_instrument.Symbol) && _settings.EnabledSetups.Contains(r.Name)))
        {
            var limit = rule.MinMoveAdr * Adr;
            if (_ruleOutcome.TryGetValue(rule.Name, out var o))
            {
                items.Add(new(rule.Name, rule.Instrument, rule.At, rule.Exit, o.State, o.Detail,
                    string.IsNullOrWhiteSpace(o.Tooltip) ? AgendaTooltip(rule, o.State, o.Detail) : o.Tooltip));
                continue;
            }
            if (now >= rule.At.AddMinutes(5) && (Adr <= 0 || bars.Count == 0))
            {
                // Sem decisão gravada e sem candles/ADR carregados ainda: não inventa movimento.
                items.Add(new(rule.Name, rule.Instrument, rule.At, rule.Exit, "aguardando", "carregando dados do dia…",
                    AgendaTooltip(rule, "aguardando", "carregando dados do dia…")));
                continue;
            }
            if (now >= rule.At.AddMinutes(5))
            {
                // Sem registro (app reaberto depois do horário): refaz a conta com os candles até o horário.
                var upTo = bars.Where(b => TimeOnly.FromDateTime(B3Clock.ToB3(b.OpenUtc).DateTime) <= rule.At.AddMinutes(-5)).ToList();
                var side = rule.SideFor(upTo, Adr, out var mv0);
                var state = side == 0 ? "sem sinal" : "não operou";
                var replayDetail = side == 0 ? $"movimento {mv0:+0.#;-0.#}, precisava ±{limit:0.#}"
                    : $"teria {(side > 0 ? "comprado" : "vendido")} (movimento {mv0:+0.#;-0.#}), sem registro de decisão do robô nesse horário (app fechado/reiniciando?)";
                items.Add(new(rule.Name, rule.Instrument, rule.At, rule.Exit, state, replayDetail,
                    AgendaTooltip(rule, state, replayDetail)));
                continue;
            }
            var m = rule.MeasureNow(bars);
            var what = rule.Kind == TimeRuleKind.AwayFromAverage ? "distância do preço médio" : "movimento";
            string detail;
            if (m is not { } mv || Adr <= 0) detail = "medindo…";
            else if (Math.Abs(mv) >= limit)
                detail = $"agora: {what} {mv:+0.#;-0.#} (limite ±{limit:0.#}) → {((Math.Sign(mv) * (rule.Follow ? 1 : -1)) > 0 ? "compraria" : "venderia")} se fosse agora";
            else
                detail = $"agora: {what} {mv:+0.#;-0.#} (precisa ±{limit:0.#}) → ainda não";
            items.Add(new(rule.Name, rule.Instrument, rule.At, rule.Exit, "aguardando", detail,
                AgendaTooltip(rule, "aguardando", detail)));
        }
        return items;
    }

    private IReadOnlyList<StudyResult> _studies = [];
    private DateOnly _studiesDay;
    private long _studiesRevision = -1;
    private DateTimeOffset _lastStudiesWarning;
    private DateOnly _historyRequestedDay;
    // Amplitude média diária (ADR 20 pregões) por ativo: base dos limiares e stops das estratégias de horário.
    private readonly Dictionary<string, decimal> _adr = [];
    private readonly Dictionary<string, IReadOnlyList<Candle>> _historyCandles = [];
    private DateTimeOffset _lastParityAudit;

    /// <summary>Ativo pelo símbolo: WDOV26 → WDO, WINV26 → WIN.</summary>
    private string InstrumentKey => _instrument?.Symbol is { Length: >= 3 } s ? s[..3].ToUpperInvariant() : "WDO";
    private decimal Adr => _adr.GetValueOrDefault(InstrumentKey);
    private static (decimal Tick, decimal Cost) Specs(string inst) => inst == "WIN" ? (5m, 10m) : (0.5m, 0.5m);

    private void StartShadowPaper(string signalId, EntrySignal signal, TickMessage tick)
    {
        if (SetupNames.TimeRuleOf(signal.Setup) is not { } rule || Adr <= 0) return;
        var entry = signal.Side == Side.Buy ? Nz(tick.Ask, tick.Last) : Nz(tick.Bid, tick.Last);
        if (entry <= 0) return;
        var spec = Specs(InstrumentKey);
        var localExit = _day.TradingDay.ToDateTime(rule.Exit, DateTimeKind.Unspecified);
        var exitAt = new DateTimeOffset(localExit, B3Clock.Zone.GetUtcOffset(localExit)).ToUniversalTime();
        _shadowPaper[signalId] = new ShadowPaperPosition(signalId, signal.Side, entry,
            rule.StopPoints(Adr, _instrument?.TickSize ?? spec.Tick), spec.Cost, exitAt);
    }

    private void UpdateShadowPaper(TickMessage tick)
    {
        if (_shadowPaper.Count == 0) return;
        foreach (var paper in _shadowPaper.Values.ToList())
        {
            var exit = paper.Side == Side.Buy ? Nz(tick.Bid, tick.Last) : Nz(tick.Ask, tick.Last);
            if (exit <= 0) continue;
            var adverse = (exit - paper.Entry) * paper.Side.Sign() <= -paper.StopPoints;
            if (!adverse && clock.UtcNow < paper.ExitAtUtc) continue;
            var grossPoints = adverse ? -paper.StopPoints : (exit - paper.Entry) * paper.Side.Sign();
            var r = Math.Round((grossPoints - paper.CostPoints) / paper.StopPoints, 4);
            shadowLearning.RecordOutcome(paper.SignalId, clock.UtcNow, Label,
                adverse ? "contrafactual_stop" : "contrafactual_saida_programada", 0, r, null,
                "Resultado simulado do sinal vetado; nenhuma ordem foi enviada.");
            _shadowPaper.Remove(paper.SignalId);
        }
    }

    /// <summary>
    /// Regras da posição por setup. As estratégias de horário foram testadas com stop pela ADR, sem alvo e sem
    /// break-even/parcial/trailing (deixam correr até o horário de saída); as regras gerais mudariam o resultado testado.
    /// </summary>
    private RiskSettings SettingsFor(string? setup)
    {
        if (SetupNames.TimeRuleOf(setup) is not { } rule || Adr <= 0) return _settings;
        var stop = rule.StopPoints(Adr, _instrument?.TickSize ?? Specs(InstrumentKey).Tick);
        return _settings with
        {
            StopPoints = stop, MaxStopPoints = Math.Max(_settings.MaxStopPoints, stop),
            TargetPoints = stop * 100, BreakevenEnabled = false, Partials = [], ProfitLocks = [], TrailingEnabled = false,
            MaxHoldMinutes = 0, ScaleMode = ScaleMode.Off
        };
    }

    private RiskSettings SettingsPreserving(PositionMessage position)
    {
        if (_instrument is null) return _settings;
        var stop = position.StopLoss > 0 ? Math.Abs(position.Price - position.StopLoss) : _settings.StopPoints;
        var target = position.TakeProfit > 0 ? Math.Abs(position.TakeProfit - position.Price) : stop * 100;
        return _settings with
        {
            StopPoints = stop, MaxStopPoints = Math.Max(stop, _settings.MaxStopPoints), TargetPoints = target,
            BreakevenEnabled = false, Partials = [], ProfitLocks = [], TrailingEnabled = false,
            MaxHoldMinutes = 0, ScaleMode = ScaleMode.Off
        };
    }

    private List<Candle> ReadBars(string file)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return File.ReadLines(file).Select(l => l.Split(';')).Where(p => p.Length >= 5)
            .Select(p => new Candle(DateTimeOffset.FromUnixTimeSeconds(long.Parse(p[0], inv) - _serverOffset), decimal.Parse(p[1], inv),
                decimal.Parse(p[2], inv), decimal.Parse(p[3], inv), decimal.Parse(p[4], inv))).ToList();
    }

    /// <summary>
    /// 1x por dia, com os ~2 anos de candles que o MT5 exportou: calcula a ADR de cada ativo e refaz o teste de cada
    /// estratégia. O resultado serve como monitor de deriva; a carteira versionada não muda sozinha.
    /// </summary>
    private async Task LoadStudiesAsync(DateOnly day)
    {
        var overrideDir = Environment.GetEnvironmentVariable("TRADEGUARD_HISTORY_DIR");
        var history = portfolio.HistoryStatus(day);
        // Harness isolado de E2E: os dois arquivos são a própria confirmação de exportação.
        if (!history.Ready && !string.IsNullOrWhiteSpace(overrideDir)
            && File.Exists(Path.Combine(overrideDir, "WDO_cont_M5.csv"))
            && File.Exists(Path.Combine(overrideDir, "WIN_cont_M5.csv")))
        {
            portfolio.BeginHistoryExport(day);
            portfolio.MarkHistoryExported(day, "WDO");
            portfolio.MarkHistoryExported(day, "WIN");
            history = portfolio.HistoryStatus(day);
        }
        if (!history.Ready)
        {
            StudiesNotReady("exportação diária de WDO e WIN ainda não terminou");
            return;
        }
        if (_studiesDay == day && _studiesRevision == history.Revision) return;
        if (!_serverClockKnown)
        {
            StudiesNotReady("relógio do servidor MT5 ainda não foi confirmado");
            return;
        }
        if (string.IsNullOrWhiteSpace(overrideDir) && TickHistory.Mt5Directory is not { })
        {
            StudiesNotReady("pasta do MT5 ainda não localizada");
            return;
        }
        var dir = !string.IsNullOrWhiteSpace(overrideDir)
            ? Path.GetFullPath(overrideDir)
            : Path.Combine(Path.GetDirectoryName(TickHistory.Mt5Directory!)!, "bars");
        var studies = new List<StudyResult>();
        var adrs = new Dictionary<string, decimal>();
        var histories = new Dictionary<string, IReadOnlyList<Candle>>();
        var adrNotes = new List<string>();
        foreach (var inst in new[] { "WDO", "WIN" })
        {
            var file = Path.Combine(dir, $"{inst}_cont_M5.csv");
            if (!File.Exists(file))
            {
                StudiesNotReady($"histórico {inst} ainda não foi exportado");
                return;
            }
            var (tick, cost) = Specs(inst);
            decimal adr;
            List<StudyResult> found;
            try
            {
                (adr, found) = await Task.Run(() =>
                {
                    var candles = ReadBars(file);
                    histories[inst] = candles;
                    // A série contínua do MT5 é reajustada no rollover: o ADR fica congelado no primeiro cálculo do dia
                    // (sobrevive a reinício e a reexportação) e a evidência vai para a paridade.
                    var ranges = DailyRange.Ranges(candles, day);
                    var frozen = DailyRange.Adr(ranges) > 0 ? AdrSnapshots.Freeze(inst, day, ranges) : ranges;
                    var current = DailyRange.Adr(ranges);
                    var used = DailyRange.Adr(frozen);
                    if (used != current)
                        adrNotes.Add($"{inst}: ADR mantido em {used:0.###} (congelado no início do dia); a série exportada agora daria " +
                                     $"{current:0.###} porque o MT5 reajustou o histórico contínuo.");
                    return (used,
                        SetupNames.TimeRules.Where(r => r.Instrument == inst).Select(r => TimeRuleStudy.Run(candles, r, tick, cost)).OfType<StudyResult>().ToList());
                });
            }
            catch (Exception ex)
            {
                StudiesNotReady($"histórico {inst} inválido: {ex.Message}");
                return;
            }
            if (adr <= 0 || found.Count != SetupNames.TimeRules.Count(r => r.Instrument == inst))
            {
                StudiesNotReady($"histórico {inst} insuficiente para ADR e reteste");
                return;
            }
            adrs[inst] = adr;
            studies.AddRange(found);
        }
        _adr.Clear();
        foreach (var item in adrs) _adr[item.Key] = item.Value;
        _historyCandles.Clear();
        foreach (var item in histories) _historyCandles[item.Key] = item.Value;
        _studies = studies;
        _studiesDay = day; // só fica pronto quando os dois ativos foram carregados e validados
        _studiesRevision = history.Revision;
        foreach (var item in adrs) Log("info", $"{item.Key}: amplitude média diária (20 pregões) = {item.Value:0.#} pts.");
        foreach (var note in adrNotes) Log("info", note);
        foreach (var r in _studies)
            Log(r.Validated ? "info" : "warn",
                $"{r.Setup} retestada em {r.Sessions} pregões: calibragem {r.Calibration.NetR:+0.0;-0.0}R (PF {r.Calibration.ProfitFactor}), " +
                $"validação {r.Validation.NetR:+0.0;-0.0}R (PF {r.Validation.ProfitFactor}, {r.Validation.Trades} trades). " +
                (r.Validated ? "Monitoramento histórico saudável."
                    : "ALERTA DE DERIVA: fora dos critérios históricos; revisar a carteira, sem alteração automática."));
        if (IsPrimary)
        {
            parity.AuditAll(_historyCandles, day, clock.TimeOfDay);
            Log(parity.Summary.Issues == 0 ? "info" : "warn", "Paridade replay × ao vivo: " + parity.Summary.Message + ".");
        }
    }

    private void StudiesNotReady(string reason)
    {
        if (clock.UtcNow - _lastStudiesWarning < TimeSpan.FromSeconds(30)) return;
        _lastStudiesWarning = clock.UtcNow;
        Log("warn", $"Regras de horário bloqueadas: {reason}. Vou tentar novamente automaticamente.");
    }

    // Aumento de posição só com o fluxo confirmando: a favor = agressão a favor agora; preço médio = absorção a favor.
    private bool ScaleLiquidity(Side side, ScaleMode mode)
    {
        var f = _flow.Read(FlowNow);
        if (!f.HasData) return false;
        var ours = side == Side.Buy ? FlowBias.Buyers : FlowBias.Sellers;
        return mode == ScaleMode.WithTrend ? f.Bias == ours && f.Delta5s * side.Sign() > 0 : f.Absorption == ours;
    }

    private abstract record Input;
    private sealed record FromBridge(BridgeEvent Event) : Input;
    private sealed record FromUi(UiAction Action, TaskCompletionSource<string> Done) : Input;
    private sealed record Tick : Input;
    private sealed record ReloadSettings : Input;
    private sealed record External(string Level, string Text) : Input;
    private sealed record PortfolioLock(DateOnly Day, LockReason Reason, DateTimeOffset AtUtc, string Origin) : Input;

    private readonly Channel<Input> _inputs = Channel.CreateUnbounded<Input>(new() { SingleReader = true });
    private readonly List<LogLine> _log = [];
    private readonly List<NewsEvent> _newsBuffer = [];
    private readonly Dictionary<string, string> _pending = [];

    private RiskSettings _settings = RiskSettings.Default;
    private PendingSettings? _pendingSettings;
    private DayLedger _day = new(DateOnly.MinValue);
    private decimal _weekBase, _monthBase;
    private HelloMessage? _hello;
    private InstrumentSpec? _instrument;
    private long _serverOffset;
    private bool _serverClockKnown;
    private QuoteInfo? _quote;
    private decimal _balance, _equity;
    private PositionMessage? _brokerPos;
    private ManagedPosition? _managed;
    private string? _expectedSource;
    private List<NewsEvent> _news = [];
    private DateTimeOffset _lastModifyAt, _lastHeartbeat, _lastFlattenAttempt, _lastEnforceAt;
    private DateTimeOffset _lastTickReceivedAt;
    private readonly List<DealMessage> _positionDeals = [];
    private readonly List<DealMessage> _historyBuffer = [];
    private DateTimeOffset? _awaitingExitSince;
    private readonly MinuteBarBuilder _bars = new();
    private DateOnly? _checklistDay;

    public EngineState State { get; private set; } = new();

    public event Action? Changed;

    /// <summary>Eventos importantes para notificação do Windows (título, texto).</summary>
    public event Action<string, string>? Alerted;

    /// <summary>Sinal de entrada aguardando confirmação (som, piscar a janela etc.).</summary>
    public event Action<PendingSignal>? SignalRaised;

    /// <summary>Mensagens de outros serviços (ex.: orquestrador do MT5) no registro do painel.</summary>
    public void Report(string level, string text) => _inputs.Writer.TryWrite(new External(level, text));

    /// <summary>Notificação do Windows disparada por outros serviços.</summary>
    public void RaiseAlert(string title, string text) => Alert(title, text);

    public Task<string> RequestAsync(UiAction action)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _inputs.Writer.TryWrite(new FromUi(action, tcs));
        return tcs.Task;
    }

    /// <summary>
    /// Teste dos avisos (tela Verificação): mesmo som/piscar de um sinal real, notificação do Windows
    /// e push do MT5 no celular. Não abre ordem nem mexe em nada.
    /// </summary>
    public async Task<string> TestAlertsAsync()
    {
        SignalRaised?.Invoke(new PendingSignal(new EntrySignal(Side.Buy, "Teste", "Teste de aviso"), clock.UtcNow));
        Alert("Teste de aviso do TradeGuard", "Se você ouviu o som e está vendo este aviso, os alertas funcionam.");
        var push = bridge.IsConnected && await bridge.SendAsync(new NotifyCommand("TradeGuard: teste de notificacao no celular"));
        return push
            ? "Som, aviso do Windows e push do MT5 enviados. Confira o celular (só chega se o MetaQuotes ID estiver configurado no MT5)."
            : "Som e aviso do Windows enviados. Push do celular não enviado: MT5 desconectado.";
    }

    /// <summary>Recarrega configurações depois de salvar na tela.</summary>
    public void Poke() => _inputs.Writer.TryWrite(new ReloadSettings());

    private void OnPortfolioLockRequested(DateOnly day, LockReason reason, DateTimeOffset atUtc, string origin)
    {
        if (!origin.Equals(Label, StringComparison.OrdinalIgnoreCase))
            _inputs.Writer.TryWrite(new PortfolioLock(day, reason, atUtc, origin));
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await journal.EnsureCreatedAsync();
        if (IsPrimary)
        {
            AuditStorage.Probe();
            StorageMaintenance.CompactOldFlowFiles(clock.UtcNow);
        }
        AnyTradeClosed += other => { if (other != this) Poke(); };
        portfolio.LockRequested += OnPortfolioLockRequested;
        await StartDayAsync(clock.TradingDay);
        Log("info", "Motor iniciado. Aguardando o EA do MT5 conectar…");

        _ = bridge.RunAsync(ct);
        _ = PumpBridgeAsync(ct);
        _ = PumpTimerAsync(ct);

        await foreach (var input in _inputs.Reader.ReadAllAsync(ct))
        {
            try
            {
                switch (input)
                {
                    case FromBridge b: await HandleBridgeAsync(b.Event); break;
                    case FromUi u: u.Done.TrySetResult(await HandleUiAsync(u.Action)); break;
                    case Tick: await HandleTimerAsync(); break;
                    case External x: Log(x.Level, x.Text); break;
                    case PortfolioLock p when p.Day == _day.TradingDay:
                        await ApplyPortfolioLockAsync(p.Reason, p.AtUtc, persist: false,
                            $"trava agregada disparada pelo {p.Origin}");
                        break;
                    case ReloadSettings:
                        // Regras ou diário mudaram: recalcula o dia a partir do banco.
                        await StartDayAsync(_day.TradingDay);
                        Log("info", "Regras e diário recarregados.");
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro processando {Input}", input);
                Log("error", "Erro interno: " + ex.Message);
                if (input is FromUi u) u.Done.TrySetResult("Erro: " + ex.Message);
            }
            Publish();
        }
    }

    private async Task PumpBridgeAsync(CancellationToken ct)
    {
        await foreach (var ev in bridge.Events.ReadAllAsync(ct))
            await _inputs.Writer.WriteAsync(new FromBridge(ev), ct);
    }

    private async Task PumpTimerAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct))
            await _inputs.Writer.WriteAsync(new Tick(), ct);
    }

    // ------------------------------------------------------------------ dia / configurações

    private async Task StartDayAsync(DateOnly day)
    {
        try
        {
            if (IsPrimary && await journal.BackupAsync(day) is { } file) logger.LogInformation("Backup do diário: {File}", file);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Backup do diário falhou");
        }

        _settings = await settingsStore.EffectiveAsync(day);
        portfolio.StartDay(Label, day);

        // Uma vez só: o robô passa a operar sozinho a carteira experimental em candles reais.
        // Liga as 11 estratégias de horário, tira as reprovadas (9.1, Ponto Contínuo, Rompimento) e pede o modo Automático
        // (afrouxar = vale no próximo pregão; só no simulador). Tudo reversível em Regras.
        // Carteira v2 (WDO + WIN, stops pela ADR) no automático, dimensionada para a meta de R$ 5 mil/mês no simulador:
        // R$ 300 de risco por trade; travas iguais às do teste (perda máx. 4R/dia; sem trava de perdas seguidas, sem pausa
        // após perda e sem filtro de notícias, que não fizeram parte do teste). Pedido do dono: vale já.
        var marker = Path.Combine(TradeGuardServices.DataDirectory, "carteira-automatica-v2.txt");
        if (IsPrimary && !File.Exists(marker))
        {
            var desired = await settingsStore.DesiredAsync(day);
            var result = await settingsStore.SaveAsync(desired with
            {
                EnabledSetups = [.. SetupNames.TimeRules.Select(r => r.Name)],
                Mode = TradingMode.Auto,
                RiskPerTradeMoney = 300, Contracts = 1, MaxContracts = 6,
                // Perdas seguidas, pausa e notícias não fizeram parte do backtest da carteira; ficam desligados para
                // o ao vivo reproduzir exatamente o que foi testado.
                MaxDailyLoss = 1200, MaxConsecutiveLosses = 0, MaxTradesPerDay = 12,
                MaxWeeklyLoss = 3000, MaxMonthlyLoss = 6000, GiveBackPercent = 0, DailyProfitTarget = 0,
                CooldownAfterLossMinutes = 0, NewsFilterEnabled = false, FlowFilterEnabled = false
            }, day, applyNow: true);
            _settings = await settingsStore.EffectiveAsync(day);
            Log("info", result.Errors.Count > 0
                ? "Não consegui aplicar a carteira automática: " + string.Join(" ", result.Errors)
                : $"Carteira automática v2 ligada ({SetupNames.TimeRules.Count} estratégias WDO + WIN), risco R$ 300 por trade, perda máx. R$ 1.200/dia. Valendo já (simulador).");
            if (result.Errors.Count == 0) await File.WriteAllTextAsync(marker, day.ToString("yyyy-MM-dd"));
        }
        _pendingSettings = await settingsStore.PendingAsync(day);
        _day = new DayLedger(day);
        _checklistDay = File.Exists(ChecklistFile)
                        && DateOnly.TryParse(await File.ReadAllTextAsync(ChecklistFile), System.Globalization.CultureInfo.InvariantCulture, out var done)
                        && done == day
            ? day
            : null;

        var todayTrades = (await journal.TradesAsync(day, day)).ToList();
        RestoreRuleOutcomes(todayTrades, day);
        foreach (var t in todayTrades.Where(t => t.CountsForLimits))
            _day.RegisterClosedTrade(t.PnlMoney, t.ClosedAtUtc);

        var weekStart = B3Clock.WeekStart(day);
        var monthStart = B3Clock.MonthStart(day);
        var from = weekStart < monthStart ? weekStart : monthStart;
        var previous = (await journal.TradesAsync(from, day.AddDays(-1))).Where(t => t.CountsForLimits).ToList();
        _weekBase = previous.Where(t => t.TradingDay >= weekStart).Sum(t => t.PnlMoney);

        // A carteira é versionada no código: o reteste diário monitora deriva, mas não liga/desliga regras sozinho.
        // Alterar a carteira olhando a janela móvel recriaria o ajuste excessivo que a validação tenta evitar.
        _validatedSetups = new HashSet<string>();
        if (_studiesDay != day)
        {
            _adr.Clear();
            _studies = [];
        }
        await RequestDailyHistoryAsync(day);
        await LoadStudiesAsync(day);
        if (_studiesDay == day)
            _validatedSetups = _settings.EnabledSetups.Where(s => SetupNames.TimeRuleOf(s) is not null).ToHashSet();
        _monthBase = previous.Where(t => t.TradingDay >= monthStart).Sum(t => t.PnlMoney);

        if (await journal.DayLockAsync(day) is { } saved)
        {
            // Trava por contagem/lucro que só existiu por um trade depois desconsiderado (ex.: importação absurda):
            // não vale mais. Trava por perda ou manual é definitiva.
            // Perdas seguidas também: se a trava foi desligada nas regras (carteira testada sem ela), ela não vale mais.
            var fromTrades = saved.Reason is LockReason.MaxTrades or LockReason.DailyProfitTarget or LockReason.ProfitGiveBack
                             || (saved.Reason == LockReason.ConsecutiveLosses && _settings.MaxConsecutiveLosses == 0);
            if (fromTrades && RiskGuard.LockToApply(Context() with { HasOpenPosition = false }, 0) == LockReason.None)
            {
                await journal.DeleteDayLockAsync(day);
                portfolio.ClearLock(day);
                Log("info", $"Trava \"{saved.Reason.Describe()}\" desfeita: sem os trades desconsiderados, o limite não foi atingido.");
            }
            else
            {
                _day.TryLock(saved.Reason, saved.LockedAtUtc);
                portfolio.RestoreLock(day, saved.Reason);
            }
        }
    }

    // Spread máximo está em pontos de WDO (tick 0,5): no WIN (tick 5) vale o mesmo número de ticks.
    private RiskSettings GuardSettings => _instrument is { TickSize: > 0 } i && i.TickSize != 0.5m
        ? _settings with { MaxSpreadPoints = _settings.MaxSpreadPoints * i.TickSize / 0.5m }
        : _settings;

    private GuardContext Context()
    {
        var health = portfolio.Health(_day.TradingDay, clock.UtcNow, TimeSpan.FromSeconds(10));
        return new GuardContext(
            clock.Now, GuardSettings, _day,
            _weekBase + _day.RealizedPnl, _monthBase + _day.RealizedPnl,
            _news, _quote?.Spread is > 0 ? _quote.Spread : null,
            bridge.IsConnected, _hello?.IsDemo ?? false,
            _brokerPos is { IsFlat: false }, _instrument,
            ChecklistOk,
            _brokerConnected, _algoAllowed,
            health.Healthy, health.Message,
            AuditStorage.Healthy, AuditStorage.Message,
            parity.Readiness.Qualified);
    }

    private bool CanAct => bridge.IsConnected && _hello is not null
        && (_hello.IsDemo || (_settings.AllowRealAccount && parity.Readiness.Qualified));

    // ------------------------------------------------------------------ MT5

    private async Task HandleBridgeAsync(BridgeEvent ev)
    {
        switch (ev)
        {
            case BridgeConnected c: await OnConnectedAsync(c.Hello); break;
            case BridgeFailed f:
                Log("error", f.Reason);
                break;
            case BridgeDisconnected d:
                Log("warn", $"MT5 desconectado ({d.Reason}). O stop continua na corretora.");
                if (IsPrimary) _historyRequestedDay = default;
                portfolio.UpdateHealth(Label, _day.TradingDay, connected: false, _brokerConnected);
                if (_brokerPos is { IsFlat: false }) Alert("MT5 desconectado", "Há posição aberta. O stop continua na corretora; o TradeGuard está reconectando.");
                await journal.AuditAsync("warn", "bridge", "desconectado: " + d.Reason);
                break;
            case BridgeInbound { Message: var m }:
                switch (m)
                {
                    case TickMessage t: await OnTickAsync(t); break;
                    case PositionMessage p: await OnPositionAsync(p); break;
                    case DealMessage d: await OnDealAsync(d); break;
                    case HistoryDealMessage h: _historyBuffer.Add(h.Deal); break;
                    case HistoryEndMessage: await ImportHistoryAsync(); break;
                    case BarMessage b: OnBar(b); break;
                    case BarsEndMessage: OnBarsLoaded(); break;
                    case AccountMessage a:
                        _balance = a.Balance; _equity = a.Equity;
                        TrackTerminalStatus(a.BrokerConnected, a.AlgoAllowed);
                        portfolio.UpdateHealth(Label, _day.TradingDay, bridge.IsConnected, a.BrokerConnected,
                            _lastTickReceivedAt == default ? null : _lastTickReceivedAt);
                        if (a.GmtTime != 0) { _serverOffset = a.ServerTime - a.GmtTime; _serverClockKnown = true; }
                        break;
                    case AckMessage a: await OnAckAsync(a); break;
                    case NewsMessage n:
                        _newsBuffer.Add(new NewsEvent(ServerToUtc(n.Time), n.Currency, n.Importance, n.Name));
                        break;
                    case NewsEndMessage:
                        _news = [.. _newsBuffer];
                        _newsBuffer.Clear();
                        break;
                    case LogMessage l:
                        if (l.Text.StartsWith("Historico de candles exportado:", StringComparison.Ordinal))
                        {
                            if (l.Text.Contains("WDO$", StringComparison.Ordinal))
                                portfolio.MarkHistoryExported(_day.TradingDay, "WDO");
                            else if (l.Text.Contains("WIN$", StringComparison.Ordinal))
                                portfolio.MarkHistoryExported(_day.TradingDay, "WIN");
                        }
                        Log(l.Level, "[MT5] " + l.Text);
                        break;
                    case TradesMessage tm:
                    {
                        // Horário do servidor do MT5 → UTC.
                        var prints = tm.Prints.Select(p => p with { TimeMsc = p.TimeMsc - _serverOffset * 1000 }).ToList();
                        _flow.Add(prints);
                        _flowRecorder.Trades(prints);
                        if (prints.Count > 0)
                        {
                            _lastPrintUtc = prints[^1].TimeUtc;
                            _flowSkew = clock.UtcNow - _lastPrintUtc;
                            WarnClockSkew();
                        }
                        break;
                    }
                    case BookMessage bm:
                        _flow.SetBook(bm.Bids, bm.Asks);
                        _flowRecorder.Book(clock.UtcNow, bm.Bids, bm.Asks);
                        break;
                    case FlowCapsMessage fc:
                        _flowCaps = fc;
                        Log(fc.Trades > 0 && fc.WithSide * 10 >= fc.Trades * 9 ? "info" : "warn",
                            $"Fluxo (tape reading): {fc.Trades} negócios nos últimos 30 min, {fc.WithSide} com lado agressor, " +
                            $"book com {Math.Max(0, fc.BookDepth)} níveis, histórico de ticks {(fc.History > 0 ? "disponível" : "não veio")}.");
                        break;
                }
                break;
        }
    }

    private async Task OnConnectedAsync(HelloMessage h)
    {
        _hello = h;
        Mt5.Mt5AccountMemory.Save(h.Account, h.Server);
        _serverOffset = h.ServerOffsetSeconds;
        _serverClockKnown = true;
        _instrument = new InstrumentSpec(h.Symbol, h.TickSize, h.TickValue,
            h.ExpirationServer > 0 ? ServerToUtc(h.ExpirationServer) : null);
        parity.ObserveContract(Label, h.Symbol, clock.UtcNow);
        _pending.Clear();
        portfolio.UpdateHealth(Label, _day.TradingDay, connected: true, _brokerConnected,
            _lastTickReceivedAt == default ? null : _lastTickReceivedAt);

        var kind = h.IsDemo ? "SIMULADOR" : "CONTA REAL";
        Log("info", $"Conectado: {kind} {h.Account} @ {h.Server} — {h.Symbol} (1 ponto = R$ {_instrument.PointValue:0.##})");
        if (!h.IsNetting) Log("warn", "Conta não é 'netting'. O robô foi feito para netting (B3).");
        if (!h.IsDemo && !_settings.AllowRealAccount)
            Log("warn", "Conta REAL detectada e não autorizada: o robô só observa, não envia nenhuma ordem.");
        await journal.AuditAsync("info", "bridge", $"conectado {kind} {h.Account} {h.Symbol}");
        TickHistory.ServerOffsetSeconds = _serverOffset;
        if (IsPrimary)
        {
            await bridge.SendAsync(new TickHistoryCommand(20)); // negócios dos últimos 20 pregões (só baixa os que faltam)
            await RequestDailyHistoryAsync(_day.TradingDay);
        }
    }

    private async Task RequestDailyHistoryAsync(DateOnly day)
    {
        if (!IsPrimary || !bridge.IsConnected || _historyRequestedDay == day) return;
        _historyRequestedDay = day;
        portfolio.BeginHistoryExport(day);
        _validatedSetups = new HashSet<string>();
        await bridge.SendAsync(new BarHistoryCommand("WDO$", 3000));
        await bridge.SendAsync(new BarHistoryCommand("WIN$", 3000));
        await bridge.SendAsync(new NewsHistoryCommand(3000));
    }

    private async Task OnTickAsync(TickMessage t)
    {
        _quote = new QuoteInfo(t.Bid, t.Ask, t.Last, ServerMsToUtc(t.TimeMsc));
        _lastTickReceivedAt = clock.UtcNow;
        portfolio.UpdateHealth(Label, _day.TradingDay, bridge.IsConnected, _brokerConnected, _lastTickReceivedAt);
        _bars.Add(_quote.TimeUtc, Nz(t.Last, (t.Bid + t.Ask) / 2));
        UpdateShadowPaper(t);
        if (_rebuildPending && _candles.Count > 0 && t.Bid > 0)
        {
            _rebuildPending = false;
            _signals.Rebuild(_candles, clock.UtcNow, SignalOptions(), t.Bid, t.Ask);
            Log("info", _signals.Armed.Count == 0
                ? "Leitura refeita: nenhum setup armado no momento."
                : "Leitura refeita, setups rearmados: " + string.Join(" · ", _signals.Armed.Select(a =>
                    $"{a.Setup} {(a.Side == Side.Buy ? "compra acima de" : "venda abaixo de")} {a.Trigger:0.0} (até {B3Clock.ToB3(a.ExpiresUtc):HH:mm})")));
        }
        if (_managed is null)
        {
            portfolio.UpdatePosition(Label, _day.TradingDay, 0, false);
            await EvaluateSignalsAsync(t);
            return;
        }
        if (_awaitingExitSince is not null) return; // já zerou; só falta o negócio de saída chegar

        // Preço de saída: comprado sai no bid, vendido sai no ask.
        var price = _managed.Side == Side.Buy ? Nz(t.Bid, t.Last) : Nz(t.Ask, t.Last);
        if (price == 0) return;

        foreach (var action in _managed.OnPrice(price, clock.UtcNow, _bars.Previous, ScaleLiquidity))
            await ExecuteAsync(action);

        var totalFloating = portfolio.UpdatePosition(Label, _day.TradingDay, _managed.FloatingPnl(price), true);
        var lockReason = RiskGuard.LockToApply(Context(), totalFloating);
        if (lockReason != LockReason.None)
            await RequestPortfolioLockAsync(lockReason);
    }

    private async Task OnPositionAsync(PositionMessage p)
    {
        var previous = _brokerPos;
        _brokerPos = p;

        if (p.IsFlat)
        {
            portfolio.UpdatePosition(Label, _day.TradingDay, 0, false);
            if (_managed is null)
            {
                if (_entryOrderId is not null && clock.UtcNow - _entryOrderSentAt > TimeSpan.FromSeconds(15))
                {
                    Log("warn", "MT5 confirmou que continua zerado; liberando a ordem de entrada que ficou sem confirmação.");
                    if (_expectedShadowSignalId is { } shadowId)
                        shadowLearning.RecordDisposition(shadowId, clock.UtcNow, Label, "ordem_sem_confirmacao",
                            "MT5 confirmou que a conta permaneceu zerada");
                    _entryOrderId = null;
                    _expectedSource = null;
                    _expectedSetup = null;
                    _expectedShadowSignalId = null;
                    PositionContextStore.Clear(Label);
                }
                return;
            }
            // O negócio de saída pode chegar logo depois do "zerado": espera por ele antes de fechar o trade.
            if (_positionDeals.Any(d => d.Entry is "out" or "inout")) await FinalizeTradeAsync();
            else _awaitingExitSince ??= clock.UtcNow;
            return;
        }

        if (_awaitingExitSince is not null)
        {
            _awaitingExitSince = null;
            await FinalizeTradeAsync();
        }

        var side = p.AsSide!.Value;
        if (_managed is not null && _managed.Side != side)
        {
            await FinalizeTradeAsync(); // virada de mão: fecha o trade anterior e começa outro
            previous = null;
        }

        if (_managed is null)
        {
            await OnNewPositionAsync(p, side);
            return;
        }

        // Aumentou a posição (preço médio)?
        var robotAdd = _scaleExpected && previous is { IsFlat: false } && p.Volume > previous.Volume;
        if (robotAdd) _scaleExpected = false;
        if (!robotAdd && !_settings.AllowScaleIn && previous is { IsFlat: false } && p.Volume > previous.Volume)
        {
            await SendAsync(new ReduceCommand(NewId(), p.Volume - previous.Volume, "aumento de posicao proibido"),
                "Aumentar posição é proibido nas regras — volume extra zerado.");
            return;
        }

        var modifyRecently = clock.UtcNow - _lastModifyAt < TimeSpan.FromSeconds(3);
        var reimpose = _managed.SyncFromBroker(p.Volume, p.Price, modifyRecently ? _managed.Stop : p.StopLoss, p.TakeProfit);
        if (reimpose is { } stop && !modifyRecently)
            await SendModifyAsync(stop, _managed.Target, p.StopLoss == 0
                ? "Posição sem stop — stop recolocado."
                : $"Stop afrouxado no MT5 ({p.StopLoss:0.0##}) — revertido para {stop:0.0##}.");
    }

    private async Task OnNewPositionAsync(PositionMessage p, Side side)
    {
        if (_instrument is null) return;
        var persisted = PositionContextStore.Load(Label);
        var persistedMatches = persisted is not null
                               && persisted.Side == side
                               && persisted.Symbol.Equals(_instrument.Symbol, StringComparison.OrdinalIgnoreCase)
                               && (p.StopLoss == 0 || Math.Abs(p.StopLoss - persisted.StopLoss) <= _instrument.TickSize)
                               && (persisted.PositionId == 0 || p.PositionId == 0 || persisted.PositionId == p.PositionId);
        var setup = _expectedSetup ?? (persistedMatches ? persisted!.Setup : null) ?? SetupNames.SetupFromTradeTag(p.Comment);
        var robotPosition = _expectedSource is not null || persistedMatches
                            || p.Comment.StartsWith("TG|", StringComparison.OrdinalIgnoreCase)
                            || p.Comment.Equals("TradeGuard", StringComparison.OrdinalIgnoreCase);
        var source = _expectedSource ?? (persistedMatches ? persisted!.Source : null)
                     ?? (robotPosition ? "TradeGuard recuperado após reinício" : "MT5 manual");
        var shadowSignalId = _expectedShadowSignalId ?? (persistedMatches ? persisted!.ShadowSignalId : null);
        _expectedSource = null;
        _currentSetup = setup;
        _expectedSetup = null;
        _currentShadowSignalId = shadowSignalId;
        _expectedShadowSignalId = null;
        _entryOrderId = null;
        _entryOrderTimeoutAlerted = false;
        _currentPositionId = p.PositionId;

        _managed = new ManagedPosition(side, p.Price, p.Volume, p.StopLoss, p.TakeProfit,
            _currentSetup is not null ? SettingsFor(_currentSetup) : robotPosition ? SettingsPreserving(p) : _settings,
            _instrument, p.OpenedAtServer > 0 ? ServerToUtc(p.OpenedAtServer) : clock.UtcNow, source);
        portfolio.UpdatePosition(Label, _day.TradingDay, p.Profit, true);
        if (robotPosition)
        {
            if (!PositionContextStore.Save(new PersistedPositionContext(Label, _instrument.Symbol, side, _currentSetup,
                    source, _managed.Stop, _managed.Target, persisted?.CreatedAtUtc ?? clock.UtcNow, p.PositionId,
                    _currentShadowSignalId)))
            {
                Log("error", "Não consegui persistir o contexto da posição. A posição segue protegida, mas um novo reinício perderia a regra.");
                Alert("Falha ao salvar posição", $"{Label}: não reinicie o app enquanto a posição estiver aberta.");
            }
        }
        Log("info", $"Posição detectada: {Describe(side)} {p.Volume} @ {p.Price:0.0##} ({source}).");
        DecisionRecorder.Write(Label, _day.TradingDay, clock.UtcNow, _currentSetup ?? source, "posição", side.Sign(),
            Adr, null, null, _quote?.Bid, _quote?.Ask, "aberta", $"{p.Volume} @ {p.Price}; stop {p.StopLoss}; pid {p.PositionId}");
        if (_currentShadowSignalId is { } openedShadowId)
            shadowLearning.RecordDisposition(openedShadowId, clock.UtcNow, Label, "posicao_aberta", null, p.PositionId);

        if (!CanAct) return;

        if (_day.IsLocked)
        {
            await SendAsync(new CloseAllCommand(NewId(), "dia já travado"),
                "Posição detectada com o dia travado — zerando.");
            return;
        }

        // Entrada manual pelo MT5 também passa pelas regras. Se não podia, zera na hora.
        if (source == "MT5 manual")
        {
            var blocks = RiskGuard.EntryBlocks(Context() with { HasOpenPosition = false });
            if (blocks.Count > 0)
            {
                await SendAsync(new CloseAllCommand(NewId(), "entrada fora das regras"),
                    "Entrada BLOQUEADA e zerada: " + string.Join(" ", blocks.Select(b => b.Message)));
                return;
            }
        }

        if (p.Volume > _settings.MaxContracts)
            await SendAsync(new ReduceCommand(NewId(), p.Volume - _settings.MaxContracts, "acima do maximo de contratos"),
                $"Volume {p.Volume} acima do máximo {_settings.MaxContracts} — excesso zerado.");

        if (p.StopLoss != _managed.Stop || p.TakeProfit != _managed.Target)
            await SendModifyAsync(_managed.Stop, _managed.Target,
                $"Stop {_managed.Stop:0.0##} / alvo {_managed.Target:0.0##} aplicados (risco R$ {_managed.InitialRiskMoney:0.00}).");
    }

    private async Task OnDealAsync(DealMessage d)
    {
        if (d.Entry is not ("in" or "out" or "inout")) return;
        if (_currentPositionId != 0 && d.PositionId != 0 && d.PositionId != _currentPositionId) return;
        if (_positionDeals.All(x => x.Ticket != d.Ticket)) _positionDeals.Add(d);
        if (d.PositionId != 0) _currentPositionId = d.PositionId;
        if (d.Entry is "out" or "inout" && _awaitingExitSince is not null && _brokerPos is { IsFlat: true })
        {
            _awaitingExitSince = null;
            await FinalizeTradeAsync();
        }
    }

    /// <summary>
    /// Completa o diário com trades feitos com o TradeGuard fechado (ou zerados durante uma desconexão).
    /// Só grava posições que ainda não estão no diário.
    /// </summary>
    private async Task ImportHistoryAsync()
    {
        var deals = _historyBuffer
            .Where(d => d.PositionId != 0 && d.Side is "buy" or "sell")
            .Select(d => new DealRecord(d.Ticket, d.PositionId, d.Symbol, ServerToUtc(d.Time),
                d.Side == "buy" ? Side.Buy : Side.Sell, d.Volume, d.Price, d.Profit, d.Entry))
            .ToList();
        _historyBuffer.Clear();

        var imported = await journal.ImportAsync(TradeReconstructor.FromDeals(deals, "MT5 (TradeGuard fechado)"));
        if (imported.Count == 0) return;

        var total = imported.Sum(t => t.PnlMoney);
        Log("info", $"Diário completado com {imported.Count} trade(s) feito(s) fora do TradeGuard (R$ {total:+0.00;-0.00}).");
        await journal.AuditAsync("info", "import", $"{imported.Count} trades importados do histórico do MT5");

        // Trades de hoje contam para as travas do dia.
        if (imported.Any(t => t.TradingDay >= B3Clock.WeekStart(_day.TradingDay) || t.TradingDay >= B3Clock.MonthStart(_day.TradingDay)))
        {
            await StartDayAsync(_day.TradingDay);
            var lockReason = RiskGuard.LockToApply(Context() with { HasOpenPosition = false }, 0);
            if (lockReason != LockReason.None && _brokerPos is not { IsFlat: false }) await RequestPortfolioLockAsync(lockReason);
        }
    }

    private async Task OnAckAsync(AckMessage a)
    {
        _pending.Remove(a.Id, out var what);
        if (a.Id == _entryOrderId && !a.Ok)
        {
            if (_expectedShadowSignalId is { } shadowId)
                shadowLearning.RecordDisposition(shadowId, clock.UtcNow, Label, "ordem_recusada",
                    $"{a.RetCode}: {a.Text}");
            _entryOrderId = null;
            _expectedSource = null;
            _expectedSetup = null;
            _expectedShadowSignalId = null;
            PositionContextStore.Clear(Label);
        }
        if (!a.Ok)
        {
            Log("error", $"MT5 recusou '{what ?? a.Id}': {a.Text} (código {a.RetCode}).");
            await journal.AuditAsync("error", "order", $"recusada {what}: {a.RetCode} {a.Text}");
        }
    }

    private async Task FinalizeTradeAsync()
    {
        var m = _managed!;
        _managed = null;
        if (_instrument is null) return;

        var deals = _currentPositionId == 0
            ? _positionDeals.ToList()
            : _positionDeals.Where(d => d.PositionId == 0 || d.PositionId == _currentPositionId).ToList();
        var exits = deals.Where(d => d.Entry is "out" or "inout").ToList();
        if (exits.Count == 0)
        {
            // Negócio de saída não chegou (MT5 caiu ou zerou com o TradeGuard desconectado): o resultado exato
            // vem do histórico do MT5, que o EA envia ao reconectar (sem estimativa, sem duplicar).
            Log("info", "Posição zerada sem o negócio de saída; o resultado será lido do histórico do MT5.");
            _positionDeals.Clear();
            _currentPositionId = 0;
            _currentSetup = null;
            if (_currentShadowSignalId is { } unresolvedShadowId)
                shadowLearning.RecordDisposition(unresolvedShadowId, clock.UtcNow, Label, "resultado_pendente_historico",
                    "negócio de saída será reconciliado pelo histórico do MT5");
            _currentShadowSignalId = null;
            PositionContextStore.Clear(Label);
            portfolio.UpdatePosition(Label, _day.TradingDay, 0, false);
            return;
        }

        var setup = _currentSetup;
        var shadowSignalId = _currentShadowSignalId;
        var pnl = deals.Sum(d => d.Profit); // inclui comissão/taxa tanto da entrada quanto da saída
        var vol = exits.Sum(d => d.Volume);
        var exit = vol == 0 ? 0 : exits.Sum(d => d.Price * d.Volume) / vol;
        var reason = exits.LastOrDefault()?.Comment is { Length: > 0 } c ? c : "";
        var positionId = _currentPositionId != 0 ? _currentPositionId : exits.LastOrDefault()?.PositionId;
        _positionDeals.Clear();
        _currentPositionId = 0;
        _currentSetup = null;
        _currentShadowSignalId = null;
        PositionContextStore.Clear(Label);
        portfolio.UpdatePosition(Label, _day.TradingDay, 0, false);

        var record = new TradeRecord
        {
            TradingDay = _day.TradingDay,
            Symbol = _instrument.Symbol,
            Side = m.Side,
            OpenedAtUtc = m.OpenedAtUtc,
            ClosedAtUtc = clock.UtcNow,
            Contracts = m.MaxContracts,
            EntryPrice = m.EntryPrice,
            ExitPrice = Math.Round(exit, 3),
            InitialStop = m.InitialStop,
            PnlMoney = pnl,
            PnlPoints = Math.Round((exit - m.EntryPrice) * m.Side.Sign(), 2),
            InitialRiskMoney = m.InitialRiskMoney,
            MfePoints = m.MfePoints,
            MaePoints = m.MaePoints,
            Source = m.Source,
            ExitReason = reason,
            PositionId = positionId is > 0 ? positionId : null,
            Setup = setup,
            Excluded = setup == ExecutionTestSetup // teste de execução não entra nas estatísticas
        };
        await journal.AddTradeAsync(record);
        ShadowReviewNotice? shadowNotice = null;
        if (shadowSignalId is not null)
            shadowNotice = shadowLearning.RecordOutcome(shadowSignalId, record.ClosedAtUtc, Label, "trade_fechado",
                record.PnlMoney, record.RMultiple, record.PositionId, record.ExitReason);
        _day.RegisterClosedTrade(pnl, record.ClosedAtUtc);
        try { AnyTradeClosed?.Invoke(this); } catch { /* aviso entre motores nunca derruba o fechamento */ }
        Log(pnl >= 0 ? "info" : "warn",
            $"Trade fechado: {Describe(m.Side)} {record.PnlPoints:+0.0;-0.0} pts = R$ {pnl:+0.00;-0.00} ({record.RMultiple:0.0}R). Dia: R$ {_day.RealizedPnl:0.00}.");
        DecisionRecorder.Write(Label, _day.TradingDay, record.ClosedAtUtc, setup ?? m.Source, "posição", m.Side.Sign(),
            Adr, null, null, _quote?.Bid, _quote?.Ask, "fechada", $"R$ {pnl}; {record.RMultiple}R; {reason}");
        if (SetupNames.TimeRuleOf(setup) is not null)
            SetOutcome(setup!, "operou",
                $"EXECUTADO: {(m.Side == Side.Buy ? "compra" : "venda")} {record.Contracts} @ {record.EntryPrice:0.0##} → {record.ExitPrice:0.0##}; " +
                $"fechou {B3Clock.ToB3(record.ClosedAtUtc):HH:mm}; R$ {pnl:+0.00;-0.00} ({record.RMultiple:0.0}R)",
                TradeTooltip(record));
        Alert(pnl >= 0 ? "Trade no lucro" : "Trade no prejuízo",
            $"{record.PnlPoints:+0.0;-0.0} pts = R$ {pnl:+0.00;-0.00}. Dia: R$ {_day.RealizedPnl:0.00}.");
        if (shadowNotice is not null)
        {
            Log("info", shadowNotice.Detail);
            Alert(shadowNotice.Title, shadowNotice.Detail);
        }

        if (bridge.IsConnected)
        {
            await bridge.SendAsync(new NotifyCommand(
                $"TradeGuard: {record.PnlPoints:+0.0;-0.0} pts = R$ {pnl:+0.00;-0.00}. Dia: R$ {_day.RealizedPnl:0.00}"));
            if (shadowNotice is not null)
                await bridge.SendAsync(new NotifyCommand("TradeGuard: filtro probabilistico aprovado; piloto demo sera ativado automaticamente."));
        }

        var lockReason = RiskGuard.LockToApply(Context() with { HasOpenPosition = false }, 0);
        if (lockReason != LockReason.None) await RequestPortfolioLockAsync(lockReason);
    }

    // ------------------------------------------------------------------ ações

    private async Task ExecuteAsync(ManagementAction action)
    {
        switch (action)
        {
            case MoveStop ms:
                await SendModifyAsync(ms.NewStop, _managed!.Target, $"{ms.Reason}: stop → {ms.NewStop:0.0##}.");
                break;
            case ClosePartial cp:
                await SendAsync(new ReduceCommand(NewId(), cp.Contracts, "parcial"), $"{cp.Reason}: {cp.Contracts} contrato(s).");
                break;
            case CloseAll ca:
                await SendAsync(new CloseAllCommand(NewId(), ca.Reason), ca.Reason);
                break;
            case AddContracts add:
                _scaleExpected = true;
                _lastModifyAt = clock.UtcNow; // o stop novo vai junto na ordem
                if (!await SendAsync(new OpenCommand(NewId(), _managed!.Side, add.Contracts, add.NewStop, _managed.Target, "TradeGuard aumento"), add.Reason))
                    _scaleExpected = false;
                break;
        }
    }

    private async Task SendModifyAsync(decimal stop, decimal target, string description)
    {
        _lastModifyAt = clock.UtcNow;
        await SendAsync(new ModifyCommand(NewId(), stop, target), description);
    }

    private async Task<bool> SendAsync(BridgeCommand cmd, string description)
    {
        if (!CanAct)
        {
            Log("warn", "Não enviado (robô sem permissão de agir): " + description);
            return false;
        }
        if (!await bridge.SendAsync(cmd))
        {
            Log("error", "Falha ao enviar ao MT5: " + description);
            return false;
        }
        if (cmd.Id.Length > 0) _pending[cmd.Id] = description;
        Log("info", description);
        await journal.AuditAsync("info", cmd.Type, description);
        return true;
    }

    private async Task RequestPortfolioLockAsync(LockReason reason)
    {
        var at = clock.UtcNow;
        if (portfolio.TryRequestLock(_day.TradingDay, reason, at, Label))
            await ApplyPortfolioLockAsync(reason, at, persist: true, "trava detectada neste motor");
    }

    private async Task ApplyPortfolioLockAsync(LockReason reason, DateTimeOffset atUtc, bool persist, string origin)
    {
        var newlyLocked = _day.TryLock(reason, atUtc);
        if (persist) await journal.SaveDayLockAsync(_day.TradingDay, reason, atUtc);
        if (newlyLocked)
        {
            Log("warn", $"TRAVA DO PORTFÓLIO: {reason.Describe()} ({origin}). Nenhuma nova entrada até o próximo pregão.");
            Alert("Dia encerrado", reason.Describe() + ".");
        }
        if (_brokerPos is { IsFlat: false })
            await SendAsync(new CloseAllCommand(NewId(), "trava do dia"), "Zerando posição por trava do dia.");
        if (persist && bridge.IsConnected)
            await bridge.SendAsync(new NotifyCommand($"TradeGuard: dia travado - {reason.Describe()}"));
    }

    // ------------------------------------------------------------------ entradas

    /// <summary>Único caminho de entrada: clique, sinal confirmado ou automático passam pelas mesmas travas.</summary>
    private async Task<string> OpenAsync(Side side, string source, string? setup = null, string? shadowSignalId = null)
    {
        string Reject(string message)
        {
            if (shadowSignalId is not null)
                shadowLearning.RecordDisposition(shadowSignalId, clock.UtcNow, Label, "entrada_nao_enviada", message);
            return message;
        }

        if (_entryOrderId is not null) return Reject("Já existe uma ordem de entrada aguardando confirmação do MT5.");
        var blocks = RiskGuard.EntryBlocks(Context());
        if (blocks.Count > 0) return Reject("Bloqueado: " + string.Join(" ", blocks.Select(b => b.Message)));
        if (!CanAct) return Reject("Robô sem permissão de agir nesta conta.");
        if (_instrument is null || _quote is null) return Reject("Sem cotação ainda.");

        var entry = side == Side.Buy ? Nz(_quote.Ask, _quote.Last) : Nz(_quote.Bid, _quote.Last);
        var sign = side.Sign();
        var ps = SettingsFor(setup);
        var sl = _instrument.RoundStopTowardsEntry(entry - sign * ps.StopPoints, side);
        var tp = _instrument.RoundToTick(entry + sign * ps.TargetPoints);
        var contracts = ps.ContractsFor(_instrument); // pelo stop da estratégia (risco em R$ fixo por trade)
        if (contracts < 1)
            return Reject($"Risco por trade (R$ {ps.RiskPerTradeMoney}) não paga 1 contrato com stop de {ps.StopPoints} pts.");
        if (_hello?.IsDemo == false)
        {
            // Primeira fase real é deliberadamente limitada a um contrato e 0,5% do patrimônio por trade.
            contracts = Math.Min(contracts, 1);
            var oneContractRisk = _instrument.Money(Math.Abs(entry - sl), 1);
            if (_equity <= 0 || oneContractRisk > _equity * 0.005m)
                return Reject($"Piloto real bloqueado: 1 contrato arriscaria R$ {oneContractRisk:0.00}, acima de 0,5% do patrimônio.");
        }

        _expectedSource = source;
        _expectedSetup = setup;
        _expectedShadowSignalId = shadowSignalId;
        var id = NewId();
        _entryOrderId = id;
        _entryOrderSentAt = clock.UtcNow;
        _entryOrderTimeoutAlerted = false;
        if (!PositionContextStore.Save(new PersistedPositionContext(Label, _instrument.Symbol, side, setup, source,
                sl, tp, clock.UtcNow, ShadowSignalId: shadowSignalId)))
        {
            _entryOrderId = null;
            _expectedSource = null;
            _expectedSetup = null;
            _expectedShadowSignalId = null;
            return Reject("Entrada bloqueada: não consegui gravar o contexto de recuperação da posição.");
        }
        var comment = setup == ExecutionTestSetup ? "TG|TEST" : SetupNames.TradeTag(setup);
        var ok = await SendAsync(new OpenCommand(id, side, contracts, sl, tp, comment),
            $"{Describe(side)} {contracts} a mercado (~{entry:0.0##}), stop {sl:0.0##}, alvo {tp:0.0##} · {source}.");
        if (!ok)
        {
            _entryOrderId = null;
            _expectedSource = null;
            _expectedSetup = null;
            _expectedShadowSignalId = null;
            PositionContextStore.Clear(Label);
        }
        if (shadowSignalId is not null)
            shadowLearning.RecordDisposition(shadowSignalId, clock.UtcNow, Label,
                ok ? "ordem_enviada" : "falha_ao_enviar_ordem", ok ? null : "ponte não aceitou o envio");
        return ok ? "Ordem enviada." : "Falha ao enviar.";
    }

    // ------------------------------------------------------------------ setups e sinais

    private SignalEngine.Options SignalOptions() => new(
        _settings.EnabledSetups, _settings.TargetPoints, _settings.MaxStopPoints,
        _instrument?.TickSize is > 0 ? _instrument.TickSize : 0.5m, _barLength, Adr, _instrument?.Symbol ?? "");

    private void OnBar(BarMessage b)
    {
        _barLength = TimeSpan.FromSeconds(b.Seconds > 0 ? b.Seconds : 300);
        var candle = new Candle(ServerToUtc(b.Time), b.Open, b.High, b.Low, b.Close);
        if (b.IsHistory)
        {
            _candleBuffer.Add(candle);
            return;
        }
        if (_candles.Count > 0 && candle.OpenUtc <= _candles[^1].OpenUtc) return;
        _candles.Add(candle);
        if (_candles.Count > 600) _candles.RemoveRange(0, _candles.Count - 600);
        ArmSetups();
    }

    private void OnBarsLoaded()
    {
        _candles.Clear();
        _candles.AddRange(_candleBuffer.OrderBy(c => c.OpenUtc).DistinctBy(c => c.OpenUtc));
        _candleBuffer.Clear();
        Log("info", $"{_candles.Count} candles de {_barLength.TotalMinutes:0} min carregados; setups em observação: {string.Join(", ", _settings.EnabledSetups)}.");
        _rebuildPending = true; // rearma com a primeira cotação (precisa do preço atual)
        SaveCandles();
    }

    /// <summary>
    /// Guarda os candles do dia em dados\candles\AAAA-MM-DD.csv (horário de Brasília) com as médias e a tendência
    /// que o robô enxergou. Serve para conferir e calibrar os setups com o que aconteceu de verdade.
    /// </summary>
    private void SaveCandles()
    {
        try
        {
            var dir = Path.Combine(TradeGuardServices.DataDirectory, IsPrimary ? "candles" : "candles-" + Label.ToLowerInvariant());
            Directory.CreateDirectory(dir);
            var ema9 = Indicators.Ema(_candles, 9);
            var sma21 = Indicators.Sma(_candles, 21);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder("hora;abertura;maxima;minima;fechamento;mme9;mm21;tendencia\n");
            var all = new System.Text.StringBuilder("data;hora;abertura;maxima;minima;fechamento\n");
            foreach (var c in _candles)
            {
                var t = B3Clock.ToB3(c.OpenUtc);
                all.Append(string.Join(';', t.ToString("yyyy-MM-dd"), t.ToString("HH:mm"), c.Open.ToString(inv), c.High.ToString(inv),
                    c.Low.ToString(inv), c.Close.ToString(inv))).Append('\n');
            }
            File.WriteAllText(Path.Combine(dir, "recentes.csv"), all.ToString()); // ~3 pregões, para testar calibragens
            if (IsPrimary) CandleArchive.Merge(_candles);
            for (var i = 0; i < _candles.Count; i++)
            {
                var c = _candles[i];
                var t = B3Clock.ToB3(c.OpenUtc);
                if (DateOnly.FromDateTime(t.DateTime) != _day.TradingDay) continue;
                var trend = i >= 25 ? Indicators.TrendOf(_candles.Take(i + 1).ToList()) : Trend.Sideways;
                sb.Append(string.Join(';', t.ToString("HH:mm"), c.Open.ToString(inv), c.High.ToString(inv), c.Low.ToString(inv),
                    c.Close.ToString(inv), Math.Round(ema9[i], 2).ToString(inv), Math.Round(sma21[i], 2).ToString(inv), trend)).Append('\n');
            }
            File.WriteAllText(Path.Combine(dir, $"{_day.TradingDay:yyyy-MM-dd}.csv"), sb.ToString());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Não consegui gravar os candles do dia");
            AuditStorage.MarkFailure(ex);
        }
    }

    private void ArmSetups()
    {
        SaveCandles();
        if (_instrument is not null)
        {
            var bars = TodayBars();
            foreach (var rule in SetupNames.TimeRules.Where(r => r.AppliesTo(_instrument.Symbol) && _settings.EnabledSetups.Contains(r.Name)))
            {
                if (bars.Count == 0 || TimeOnly.FromDateTime(B3Clock.ToB3(bars[^1].OpenUtc).DateTime) != rule.At.AddMinutes(-5)) continue;
                var side = rule.SideFor(bars, Adr, out var move);
                var limit = rule.MinMoveAdr * Adr;
                SetOutcome(rule.Name, side == 0 ? "sem sinal" : "sinal",
                    side == 0 ? $"movimento {move:+0.#;-0.#}, precisava ±{limit:0.#}: não entrou"
                               : $"movimento {move:+0.#;-0.#} (limite ±{limit:0.#}) → {(side > 0 ? "compra" : "venda")}");
                DecisionRecorder.Write(Label, _day.TradingDay, clock.UtcNow, rule.Name, "avaliação", side, Adr,
                    move, limit, _quote?.Bid, _quote?.Ask, side == 0 ? "sem sinal" : "sinal", "candle anterior fechado");
                // Toda avaliação vai para o registro: dá para ver que o robô olhou, mesmo quando não entra.
                if (side == 0) Log("info", $"{rule.At:HH\\:mm} {rule.Name}: sem sinal (movimento {move:+0.#;-0.#}, precisava ±{limit:0.#}).");
            }
        }
        foreach (var c in _signals.OnBarClosed(_candles, clock.UtcNow, SignalOptions()))
        {
            // Regra de horário com posição/ordem já ativa neste ativo: como na pesquisa (t <= busy), não entra.
            // Decide agora: se a posição fechasse dentro dos 5 min de validade, entraria atrasada e fora do testado.
            if (SetupNames.TimeRuleOf(c.Setup) is not null && (_managed is not null || _entryOrderId is not null || _pendingSignal is not null))
            {
                _signals.Disarm(c);
                const string why = "já havia posição aberta neste ativo (uma por vez)";
                DecisionRecorder.Write(Label, _day.TradingDay, clock.UtcNow, c.Setup, "execução", c.Side.Sign(), Adr, null, null,
                    _quote?.Bid, _quote?.Ask, "bloqueada", why);
                SetOutcome(c.Setup, "fora", "bloqueado: " + why);
                Log("info", $"Sinal {c.Setup} ({Describe(c.Side)}) não entra: {why}. {c.Reason}");
                continue;
            }
            Log("info", $"Em observação · {c.Setup} ({Describe(c.Side)}): {c.Reason}");
        }
        LogExpired();
    }

    /// <summary>Explica cada setup que expirou sem disparar: quão perto o preço chegou do gatilho.</summary>
    private void LogExpired()
    {
        foreach (var (c, closest) in _signals.TakeExpired())
        {
            // Regra de horário não tem gatilho de preço (entra a mercado): só explica que a janela passou.
            if (SetupNames.TimeRuleOf(c.Setup) is not null)
            {
                Log("info", $"Expirou sem entrar · {c.Setup} ({Describe(c.Side)}): a entrada a mercado não aconteceu dentro da validade.");
                continue;
            }
            var gatilho = $"{(c.Side == Side.Buy ? "compra acima de" : "venda abaixo de")} {c.Trigger:0.0}";
            var perto = closest is { } p
                ? $"o mais perto que o preço chegou foi {p:0.0} (faltou {Math.Abs(c.Trigger - p):0.0} pt)"
                : "sem cotação no período";
            Log("info", $"Expirou sem disparar · {c.Setup} ({gatilho}): {perto}.");
        }
    }

    /// <summary>A cada cotação sem posição: algum setup armado disparou?</summary>
    private async Task EvaluateSignalsAsync(TickMessage t)
    {
        if (_pendingSignal is { } p && clock.UtcNow - p.AtUtc > SignalLifetime)
        {
            if (p.ShadowSignalId is { } expiredId)
                shadowLearning.RecordDisposition(expiredId, clock.UtcNow, Label, "confirmacao_expirada");
            _pendingSignal = null;
        }
        if (_managed is not null || _pendingSignal is not null || _entryOrderId is not null) return;

        var levels = DayLevels.From(_candles, _day.TradingDay);
        SignalEngine.FlowCheck? flow = _settings.FlowFilterEnabled
            ? (Side side, out string why) => _flow.Confirms(side, FlowNow, out why)
            : null;
        var signal = _signals.OnPrice(t.Bid, t.Ask, clock.UtcNow, levels, SignalOptions(), out var skipped, flow);
        LogExpired();
        if (skipped is not null) Log("info", skipped);
        if (signal is null) return;

        var shadowSignalId = shadowLearning.ObserveSignal(Label, signal, clock.UtcNow, Adr,
            t.Bid, t.Ask, _candles, _flow.Read(FlowNow));
        var label = $"{signal.Setup} · {Describe(signal.Side)}";
        if (_settings.Mode == TradingMode.Assistant)
        {
            shadowLearning.RecordDisposition(shadowSignalId, clock.UtcNow, Label, "somente_observacao",
                "modo Assistente");
            DecisionRecorder.Write(Label, _day.TradingDay, clock.UtcNow, signal.Setup, "execução", signal.Side.Sign(),
                Adr, null, null, t.Bid, t.Ask, "somente observação", signal.Reason);
            Log("info", $"Oportunidade {label} (modo Assistente: só informativo). {signal.Reason}");
            await bridge.SendAsync(new MarkCommand(signal.Side, label));
            return;
        }

        var blocks = RiskGuard.EntryBlocks(Context());
        if (blocks.Count > 0)
        {
            shadowLearning.RecordDisposition(shadowSignalId, clock.UtcNow, Label, "bloqueada_pelo_risco",
                blocks[0].Message);
            DecisionRecorder.Write(Label, _day.TradingDay, clock.UtcNow, signal.Setup, "execução", signal.Side.Sign(),
                Adr, null, null, t.Bid, t.Ask, "bloqueada", blocks[0].Message);
            // Sem som (não é para entrar), mas marca no gráfico: dá para estudar o que o setup teria feito.
            Log("warn", $"Sinal {label} apareceu, mas não vale entrar: {blocks[0].Message} (marcado no gráfico só para estudo)");
            if (SetupNames.TimeRuleOf(signal.Setup) is not null) SetOutcome(signal.Setup, "fora", "bloqueado: " + blocks[0].Message);
            await bridge.SendAsync(new MarkCommand(signal.Side, label + " (bloqueado)"));
            return;
        }

        await bridge.SendAsync(new MarkCommand(signal.Side, label));

        // Automático: só no simulador e só para setup que já provou resultado (≥ 30 trades, expectativa positiva).
        if (_settings.Mode == TradingMode.Auto && _hello?.IsDemo == true && _validatedSetups.Contains(signal.Setup))
        {
            var intelligence = shadowLearning.DemoDecision(shadowSignalId);
            if (intelligence.Active && !intelligence.Allow)
            {
                StartShadowPaper(shadowSignalId, signal, t);
                shadowLearning.RecordDisposition(shadowSignalId, clock.UtcNow, Label, "filtrada_pelo_piloto_demo",
                    $"modelo {intelligence.ModelId}; probabilidade {intelligence.Probability:P1}");
                DecisionRecorder.Write(Label, _day.TradingDay, clock.UtcNow, signal.Setup, "execução", signal.Side.Sign(),
                    Adr, null, null, t.Bid, t.Ask, "filtrada pela inteligência",
                    $"Piloto demo {intelligence.ModelId}: probabilidade {intelligence.Probability:P1}");
                SetOutcome(signal.Setup, "fora", "filtro automático em piloto demo");
                Log("info", $"Sinal {label} filtrado automaticamente pelo piloto {intelligence.ModelId} " +
                            $"(p={intelligence.Probability:P1}). Nenhuma ordem foi enviada.");
                return;
            }
            Log("info", $"Sinal {label} — entrada automática. {signal.Reason}");
            var outcome = await OpenAsync(signal.Side, $"Sinal {signal.Setup} (automático)", signal.Setup, shadowSignalId);
            DecisionRecorder.Write(Label, _day.TradingDay, clock.UtcNow, signal.Setup, "execução", signal.Side.Sign(),
                Adr, null, null, t.Bid, t.Ask, outcome.StartsWith("Ordem enviada") ? "ordem enviada" : "não operou", outcome);
            if (SetupNames.TimeRuleOf(signal.Setup) is not null)
                SetOutcome(signal.Setup, outcome.StartsWith("Ordem enviada") ? "operou" : "fora",
                    $"{(signal.Side == Side.Buy ? "compra" : "venda")}: {outcome}");
            Alert($"Entrada automática · {signal.Setup}", $"{(signal.Side == Side.Buy ? "Compra" : "Venda")}: {outcome}");
            if (bridge.IsConnected)
                await bridge.SendAsync(new NotifyCommand($"TradeGuard: entrada automatica {Describe(signal.Side)} ({signal.Setup}) - {outcome}"));
            return;
        }

        _pendingSignal = new PendingSignal(signal, clock.UtcNow, shadowSignalId);
        shadowLearning.RecordDisposition(shadowSignalId, clock.UtcNow, Label, "aguardando_confirmacao");
        DecisionRecorder.Write(Label, _day.TradingDay, clock.UtcNow, signal.Setup, "execução", signal.Side.Sign(),
            Adr, null, null, t.Bid, t.Ask, "aguardando confirmação", signal.Reason);
        Log("info", $"SINAL {label}: {signal.Reason} Confirme no painel (vale 60 s).");
        SignalRaised?.Invoke(_pendingSignal);
        Alert($"Sinal de {(signal.Side == Side.Buy ? "COMPRA" : "VENDA")} · {signal.Setup}", signal.Reason + " Confirme no TradeGuard.");
        if (bridge.IsConnected)
            await bridge.SendAsync(new NotifyCommand($"TradeGuard: sinal de {Describe(signal.Side)} ({signal.Setup}) - confirme no painel"));
    }
    // ------------------------------------------------------------------ interface

    private async Task<string> HandleUiAsync(UiAction action)
    {
        switch (action)
        {
            case UiAction.Buy or UiAction.Sell:
                return await OpenAsync(action == UiAction.Buy ? Side.Buy : Side.Sell, "TradeGuard");
            case UiAction.ConfirmSignal:
            {
                if (_pendingSignal is not { } sig) return "Sinal expirou.";
                _pendingSignal = null;
                if (sig.ShadowSignalId is { } confirmedId)
                    shadowLearning.RecordDisposition(confirmedId, clock.UtcNow, Label, "confirmado_pelo_usuario");
                return await OpenAsync(sig.Signal.Side, $"Sinal {sig.Signal.Setup} (confirmado)", sig.Signal.Setup,
                    sig.ShadowSignalId);
            }
            case UiAction.DismissSignal:
                if (_pendingSignal?.ShadowSignalId is { } dismissedId)
                    shadowLearning.RecordDisposition(dismissedId, clock.UtcNow, Label, "ignorado_pelo_usuario");
                _pendingSignal = null;
                return "Sinal ignorado.";
            case UiAction.CloseAll:
                if (_brokerPos is not { IsFlat: false }) return "Sem posição.";
                return await SendAsync(new CloseAllCommand(NewId(), "manual"), "Zerar posição (botão).") ? "Zerando." : "Falha.";
            case UiAction.ConfirmChecklist:
                _checklistDay = _day.TradingDay;
                // Vale o dia todo, mesmo se o programa for reaberto.
                await File.WriteAllTextAsync(ChecklistFile, _day.TradingDay.ToString("yyyy-MM-dd"));
                Log("info", "Checklist do dia confirmado.");
                return "Checklist ok.";
            case UiAction.Panic:
                if (_brokerPos is { IsFlat: false })
                    await SendAsync(new CloseAllCommand(NewId(), "panico"), "PÂNICO: zerando tudo.");
                await RequestPortfolioLockAsync(LockReason.Manual);
                return "Posição zerada e dia travado.";
        }
        return "";
    }

    // ------------------------------------------------------------------ relógio

    // ------------------------------------------------------------------ teste de execução (só simulador)

    public const string ExecutionTestSetup = "Teste de execução";
    private DateTimeOffset? _testExitAt;

    /// <summary>Pede um teste de execução: compra 1 contrato no simulador, stop normal, zera após 1 minuto.</summary>
    public void RequestExecutionTest() =>
        File.WriteAllText(Path.Combine(TradeGuardServices.DataDirectory, $"testar-execucao-{Label.ToLowerInvariant()}.txt"), DateTime.Now.ToString("O"));

    /// <summary>
    /// Prova o caminho inteiro de uma ordem sem esperar o horário das regras: entra, a corretora confirma, o robô põe o
    /// stop, zera sozinho após 1 minuto e grava no diário (desconsiderado nas estatísticas). Nunca em conta real.
    /// Disparado pelo botão da Verificação (ou pelo arquivo dados\testar-execucao-wdo.txt).
    /// </summary>
    private async Task ExecutionTestAsync(DateTimeOffset now)
    {
        var trigger = Path.Combine(TradeGuardServices.DataDirectory, $"testar-execucao-{Label.ToLowerInvariant()}.txt");
        if (_testExitAt is { } exitAt)
        {
            if (now >= exitAt && _brokerPos is { IsFlat: false } && _currentSetup == ExecutionTestSetup)
            {
                _testExitAt = null;
                await SendAsync(new CloseAllCommand(NewId(), "teste de execucao"), "Teste de execução: zerando após 1 minuto.");
            }
            else if (_brokerPos is not { IsFlat: false } && now >= exitAt) _testExitAt = null;
            return;
        }
        if (!File.Exists(trigger)) return;
        if (_hello is not { IsDemo: true })
        {
            File.Delete(trigger);
            Log("warn", "Teste de execução só roda no simulador.");
            return;
        }
        if (_brokerPos is { IsFlat: false }) { Log("warn", "Teste de execução adiado: já há posição aberta."); return; }
        Log("info", "Teste de execução: comprando 1 contrato no simulador para provar ordem → stop → saída → diário.");
        var saved = _settings;
        _settings = _settings with { RiskPerTradeMoney = 0, Contracts = 1 }; // 1 contrato, sem depender do risco
        try
        {
            var r = await OpenAsync(Side.Buy, ExecutionTestSetup, ExecutionTestSetup);
            Log("info", "Teste de execução: " + r);
            if (r.StartsWith("Ordem enviada"))
            {
                File.Delete(trigger);
                _testExitAt = now.AddMinutes(1);
            }
        }
        finally { _settings = saved; }
    }

    private async Task HandleTimerAsync()
    {
        var now = clock.UtcNow;

        await ExecutionTestAsync(now);

        if (_entryOrderId is not null && !_entryOrderTimeoutAlerted
            && now - _entryOrderSentAt > TimeSpan.FromSeconds(15))
        {
            _entryOrderTimeoutAlerted = true;
            Log("error", "Ordem de entrada sem confirmação há 15 s. Novas entradas permanecem bloqueadas até o MT5 confirmar posição ou zerado.");
            Alert("Ordem sem confirmação", $"{Label}: confira o MT5. O robô bloqueou novas entradas neste ativo.");
        }

        if (clock.TradingDay != _day.TradingDay)
        {
            await StartDayAsync(clock.TradingDay);
            Log("info", $"Novo pregão {_day.TradingDay:dd/MM}. Contadores zerados.");
        }

        await RequestDailyHistoryAsync(_day.TradingDay);

        if (IsPrimary && now - _lastParityAudit >= TimeSpan.FromMinutes(1))
        {
            _lastParityAudit = now;
            AuditStorage.Probe();
            if (_historyCandles.Count == 2)
                parity.AuditAll(_historyCandles, _day.TradingDay, clock.TimeOfDay);
        }

        // Amplitude média e teste ainda não carregados, ou a exportação diária acabou de mudar: tenta novamente.
        // Sem os dois arquivos atuais nenhuma regra de horário dispara.
        var history = portfolio.HistoryStatus(_day.TradingDay);
        if (_studiesDay != _day.TradingDay || _studiesRevision != history.Revision)
        {
            await LoadStudiesAsync(_day.TradingDay);
            if (_studiesDay == _day.TradingDay && _studiesRevision == history.Revision)
                _validatedSetups = _settings.EnabledSetups.Where(s => SetupNames.TimeRuleOf(s) is not null).ToHashSet();
        }

        if (bridge.IsConnected && now - _lastHeartbeat >= TimeSpan.FromSeconds(2))
        {
            _lastHeartbeat = now;
            await bridge.SendAsync(new HeartbeatCommand());
        }

        if (_brokerPos is { IsFlat: false } && CanAct
            && clock.TimeOfDay >= _settings.FlattenAt && now - _lastFlattenAttempt > TimeSpan.FromSeconds(10))
        {
            _lastFlattenAttempt = now;
            await SendAsync(new CloseAllCommand(NewId(), "horario de zeragem"),
                $"{_settings.FlattenAt:HH\\:mm}: zerando para evitar zeragem compulsória.");
        }

        // Estratégia de horário com saída própria (ex.: Reversão das 16h sai às 17:00).
        if (_brokerPos is { IsFlat: false } && CanAct && SetupNames.TimeRuleOf(_currentSetup) is { } rule
            && clock.TimeOfDay >= rule.Exit && rule.Exit < _settings.FlattenAt && now - _lastFlattenAttempt > TimeSpan.FromSeconds(10))
        {
            _lastFlattenAttempt = now;
            await SendAsync(new CloseAllCommand(NewId(), "saida do setup"), $"{rule.Name}: saída programada das {rule.Exit:HH\\:mm}.");
        }

        // Zerou e o negócio de saída não veio em 5 s (MT5 caiu?): o histórico completa ao reconectar.
        if (_awaitingExitSince is { } since && now - since > TimeSpan.FromSeconds(5))
        {
            _awaitingExitSince = null;
            await FinalizeTradeAsync();
        }

        // Posição aberta sem stop por mais de 3s (ex.: ordem manual) → recoloca.
        if (_managed is not null && _brokerPos is { IsFlat: false, StopLoss: 0 } && CanAct
            && now - _lastModifyAt > TimeSpan.FromSeconds(3) && now - _lastEnforceAt > TimeSpan.FromSeconds(3))
        {
            _lastEnforceAt = now;
            await SendModifyAsync(_managed.Stop, _managed.Target, "Posição sem stop — stop recolocado.");
        }
    }

    // ------------------------------------------------------------------ utilidades

    private void Publish()
    {
        var ctx = Context();
        var price = _managed is null || _quote is null ? 0
            : _managed.Side == Side.Buy ? Nz(_quote.Bid, _quote.Last) : Nz(_quote.Ask, _quote.Last);

        string? contractWarning = null;
        if (_instrument?.Expiration is { } exp)
        {
            var days = B3Clock.ToB3(exp).Date - clock.Now.Date;
            if (days.TotalDays <= 3)
                contractWarning = $"{_instrument.Symbol} vence em {B3Clock.ToB3(exp):dd/MM}. Migre para o próximo contrato.";
        }

        var blocks = RiskGuard.EntryBlocks(ctx);
        State = new EngineState
        {
            Connected = bridge.IsConnected,
            CanAct = CanAct,
            Connection = _hello is null ? null
                : new ConnectionInfo(_hello.Account, _hello.Server, _hello.Company, _hello.IsDemo, _hello.IsNetting, _hello.Symbol),
            Instrument = _instrument,
            Quote = _quote,
            Balance = _balance,
            Equity = _equity,
            Position = _managed is null ? null : new PositionInfo(
                _managed.Side, _managed.Contracts, _managed.EntryPrice, _managed.Stop, _managed.Target,
                _managed.BreakevenDone, _managed.MfePoints, _managed.MaePoints,
                price == 0 ? 0 : _managed.FloatingPnl(price),
                _instrument?.EstimatedRoundTripCost(_managed.Contracts) ?? 0,
                _managed.InitialRiskMoney, _managed.Source),
            TradingDay = _day.TradingDay,
            DayRealized = _day.RealizedPnl,
            DayTrades = _day.TradesCount,
            ConsecutiveLosses = _day.ConsecutiveLosses,
            Lock = _day.Lock,
            WeekPnl = ctx.WeekPnl,
            MonthPnl = ctx.MonthPnl,
            Blocks = blocks,
            UpcomingNews = _news.Where(n => n.TimeUtc >= clock.UtcNow.AddMinutes(-30)).OrderBy(n => n.TimeUtc).Take(8).ToList(),
            Settings = _settings,
            PendingSettings = _pendingSettings,
            ContractWarning = contractWarning,
            ChecklistDone = ChecklistOk,
            BrokerConnected = _brokerConnected,
            AlgoAllowed = _algoAllowed,
            Flow = _flow.Read(FlowNow),
            Schedule = BuildSchedule(),
            FlowCaps = _flowCaps is { } fc ? new FlowCapsInfo(fc.Trades, fc.WithSide, fc.BookDepth, fc.History > 0) : null,
            LastPrintUtc = _lastPrintUtc == default ? null : _lastPrintUtc,
            RejectedHandshakes = bridge.RejectedHandshakes,
            Readiness = parity.Readiness,
            NextContracts = _instrument is null ? _settings.Contracts : _settings.ContractsFor(_instrument),
            ValidatedSetups = _validatedSetups,
            Studies = _studies,
            ArmedSetups = _signals.Armed,
            Reading = MarketReader.Read(_candles, DayLevels.From(_candles, _day.TradingDay), _signals.Armed,
                _quote?.Bid ?? 0, _quote?.Ask ?? 0, _settings.TargetPoints, blocks),
            Trend = Indicators.TrendOf(_candles),
            LastCandleUtc = _candles.Count > 0 ? _candles[^1].OpenUtc : null,
            PendingSignal = _pendingSignal,
            Log = _log.ToList()
        };
        Changed?.Invoke();
    }

    private bool _brokerConnected = true, _algoAllowed = true;

    private void TrackTerminalStatus(bool broker, bool algo)
    {
        if (broker != _brokerConnected)
        {
            _brokerConnected = broker;
            Log(broker ? "info" : "error", broker ? "MT5 reconectado à corretora." : "MT5 perdeu a conexão com a corretora.");
            if (!broker) Alert("Sem conexão com a corretora", "O MT5 perdeu a conexão. Entradas bloqueadas até voltar.");
        }
        if (algo != _algoAllowed)
        {
            _algoAllowed = algo;
            Log(algo ? "info" : "error", algo ? "Algotrading ligado no MT5." : "Algotrading DESLIGADO no MT5: o robô não consegue enviar ordens.");
            if (!algo) Alert("Algotrading desligado", "Ligue o botão Algotrading no MT5.");
        }
    }

    private void Alert(string title, string text)
    {
        try { Alerted?.Invoke(title, text); } catch (Exception ex) { logger.LogWarning(ex, "Falha ao notificar"); }
    }

    private void Log(string level, string text)
    {
        if (!IsPrimary) text = $"[{Label}] {text}";
        _log.Insert(0, new LogLine(clock.UtcNow, level, text));
        if (_log.Count > 200) _log.RemoveAt(_log.Count - 1);
        logger.Log(level switch { "error" => LogLevel.Error, "warn" => LogLevel.Warning, _ => LogLevel.Information }, "{Text}", text);
    }

    private DateTimeOffset ServerToUtc(long serverSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(serverSeconds - _serverOffset);

    private DateTimeOffset ServerMsToUtc(long serverMs) =>
        serverMs == 0 ? clock.UtcNow : DateTimeOffset.FromUnixTimeMilliseconds(serverMs - _serverOffset * 1000);

    private static decimal Nz(decimal preferred, decimal fallback) => preferred != 0 ? preferred : fallback;

    private static string Describe(Side s) => s == Side.Buy ? "COMPRA" : "VENDA";

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];
}
