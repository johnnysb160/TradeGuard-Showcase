using System.Globalization;
using System.Text;
using TradeGuard.Domain;

namespace TradeGuard.UI.Engine;

/// <summary>Diário em CSV (separador ";" e BOM UTF-8, abre direto no Excel em português).</summary>
public static class JournalCsv
{
    public static byte[] Build(IEnumerable<TradeRecord> trades)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("dia;abertura;fechamento;lado;contratos;entrada;saida;stop_inicial;pontos;reais;risco_inicial;R;mfe;mae;origem;nota;desconsiderado\n");
        foreach (var t in trades)
            sb.AppendLine(string.Join(';',
                t.TradingDay.ToString("yyyy-MM-dd"), B3Clock.ToB3(t.OpenedAtUtc).ToString("HH:mm:ss"),
                B3Clock.ToB3(t.ClosedAtUtc).ToString("HH:mm:ss"), t.Side == Side.Buy ? "C" : "V", t.Contracts,
                t.EntryPrice.ToString(inv), t.ExitPrice.ToString(inv), t.InitialStop.ToString(inv), t.PnlPoints.ToString(inv),
                t.PnlMoney.ToString(inv), t.InitialRiskMoney.ToString(inv), t.RMultiple.ToString(inv),
                t.MfePoints.ToString(inv), t.MaePoints.ToString(inv), t.Source, (t.Notes ?? "").Replace(';', ','), t.Excluded ? "sim" : "nao"));
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }
}
