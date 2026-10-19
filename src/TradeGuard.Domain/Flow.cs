namespace TradeGuard.Domain;

/// <summary>Quem agrediu no negócio: comprador (pagou o preço de venda) ou vendedor (bateu no de compra).</summary>
public enum Aggressor { None, Buy, Sell }

/// <summary>Um negócio do Times &amp; Trades.</summary>
public sealed record TradePrint(long TimeMsc, decimal Price, decimal Volume, Aggressor Side)
{
    public DateTimeOffset TimeUtc => DateTimeOffset.FromUnixTimeMilliseconds(TimeMsc);
}

public sealed record BookLevel(decimal Price, decimal Volume);

public enum FlowBias { Neutral, Buyers, Sellers }

/// <summary>Leitura do fluxo agora (tape reading), em números e em uma frase.</summary>
public sealed record FlowReading(
    bool HasData,
    decimal Delta5s, decimal Delta30s, decimal Delta1m, decimal DeltaDay,
    decimal BuyVolume1m, decimal SellVolume1m,
    decimal BigLot, int BigBuys1m, int BigSells1m,
    decimal? BookImbalance,
    FlowBias Bias, bool Strong,
    FlowBias Absorption,
    string Headline, string Detail)
{
    public static readonly FlowReading Empty = new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, FlowBias.Neutral, false, FlowBias.Neutral,
        "Sem dados de fluxo ainda.", "O fluxo aparece quando o MT5 começar a mandar os negócios.");
}

/// <summary>
/// Tape reading: lê as agressões do Times &amp; Trades e o book.
/// <list type="bullet">
/// <item>Saldo de agressão (delta): volume que agrediu na compra − volume que agrediu na venda.</item>
/// <item>Lote grande: negócio entre os 1% maiores do dia (mínimo <see cref="MinBigLot"/>) — no MT5 não dá para saber
/// a corretora, então "institucional" é estimado pelo tamanho.</item>
/// <item>Absorção: um lado agride forte e o preço não anda — alguém grande está segurando do outro lado.</item>
/// <item>Desequilíbrio do book: mais lotes esperando na compra ou na venda nos 5 melhores níveis.</item>
/// </list>
/// Puro: recebe negócios e o relógio; o mesmo código roda ao vivo e no teste com ticks gravados.
/// </summary>
public sealed class FlowEngine
{
    public const decimal MinBigLot = 20;
    private static readonly TimeSpan Keep = TimeSpan.FromMinutes(5);

    private readonly LinkedList<TradePrint> _recent = new();
    private readonly List<decimal> _dayVolumes = [];
    private readonly List<decimal> _bucketDeltas = []; // |delta| a cada 5 s, para saber o que é "forte" hoje
    private decimal _bucketDelta;
    private long _bucketStart;
    private decimal _bigLot = MinBigLot;
    private DateOnly _day;
    private decimal _deltaDay;
    private IReadOnlyList<BookLevel> _bids = [], _asks = [];

    public void Add(IEnumerable<TradePrint> prints)
    {
        foreach (var p in prints)
        {
            var day = DateOnly.FromDateTime(B3Clock.ToB3(p.TimeUtc).DateTime);
            if (day != _day) Reset(day);
            _recent.AddLast(p);
            if (p.Side == Aggressor.None) continue;

            var signed = p.Side == Aggressor.Buy ? p.Volume : -p.Volume;
            _deltaDay += signed;
            _dayVolumes.Add(p.Volume);
            if (_dayVolumes.Count % 2000 == 0) _bigLot = Math.Max(MinBigLot, Percentile(_dayVolumes, 0.99m));

            if (_bucketStart == 0) _bucketStart = p.TimeMsc;
            while (p.TimeMsc - _bucketStart >= 5000)
            {
                _bucketDeltas.Add(Math.Abs(_bucketDelta));
                if (_bucketDeltas.Count > 720) _bucketDeltas.RemoveAt(0); // última hora
                _bucketDelta = 0;
                _bucketStart += 5000;
            }
            _bucketDelta += signed;
        }
        if (_recent.Last is { } last)
            while (_recent.First is { } first && last.Value.TimeUtc - first.Value.TimeUtc > Keep) _recent.RemoveFirst();
    }

    public void SetBook(IReadOnlyList<BookLevel> bids, IReadOnlyList<BookLevel> asks)
    {
        _bids = bids;
        _asks = asks;
    }

    private void Reset(DateOnly day)
    {
        _day = day;
        _recent.Clear();
        _dayVolumes.Clear();
        _bucketDeltas.Clear();
        _bucketDelta = 0;
        _bucketStart = 0;
        _deltaDay = 0;
        _bigLot = MinBigLot;
    }

    /// <summary>Saldo de agressão (compra − venda) nos últimos <paramref name="window"/>.</summary>
    public decimal Delta(TimeSpan window, DateTimeOffset nowUtc) =>
        Window(window, nowUtc).Sum(p => p.Side == Aggressor.Buy ? p.Volume : p.Side == Aggressor.Sell ? -p.Volume : 0);

    /// <summary>|delta| de 5 s que conta como "forte" hoje (média + 1 desvio da última hora).</summary>
    public decimal StrongThreshold
    {
        get
        {
            if (_bucketDeltas.Count < 12) return Math.Max(10, _bigLot);
            var mean = _bucketDeltas.Average();
            var sd = (decimal)Math.Sqrt(_bucketDeltas.Average(d => (double)((d - mean) * (d - mean))));
            return Math.Max(5, mean + sd);
        }
    }

    /// <summary>
    /// Confirmação de entrada pelo fluxo: nos últimos 5 s o saldo de agressão está a favor e forte, e nos últimos
    /// 30 s não há absorção contra. Devolve o motivo quando não confirma.
    /// </summary>
    public bool Confirms(Side side, DateTimeOffset nowUtc, out string why)
    {
        var r = Read(nowUtc);
        why = "";
        if (!r.HasData) { why = "sem dados de fluxo"; return true; } // sem dados não bloqueia (a Verificação avisa)
        var d5 = r.Delta5s * side.Sign();
        if (d5 <= 0)
        {
            why = r.Delta5s == 0
                ? "sem agressão a favor nos últimos 5 s"
                : $"fluxo contra: saldo de agressão nos últimos 5 s é {r.Delta5s:+0;-0} contratos";
            return false;
        }
        if (d5 < StrongThreshold / 2)
        {
            why = $"fluxo fraco: saldo {r.Delta5s:+0;-0} nos últimos 5 s (forte hoje ≥ {StrongThreshold:0})";
            return false;
        }
        var against = side == Side.Buy ? FlowBias.Sellers : FlowBias.Buyers;
        if (r.Absorption == against)
        {
            why = side == Side.Buy ? "absorção vendedora: compradores agridem e o preço não sobe" : "absorção compradora: vendedores agridem e o preço não cai";
            return false;
        }
        why = $"fluxo confirma: saldo {r.Delta5s:+0;-0} em 5 s";
        return true;
    }

    public FlowReading Read(DateTimeOffset nowUtc)
    {
        var last1m = Window(TimeSpan.FromMinutes(1), nowUtc).ToList();
        if (_recent.Count == 0 || last1m.Count == 0) return FlowReading.Empty;

        var d5 = Delta(TimeSpan.FromSeconds(5), nowUtc);
        var d30 = Delta(TimeSpan.FromSeconds(30), nowUtc);
        var buy1 = last1m.Where(p => p.Side == Aggressor.Buy).Sum(p => p.Volume);
        var sell1 = last1m.Where(p => p.Side == Aggressor.Sell).Sum(p => p.Volume);
        var bigBuys = last1m.Count(p => p.Side == Aggressor.Buy && p.Volume >= _bigLot);
        var bigSells = last1m.Count(p => p.Side == Aggressor.Sell && p.Volume >= _bigLot);

        decimal? imbalance = null;
        var bidVol = _bids.Sum(b => b.Volume);
        var askVol = _asks.Sum(a => a.Volume);
        if (bidVol + askVol > 0) imbalance = Math.Round((bidVol - askVol) / (bidVol + askVol), 2);

        // Quem está na frente: mesma janela da barra da tela (último minuto), com 20% de folga para não oscilar.
        var bias = buy1 > sell1 * 1.2m ? FlowBias.Buyers : sell1 > buy1 * 1.2m ? FlowBias.Sellers : FlowBias.Neutral;
        // "Forte": o saldo dos últimos 5 s está acima do normal do dia e no mesmo sentido.
        var strong = bias != FlowBias.Neutral && Math.Abs(d5) >= StrongThreshold && Math.Sign(d5) == (bias == FlowBias.Buyers ? 1 : -1);
        var absorption = Absorption(nowUtc);

        var headline = (bias, strong) switch
        {
            (FlowBias.Buyers, true) => "Compradores agredindo forte",
            (FlowBias.Buyers, false) => "Compradores levemente na frente",
            (FlowBias.Sellers, true) => "Vendedores agredindo forte",
            (FlowBias.Sellers, false) => "Vendedores levemente na frente",
            _ => "Fluxo equilibrado"
        };
        if (absorption == FlowBias.Buyers) headline += " · absorção compradora (vendem e o preço não cai)";
        if (absorption == FlowBias.Sellers) headline += " · absorção vendedora (compram e o preço não sobe)";

        var detail = $"saldo 5 s {d5:+0;-0} · 30 s {d30:+0;-0} · dia {_deltaDay:+0;-0} contratos" +
                     (bigBuys + bigSells > 0 ? $" · lotes grandes (≥ {_bigLot:0}) no último minuto: {bigBuys} compra, {bigSells} venda" : "") +
                     (imbalance is { } im ? $" · book {(im > 0.2m ? "mais compra esperando" : im < -0.2m ? "mais venda esperando" : "equilibrado")}" : "");

        return new FlowReading(true, d5, d30, Delta(TimeSpan.FromMinutes(1), nowUtc), _deltaDay, buy1, sell1,
            _bigLot, bigBuys, bigSells, imbalance, bias, strong, absorption, headline, detail);
    }

    // Absorção nos últimos 30 s: um lado agride pelo menos 2x o outro, com volume relevante, e o preço não vai a favor dele.
    private FlowBias Absorption(DateTimeOffset nowUtc)
    {
        var w = Window(TimeSpan.FromSeconds(30), nowUtc).ToList();
        if (w.Count < 10) return FlowBias.Neutral;
        var buy = w.Where(p => p.Side == Aggressor.Buy).Sum(p => p.Volume);
        var sell = w.Where(p => p.Side == Aggressor.Sell).Sum(p => p.Volume);
        var move = w[0].Price - w[^1].Price; // Window vem do mais novo para o mais velho
        var relevant = StrongThreshold * 2;
        if (sell >= 2 * buy && sell >= relevant && move >= 0) return FlowBias.Buyers;  // vendem forte e não cai: comprador segurando
        if (buy >= 2 * sell && buy >= relevant && move <= 0) return FlowBias.Sellers;  // compram forte e não sobe: vendedor segurando
        return FlowBias.Neutral;
    }

    private IEnumerable<TradePrint> Window(TimeSpan window, DateTimeOffset nowUtc)
    {
        var from = (nowUtc - window).ToUnixTimeMilliseconds();
        for (var n = _recent.Last; n is not null && n.Value.TimeMsc >= from; n = n.Previous)
            yield return n.Value;
    }

    private static decimal Percentile(List<decimal> values, decimal q)
    {
        var sorted = values.Order().ToList();
        return sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(q * sorted.Count))];
    }
}
