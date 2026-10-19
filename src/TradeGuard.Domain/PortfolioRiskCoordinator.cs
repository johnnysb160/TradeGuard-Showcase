namespace TradeGuard.Domain;

/// <summary>
/// Estado compartilhado dos motores WDO e WIN. Soma o resultado flutuante das posições abertas,
/// acompanha a saúde dos dois canais e garante que uma trava do portfólio seja disparada uma vez só.
/// </summary>
public sealed class PortfolioRiskCoordinator
{
    private sealed record InstrumentState(
        DateOnly Day, decimal FloatingPnl, bool HasPosition, bool Connected, bool BrokerConnected,
        DateTimeOffset? LastQuoteUtc);

    private readonly object _sync = new();
    private readonly Dictionary<string, InstrumentState> _states = new(StringComparer.OrdinalIgnoreCase);
    private DateOnly _lockDay;
    private LockReason _lockReason;
    private DateOnly _historyDay;
    private readonly HashSet<string> _historyExports = new(StringComparer.OrdinalIgnoreCase);
    private long _historyRevision;

    public event Action<DateOnly, LockReason, DateTimeOffset, string>? LockRequested;

    public void BeginHistoryExport(DateOnly day)
    {
        lock (_sync)
        {
            _historyDay = day;
            _historyExports.Clear();
        }
    }

    /// <summary>Marca um arquivo concluído. A revisão só avança quando WDO e WIN terminaram no mesmo pregão.</summary>
    public void MarkHistoryExported(DateOnly day, string instrument)
    {
        lock (_sync)
        {
            if (_historyDay != day) return;
            var wasReady = _historyExports.Contains("WDO") && _historyExports.Contains("WIN");
            _historyExports.Add(instrument);
            if (!wasReady && _historyExports.Contains("WDO") && _historyExports.Contains("WIN"))
                _historyRevision++;
        }
    }

    public (bool Ready, long Revision) HistoryStatus(DateOnly day)
    {
        lock (_sync)
            return (_historyDay == day && _historyExports.Contains("WDO") && _historyExports.Contains("WIN"),
                _historyRevision);
    }

    public void StartDay(string instrument, DateOnly day)
    {
        lock (_sync)
        {
            var previous = _states.GetValueOrDefault(instrument);
            if (previous?.Day == day) return;
            _states[instrument] = new InstrumentState(day, 0, false,
                previous?.Connected ?? false, previous?.BrokerConnected ?? true, previous?.LastQuoteUtc);
            if (_lockDay != day)
            {
                _lockDay = day;
                _lockReason = LockReason.None;
            }
        }
    }

    public decimal UpdatePosition(string instrument, DateOnly day, decimal floatingPnl, bool hasPosition)
    {
        lock (_sync)
        {
            var previous = StateFor(instrument, day);
            _states[instrument] = previous with { FloatingPnl = hasPosition ? floatingPnl : 0, HasPosition = hasPosition };
            return _states.Values.Where(s => s.Day == day && s.HasPosition).Sum(s => s.FloatingPnl);
        }
    }

    public void UpdateHealth(string instrument, DateOnly day, bool connected, bool brokerConnected,
        DateTimeOffset? lastQuoteUtc = null)
    {
        lock (_sync)
        {
            var previous = StateFor(instrument, day);
            _states[instrument] = previous with
            {
                Connected = connected,
                BrokerConnected = brokerConnected,
                LastQuoteUtc = lastQuoteUtc ?? previous.LastQuoteUtc
            };
        }
    }

    /// <summary>Os dois canais precisam estar conectados à corretora e com cotação recente.</summary>
    public (bool Healthy, string Message) Health(DateOnly day, DateTimeOffset nowUtc, TimeSpan maxQuoteAge)
    {
        lock (_sync)
        {
            foreach (var instrument in new[] { "WDO", "WIN" })
            {
                if (!_states.TryGetValue(instrument, out var state) || state.Day != day || !state.Connected)
                    return (false, $"Canal {instrument} desconectado.");
                if (!state.BrokerConnected)
                    return (false, $"Canal {instrument} sem conexão com a corretora.");
                if (state.LastQuoteUtc is null || nowUtc - state.LastQuoteUtc > maxQuoteAge)
                    return (false, $"Cotação do {instrument} está atrasada.");
            }
            return (true, "");
        }
    }

    public bool TryRequestLock(DateOnly day, LockReason reason, DateTimeOffset atUtc, string origin)
    {
        Action<DateOnly, LockReason, DateTimeOffset, string>? notify;
        lock (_sync)
        {
            if (reason == LockReason.None) return false;
            if (_lockDay == day && _lockReason != LockReason.None) return false;
            _lockDay = day;
            _lockReason = reason;
            notify = LockRequested;
        }
        notify?.Invoke(day, reason, atUtc, origin);
        return true;
    }

    public void RestoreLock(DateOnly day, LockReason reason)
    {
        lock (_sync)
        {
            if (_lockDay != day || _lockReason == LockReason.None)
            {
                _lockDay = day;
                _lockReason = reason;
            }
        }
    }

    public void ClearLock(DateOnly day)
    {
        lock (_sync)
        {
            if (_lockDay == day) _lockReason = LockReason.None;
        }
    }

    private InstrumentState StateFor(string instrument, DateOnly day) =>
        _states.TryGetValue(instrument, out var state) && state.Day == day
            ? state
            : new InstrumentState(day, 0, false, false, true, null);
}
