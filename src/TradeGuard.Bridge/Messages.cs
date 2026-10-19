using System.Globalization;
using System.Text;
using System.Text.Json;
using TradeGuard.Domain;

namespace TradeGuard.Bridge;

// ---------------- Mensagens MT5 → .NET ----------------

public abstract record InboundMessage(long Seq);

public sealed record HelloMessage(
    long Seq, int Version, string Nonce, long Account, string Server, string Company, bool IsDemo,
    bool IsNetting, string Symbol, decimal TickSize, decimal TickValue, int Digits,
    long ExpirationServer, long ServerTime, long GmtTime, string EaVersion = "") : InboundMessage(Seq)
{
    /// <summary>Diferença horário do servidor do MT5 − UTC, em segundos.</summary>
    public long ServerOffsetSeconds => ServerTime - GmtTime;
}

public sealed record TickMessage(long Seq, decimal Bid, decimal Ask, decimal Last, long TimeMsc) : InboundMessage(Seq);

public sealed record PositionMessage(long Seq, string Side, int Volume, decimal Price, decimal StopLoss,
    decimal TakeProfit, decimal Profit, long PositionId = 0, string Comment = "", long OpenedAtServer = 0) : InboundMessage(Seq)
{
    public bool IsFlat => Volume == 0 || Side == "flat";
    public Side? AsSide => Side switch { "buy" => Domain.Side.Buy, "sell" => Domain.Side.Sell, _ => null };
}

public sealed record DealMessage(long Seq, ulong Ticket, long Time, string Side, int Volume, decimal Price,
    decimal Profit, string Entry, string Comment, long PositionId = 0, string Symbol = "") : InboundMessage(Seq);

/// <summary>Negócio do histórico, enviado ao conectar (para completar o diário).</summary>
public sealed record HistoryDealMessage(long Seq, DealMessage Deal) : InboundMessage(Seq);

public sealed record HistoryEndMessage(long Seq) : InboundMessage(Seq);

/// <summary>Candle fechado do gráfico (Time em horário do servidor; Seconds = duração do candle).</summary>
public sealed record BarMessage(long Seq, long Time, decimal Open, decimal High, decimal Low, decimal Close, int Seconds, bool IsHistory)
    : InboundMessage(Seq);

public sealed record BarsEndMessage(long Seq) : InboundMessage(Seq);

public sealed record AccountMessage(long Seq, decimal Balance, decimal Equity, decimal FreeMargin,
    long ServerTime, long GmtTime, bool BrokerConnected = true, bool AlgoAllowed = true) : InboundMessage(Seq);

public sealed record AckMessage(long Seq, string Id, bool Ok, int RetCode, string Text) : InboundMessage(Seq);

public sealed record NewsMessage(long Seq, long Time, string Currency, int Importance, string Name) : InboundMessage(Seq);

public sealed record NewsEndMessage(long Seq) : InboundMessage(Seq);

public sealed record LogMessage(long Seq, string Level, string Text) : InboundMessage(Seq);

public sealed record UnknownMessage(long Seq, string Type) : InboundMessage(Seq);

/// <summary>Negócios do Times &amp; Trades (em lote).</summary>
public sealed record TradesMessage(long Seq, IReadOnlyList<TradePrint> Prints) : InboundMessage(Seq);

/// <summary>Melhores níveis do book (compra do melhor para o pior; venda idem).</summary>
public sealed record BookMessage(long Seq, IReadOnlyList<BookLevel> Bids, IReadOnlyList<BookLevel> Asks) : InboundMessage(Seq);

/// <summary>O que a corretora entrega para fluxo: negócios em 30 min, quantos com lado agressor, profundidade do book, histórico.</summary>
public sealed record FlowCapsMessage(long Seq, int Trades, int WithSide, int WithVolume, int BookDepth, int History) : InboundMessage(Seq);

public static class InboundParser
{
    public static InboundMessage Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var seq = L(r, "seq");
        return S(r, "t") switch
        {
            "hello" => new HelloMessage(seq, (int)L(r, "v"), S(r, "nonce"), L(r, "acct"), S(r, "server"),
                S(r, "company"), L(r, "demo") == 1, L(r, "netting") == 1, S(r, "sym"), D(r, "tick"),
                D(r, "tickval"), (int)L(r, "digits"), L(r, "exp"), L(r, "srv"), L(r, "gmt"), S(r, "ver")),
            "tick" => new TickMessage(seq, D(r, "bid"), D(r, "ask"), D(r, "last"), L(r, "tms")),
            "pos" => new PositionMessage(seq, S(r, "side"), (int)L(r, "vol"), D(r, "price"), D(r, "sl"),
                D(r, "tp"), D(r, "profit"), L(r, "pid"), S(r, "comment"), L(r, "opened")),
            "deal" => Deal(r, seq),
            "hdeal" => new HistoryDealMessage(seq, Deal(r, seq)),
            "hend" => new HistoryEndMessage(seq),
            "bar" => new BarMessage(seq, L(r, "time"), D(r, "o"), D(r, "h"), D(r, "l"), D(r, "c"), (int)L(r, "sec"), L(r, "hist") == 1),
            "barsend" => new BarsEndMessage(seq),
            "acct" => new AccountMessage(seq, D(r, "balance"), D(r, "equity"), D(r, "free"), L(r, "srv"), L(r, "gmt"),
                Flag(r, "conn", true), Flag(r, "algo", true)),
            "ack" => new AckMessage(seq, S(r, "id"), L(r, "ok") == 1, (int)L(r, "ret"), S(r, "msg")),
            "news" => new NewsMessage(seq, L(r, "time"), S(r, "cur"), (int)L(r, "imp"), S(r, "name")),
            "newsend" => new NewsEndMessage(seq),
            "log" => new LogMessage(seq, S(r, "lvl"), S(r, "msg")),
            "trades" => new TradesMessage(seq, Prints(S(r, "d"))),
            "book" => new BookMessage(seq, Levels(S(r, "b")), Levels(S(r, "a"))),
            "flowcaps" => new FlowCapsMessage(seq, (int)L(r, "n"), (int)L(r, "side"), (int)L(r, "vol"), (int)L(r, "depth"), (int)L(r, "hist")),
            var other => new UnknownMessage(seq, other)
        };
    }

    private static DealMessage Deal(JsonElement r, long seq) =>
        new(seq, (ulong)L(r, "ticket"), L(r, "time"), S(r, "side"), (int)L(r, "vol"), D(r, "price"), D(r, "profit"),
            S(r, "entry"), S(r, "comment"), L(r, "pid"), S(r, "sym"));

    // "tms,preço,volume,lado;..." (lado: b = comprador agrediu, s = vendedor agrediu, u = sem agressor)
    private static List<TradePrint> Prints(string data)
    {
        var list = new List<TradePrint>();
        foreach (var item in data.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = item.Split(',');
            if (p.Length != 4) continue;
            list.Add(new TradePrint(long.Parse(p[0], CultureInfo.InvariantCulture), decimal.Parse(p[1], CultureInfo.InvariantCulture),
                decimal.Parse(p[2], CultureInfo.InvariantCulture),
                p[3] switch { "b" => Aggressor.Buy, "s" => Aggressor.Sell, _ => Aggressor.None }));
        }
        return list;
    }

    // "preço:volume,..."
    private static List<BookLevel> Levels(string data) =>
        [.. data.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split(':')).Where(p => p.Length == 2)
            .Select(p => new BookLevel(decimal.Parse(p[0], CultureInfo.InvariantCulture), decimal.Parse(p[1], CultureInfo.InvariantCulture)))];

    // Campo ausente (EA antigo) = valor padrão.
    private static bool Flag(JsonElement e, string name, bool fallback) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() == 1 : fallback;

    private static string S(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static long L(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDecimal() : 0;

    private static decimal D(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;
}

// ---------------- Comandos .NET → MT5 ----------------

public abstract record BridgeCommand(string Id)
{
    public abstract string Type { get; }
}

public sealed record WelcomeCommand(string Nonce) : BridgeCommand("")
{
    public override string Type => "welcome";
}

public sealed record OpenCommand(string Id, Side Side, int Volume, decimal StopLoss, decimal TakeProfit, string Comment)
    : BridgeCommand(Id)
{
    public override string Type => "open";
}

public sealed record ModifyCommand(string Id, decimal StopLoss, decimal TakeProfit) : BridgeCommand(Id)
{
    public override string Type => "modify";
}

/// <summary>Reduz a posição em <paramref name="Volume"/> contratos (ordem contrária no modo netting).</summary>
public sealed record ReduceCommand(string Id, int Volume, string Reason) : BridgeCommand(Id)
{
    public override string Type => "reduce";
}

public sealed record CloseAllCommand(string Id, string Reason) : BridgeCommand(Id)
{
    public override string Type => "closeall";
}

public sealed record HeartbeatCommand() : BridgeCommand("")
{
    public override string Type => "hb";
}

/// <summary>Seta com o nome do setup no gráfico do MT5.</summary>
public sealed record MarkCommand(Side Side, string Text) : BridgeCommand("")
{
    public override string Type => "mark";
}

public sealed record NotifyCommand(string Text) : BridgeCommand("")
{
    public override string Type => "notify";
}

/// <summary>Pede ao EA o histórico de negócios dos últimos N pregões (vira arquivo em MQL5\Files\TradeGuard\ticks).</summary>
public sealed record TickHistoryCommand(int Days) : BridgeCommand("")
{
    public override string Type => "tickhist";
}

/// <summary>Pede ao EA os candles de 5 min dos últimos N dias (série contínua), para teste em anos.</summary>
public sealed record BarHistoryCommand(string Symbol, int Days) : BridgeCommand("")
{
    public override string Type => "barhist";
}

/// <summary>Pede ao EA as notícias de alto impacto dos últimos N dias (para testar o filtro de notícias).</summary>
public sealed record NewsHistoryCommand(int Days) : BridgeCommand("")
{
    public override string Type => "newshist";
}

public static class CommandWriter
{
    /// <summary>JSON plano (só string/número), fácil de ler no MQL5 sem parser completo.</summary>
    public static string ToJson(BridgeCommand cmd, long seq)
    {
        var sb = new StringBuilder();
        sb.Append("{\"t\":\"").Append(cmd.Type).Append("\",\"seq\":").Append(seq);
        void Str(string k, string v) => sb.Append(",\"").Append(k).Append("\":\"").Append(Escape(v)).Append('"');
        void Num(string k, decimal v) => sb.Append(",\"").Append(k).Append("\":").Append(v.ToString(CultureInfo.InvariantCulture));

        switch (cmd)
        {
            case WelcomeCommand w: Str("nonce", w.Nonce); break;
            case OpenCommand o:
                Str("id", o.Id); Str("side", o.Side == Side.Buy ? "buy" : "sell");
                Num("vol", o.Volume); Num("sl", o.StopLoss); Num("tp", o.TakeProfit); Str("comment", o.Comment);
                break;
            case ModifyCommand m: Str("id", m.Id); Num("sl", m.StopLoss); Num("tp", m.TakeProfit); break;
            case ReduceCommand r: Str("id", r.Id); Num("vol", r.Volume); Str("reason", r.Reason); break;
            case CloseAllCommand c: Str("id", c.Id); Str("reason", c.Reason); break;
            case NotifyCommand n: Str("msg", n.Text); break;
            case TickHistoryCommand h: Num("days", h.Days); break;
            case BarHistoryCommand b: Str("sym", b.Symbol); Num("days", b.Days); break;
            case NewsHistoryCommand nh: Num("days", nh.Days); break;
            case MarkCommand k: Str("side", k.Side == Side.Buy ? "buy" : "sell"); Str("text", k.Text); break;
        }
        return sb.Append('}').ToString();
    }

    // O lado MQL5 só entende ASCII sem escapes: remove o que quebraria o parser simples.
    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (ch is '"' or '\\' or '\t' or '\n' or '\r') continue;
            if (ch < 128) sb.Append(ch);
        }
        return sb.ToString();
    }
}
