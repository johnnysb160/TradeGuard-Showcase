using System.Globalization;
using TradeGuard.Domain;

namespace TradeGuard.UI.Engine;

/// <summary>
/// Negócios dos pregões passados que o EA exportou (MQL5\Files\TradeGuard\ticks\AAAA-MM-DD.csv, horário do servidor)
/// mais os gravados ao vivo pelo app (dados\fluxo). Base do teste tick a tick das estratégias de fluxo.
/// </summary>
public static class TickHistory
{
    /// <summary>Pasta do MT5 onde o EA grava (definida pelo orquestrador ao achar o MT5).</summary>
    public static string? Mt5Directory { get; set; }

    /// <summary>Diferença horário do servidor − UTC, em segundos (definida ao conectar).</summary>
    public static long ServerOffsetSeconds { get; set; }

    public static IReadOnlyList<DateOnly> AvailableDays() =>
        Mt5Directory is { } dir && Directory.Exists(dir)
            ? [.. Directory.GetFiles(dir, "????-??-??.csv")
                .Select(f => DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", out var d) ? d : default)
                .Where(d => d != default).Order()]
            : [];

    /// <summary>Um pregão por vez (não carrega tudo na memória).</summary>
    public static IEnumerable<IReadOnlyList<TradePrint>> LoadDays()
    {
        foreach (var day in AvailableDays())
            yield return Load(Path.Combine(Mt5Directory!, $"{day:yyyy-MM-dd}.csv"));
    }

    private static List<TradePrint> Load(string file)
    {
        var inv = CultureInfo.InvariantCulture;
        var offsetMs = ServerOffsetSeconds * 1000;
        var list = new List<TradePrint>(200_000);
        foreach (var line in File.ReadLines(file))
        {
            var p = line.Split(';');
            if (p.Length != 4 || !long.TryParse(p[0], inv, out var tms)) continue;
            list.Add(new TradePrint(tms - offsetMs, decimal.Parse(p[1], inv), decimal.Parse(p[2], inv),
                p[3] switch { "b" => Aggressor.Buy, "s" => Aggressor.Sell, _ => Aggressor.None }));
        }
        return list;
    }
}
