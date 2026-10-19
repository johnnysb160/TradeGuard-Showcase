using System.Globalization;
using System.Text;
using TradeGuard.Domain;

namespace TradeGuard.UI.Engine;

/// <summary>
/// Grava o fluxo do dia (Times &amp; Trades e book) em dados\fluxo: AAAA-MM-DD-negocios.csv e AAAA-MM-DD-book.csv.
/// É a base do teste das estratégias de fluxo com os ticks reais. Escrita em lote para não pesar.
/// </summary>
public sealed class FlowRecorder(string directory)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private DateTimeOffset _lastBookUtc;

    public void Trades(IReadOnlyList<TradePrint> prints)
    {
        if (prints.Count == 0) return;
        var sb = new StringBuilder();
        foreach (var p in prints)
            sb.Append(p.TimeMsc).Append(';').Append(p.Price.ToString(Inv)).Append(';').Append(p.Volume.ToString(Inv)).Append(';')
              .Append(p.Side switch { Aggressor.Buy => 'b', Aggressor.Sell => 's', _ => 'u' }).Append('\n');
        Append(prints[0].TimeUtc, "negocios", "tms;preco;volume;agressor\n", sb.ToString());
    }

    public void Book(DateTimeOffset nowUtc, IReadOnlyList<BookLevel> bids, IReadOnlyList<BookLevel> asks)
    {
        if (nowUtc - _lastBookUtc < TimeSpan.FromSeconds(1)) return; // 1 foto por segundo basta para o teste
        _lastBookUtc = nowUtc;
        string Side(IReadOnlyList<BookLevel> l) => string.Join(',', l.Select(x => $"{x.Price.ToString(Inv)}:{x.Volume.ToString(Inv)}"));
        Append(nowUtc, "book", "tms;compra;venda\n", $"{nowUtc.ToUnixTimeMilliseconds()};{Side(bids)};{Side(asks)}\n");
    }

    private void Append(DateTimeOffset utc, string kind, string header, string text)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, $"{B3Clock.ToB3(utc):yyyy-MM-dd}-{kind}.csv");
            if (!File.Exists(file)) File.WriteAllText(file, header);
            File.AppendAllText(file, text);
        }
        catch (IOException)
        {
            // Gravação é auxiliar: nunca derruba o robô.
        }
    }
}
