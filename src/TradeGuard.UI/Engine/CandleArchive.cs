using System.Globalization;
using TradeGuard.Domain;

namespace TradeGuard.UI.Engine;

/// <summary>
/// Todos os candles de 5 min que o MT5 já mandou, juntos em dados\candles\historico.csv (cresce a cada pregão).
/// É a base do "Teste dos setups" no Diário.
/// </summary>
public static class CandleArchive
{
    private static readonly object Gate = new();
    private static string File => Path.Combine(TradeGuardServices.DataDirectory, "candles", "historico.csv");

    public static void Merge(IReadOnlyList<Candle> candles)
    {
        lock (Gate)
        {
            var all = LoadUnlocked().ToDictionary(c => c.OpenUtc);
            foreach (var c in candles) all[c.OpenUtc] = c;
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            var inv = CultureInfo.InvariantCulture;
            System.IO.File.WriteAllLines(File, all.Values.OrderBy(c => c.OpenUtc).Select(c =>
                string.Join(';', c.OpenUtc.ToUnixTimeSeconds(), c.Open.ToString(inv), c.High.ToString(inv), c.Low.ToString(inv), c.Close.ToString(inv))));
        }
    }

    public static IReadOnlyList<Candle> Load()
    {
        lock (Gate) return LoadUnlocked();
    }

    private static List<Candle> LoadUnlocked()
    {
        if (!System.IO.File.Exists(File)) return [];
        var inv = CultureInfo.InvariantCulture;
        return System.IO.File.ReadAllLines(File)
            .Select(l => l.Split(';'))
            .Where(p => p.Length == 5)
            .Select(p => new Candle(DateTimeOffset.FromUnixTimeSeconds(long.Parse(p[0], inv)),
                decimal.Parse(p[1], inv), decimal.Parse(p[2], inv), decimal.Parse(p[3], inv), decimal.Parse(p[4], inv)))
            .ToList();
    }
}
