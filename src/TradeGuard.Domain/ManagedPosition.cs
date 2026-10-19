namespace TradeGuard.Domain;

public abstract record ManagementAction(string Reason);

public sealed record MoveStop(decimal NewStop, string Reason) : ManagementAction(Reason);

public sealed record ClosePartial(int Contracts, string Reason) : ManagementAction(Reason);

public sealed record CloseAll(string Reason) : ManagementAction(Reason);

/// <summary>Aumenta a posição e já leva o stop da posição inteira para <paramref name="NewStop"/> (risco total fixo).</summary>
public sealed record AddContracts(int Contracts, decimal NewStop, string Reason) : ManagementAction(Reason);

/// <summary>
/// Máquina de estados de UMA posição: brackets iniciais, break-even, parciais, trailing,
/// MAE/MFE. Não envia ordens: devolve <see cref="ManagementAction"/> para o motor executar.
/// </summary>
public sealed class ManagedPosition
{
    private readonly RiskSettings _settings;
    private readonly InstrumentSpec _instrument;
    private readonly decimal _initialRiskPoints;
    private readonly HashSet<int> _partialsHandled = [];
    private bool _timeExitSent;

    public Guid Id { get; } = Guid.NewGuid();
    public Side Side { get; }
    public decimal EntryPrice { get; private set; }
    public int InitialContracts { get; }
    public int MaxContracts { get; private set; }
    public int Contracts { get; private set; }
    public decimal InitialStop { get; }
    public decimal Stop { get; private set; }
    public decimal Target { get; private set; }
    public bool BreakevenDone { get; private set; }
    public decimal MfePoints { get; private set; }
    public decimal MaePoints { get; private set; }
    public DateTimeOffset OpenedAtUtc { get; }
    public string Source { get; }

    public ManagedPosition(
        Side side, decimal entryPrice, int contracts, decimal? existingStop, decimal? existingTarget,
        RiskSettings settings, InstrumentSpec instrument, DateTimeOffset openedAtUtc, string source)
    {
        _settings = settings;
        _instrument = instrument;
        Side = side;
        EntryPrice = _firstEntry = entryPrice;
        InitialContracts = MaxContracts = Contracts = contracts;
        OpenedAtUtc = openedAtUtc;
        Source = source;

        var sign = side.Sign();
        var defaultStop = instrument.RoundStopTowardsEntry(entryPrice - sign * settings.StopPoints, side);
        var widestStop = entryPrice - sign * settings.MaxStopPoints;

        // Stop colocado à mão no MT5 é respeitado se estiver do lado certo e dentro do máximo.
        Stop = existingStop is { } s && s != 0
               && (s - entryPrice) * sign < 0
               && (s - widestStop) * sign >= 0
            ? s
            : defaultStop;
        InitialStop = Stop;
        _initialRiskPoints = Math.Abs(entryPrice - InitialStop);

        Target = existingTarget is { } t && t != 0 && (t - entryPrice) * sign > 0
            ? t
            : instrument.RoundToTick(entryPrice + sign * settings.TargetPoints);
    }

    public decimal InitialRiskPoints => _initialRiskPoints;

    public decimal InitialRiskMoney => _instrument.Money(InitialRiskPoints, InitialContracts);

    public decimal FavorablePoints(decimal price) => (price - EntryPrice) * Side.Sign();

    public decimal FloatingPnl(decimal price) => _instrument.Money(FavorablePoints(price), Contracts);

    /// <summary>Stop só pode andar a favor. Stop mais largo é recusado (anti-tilt).</summary>
    public bool IsTighter(decimal candidate) => (candidate - Stop) * Side.Sign() > 0;

    /// <summary>Quantos aumentos já foram feitos nesta posição.</summary>
    public int Adds { get; private set; }
    private bool _addPending;
    private readonly decimal _firstEntry;

    /// <param name="liquidity">
    /// Confirmação do fluxo para aumentar: a favor pede agressão a favor; preço médio pede absorção a favor
    /// (alguém grande segurando o preço). Null = sem dados de fluxo (não aumenta).
    /// </param>
    public IReadOnlyList<ManagementAction> OnPrice(decimal price, DateTimeOffset nowUtc, Bar? previousBar = null,
        Func<Side, ScaleMode, bool>? liquidity = null)
    {
        var actions = new List<ManagementAction>();
        var fav = FavorablePoints(price);
        MfePoints = Math.Max(MfePoints, fav);
        MaePoints = Math.Max(MaePoints, -fav);

        var sign = Side.Sign();
        var s = _settings;

        if (TryScale(price, fav, liquidity) is { } add)
        {
            actions.Add(add);
            return actions; // o resto da gestão continua depois que a corretora confirmar o aumento
        }

        if (s.MaxHoldMinutes > 0 && !_timeExitSent && nowUtc - OpenedAtUtc >= TimeSpan.FromMinutes(s.MaxHoldMinutes))
        {
            _timeExitSent = true;
            actions.Add(new CloseAll($"Tempo máximo de {s.MaxHoldMinutes} min no trade"));
            return actions;
        }

        // Parciais (precisa sobrar pelo menos 1 contrato).
        var remaining = Contracts;
        for (var i = 0; i < s.Partials.Count; i++)
        {
            if (_partialsHandled.Contains(i)) continue;
            var level = s.Partials[i];
            if (fav < level.TriggerPoints) continue;
            _partialsHandled.Add(i);
            if (remaining - level.Contracts >= 1)
            {
                actions.Add(new ClosePartial(level.Contracts, $"Parcial em +{level.TriggerPoints} pts"));
                remaining -= level.Contracts;
            }
        }

        var bestStop = Stop;

        if (s.BreakevenEnabled && !BreakevenDone && fav >= s.BreakevenTriggerPoints)
        {
            BreakevenDone = true;
            var be = _instrument.RoundToTick(EntryPrice + sign * s.BreakevenOffsetPoints);
            if ((be - bestStop) * sign > 0) bestStop = be;
        }

        var reason = "Break-even";

        // Stop em degraus: andou X, trava Y de lucro.
        foreach (var level in s.ProfitLocks.OrderBy(l => l.TriggerPoints))
        {
            if (fav < level.TriggerPoints) break;
            var locked = _instrument.RoundToTick(EntryPrice + sign * level.LockPoints);
            if ((locked - bestStop) * sign > 0)
            {
                bestStop = locked;
                reason = $"Degrau +{level.TriggerPoints}: trava +{level.LockPoints} pts";
            }
        }

        if (s.TrailingEnabled && fav >= s.TrailingStartPoints)
        {
            decimal? trail = s.TrailingMode switch
            {
                TrailingMode.PreviousCandle when previousBar is not null =>
                    _instrument.RoundToTick(Side == Side.Buy ? previousBar.Low - _instrument.TickSize : previousBar.High + _instrument.TickSize),
                TrailingMode.Fixed => _instrument.RoundStopTowardsEntry(price - sign * s.TrailingDistancePoints, Side),
                _ => null
            };
            // O stop do trailing nunca pode ficar do outro lado do preço atual.
            if (trail is { } t && (price - t) * sign > 0 && (t - bestStop) * sign >= s.TrailingStepPoints)
            {
                bestStop = t;
                reason = s.TrailingMode == TrailingMode.PreviousCandle ? "Trailing pelo candle anterior" : "Trailing stop";
            }
        }

        if (bestStop != Stop)
        {
            Stop = bestStop;
            actions.Add(new MoveStop(bestStop, reason));
        }

        return actions;
    }

    /// <summary>
    /// Aumento de posição com RISCO TOTAL FIXO: depois de aumentar, o stop da posição inteira vai para onde a perda
    /// é, no máximo, a mesma da entrada inicial (contratos × distância do stop). Aumentar nunca aumenta a perda máxima.
    /// <list type="bullet">
    /// <item>A favor (piramidação): a cada N pts de lucro, se o fluxo agride a favor.</item>
    /// <item>Preço médio: a cada N pts contra, só se houver absorção a favor (nível de liquidez) — e o stop aperta.</item>
    /// </list>
    /// </summary>
    private AddContracts? TryScale(decimal price, decimal fav, Func<Side, ScaleMode, bool>? liquidity)
    {
        var s = _settings;
        if (s.ScaleMode == ScaleMode.Off || _addPending || Adds >= s.ScaleMaxAdds || liquidity is null) return null;
        var add = s.ScaleAddContracts;
        if (add < 1 || Contracts + add > s.MaxContracts) return null;

        // Distância contada da PRIMEIRA entrada (o preço médio muda a cada aumento).
        var fromFirst = (price - _firstEntry) * Side.Sign();
        var step = s.ScaleStepPoints * (Adds + 1);
        var reached = s.ScaleMode == ScaleMode.WithTrend ? fromFirst >= step : fromFirst <= -step;
        if (!reached || !liquidity(Side, s.ScaleMode)) return null;

        var sign = Side.Sign();
        var total = Contracts + add;
        var avg = (EntryPrice * Contracts + price * add) / total;
        var maxLossPoints = InitialRiskPoints * InitialContracts; // em "pontos × contratos"
        var newStop = _instrument.RoundStopTowardsEntry(avg - sign * maxLossPoints / total, Side);
        // Decimal/tick não podem fazer o arredondamento exceder nem um centavo do risco inicial.
        while (_instrument.Money(Math.Abs(avg - newStop), total) > InitialRiskMoney)
            newStop += sign * _instrument.TickSize;
        if ((newStop - Stop) * sign < 0) newStop = Stop; // nunca afrouxa o stop atual
        if ((price - newStop) * sign < _instrument.TickSize * 2) return null; // stop colado no preço: não aumenta

        Adds++;
        _addPending = true;
        Stop = newStop;
        var label = s.ScaleMode == ScaleMode.WithTrend ? "Aumento a favor" : "Preço médio";
        return new AddContracts(add, newStop,
            $"{label} {Adds}/{s.ScaleMaxAdds}: +{add} contrato(s) em {price:0.0##}, stop da posição em {newStop:0.0##} (perda máxima igual à inicial)");
    }

    /// <summary>
    /// Sincroniza com o que o MT5 reporta. Devolve o stop que deve ser reimposto quando alguém
    /// tentou afrouxar o stop no MT5; null se estiver tudo certo.
    /// </summary>
    public decimal? SyncFromBroker(int contracts, decimal avgPrice, decimal brokerStop, decimal brokerTarget)
    {
        if (contracts > Contracts) _addPending = false; // aumento confirmado pela corretora
        Contracts = contracts;
        MaxContracts = Math.Max(MaxContracts, contracts);
        if (avgPrice != 0) EntryPrice = avgPrice;
        if (brokerTarget != 0) Target = brokerTarget;

        if (brokerStop == 0 || (brokerStop - Stop) * Side.Sign() < 0)
            return Stop; // sem stop ou stop afrouxado → reimpõe o nosso

        Stop = brokerStop; // apertado manualmente: aceita
        return null;
    }
}
