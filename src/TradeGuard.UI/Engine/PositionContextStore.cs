using System.Text.Json;
using TradeGuard.Domain;

namespace TradeGuard.UI.Engine;

internal sealed record PersistedPositionContext(
    string Instrument, string Symbol, Side Side, string? Setup, string Source,
    decimal StopLoss, decimal TakeProfit, DateTimeOffset CreatedAtUtc, long PositionId = 0,
    string? ShadowSignalId = null);

/// <summary>
/// Pequeno estado de recuperação gravado antes de enviar a ordem. O comentário do MT5 é a segunda cópia;
/// este arquivo preserva também fonte, stop e alvo se o app cair entre o envio e a confirmação.
/// </summary>
internal static class PositionContextStore
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static bool Save(PersistedPositionContext context)
    {
        lock (Sync)
        {
            var path = PathFor(context.Instrument);
            var temp = path + ".tmp";
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(context, Json));
                File.Move(temp, path, true);
                return true;
            }
            catch
            {
                if (File.Exists(temp)) File.Delete(temp);
                return false;
            }
        }
    }

    public static PersistedPositionContext? Load(string instrument)
    {
        lock (Sync)
        {
            var path = PathFor(instrument);
            if (!File.Exists(path)) return null;
            try
            {
                var value = JsonSerializer.Deserialize<PersistedPositionContext>(File.ReadAllText(path), Json);
                if (value is null || DateTimeOffset.UtcNow - value.CreatedAtUtc > TimeSpan.FromDays(1))
                {
                    File.Delete(path);
                    return null;
                }
                return value;
            }
            catch
            {
                try { File.Delete(path); } catch { /* será alertado pelo motor se precisar recuperar */ }
                return null;
            }
        }
    }

    public static void Clear(string instrument)
    {
        lock (Sync)
        {
            var path = PathFor(instrument);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string PathFor(string instrument) =>
        Path.Combine(TradeGuardServices.DataDirectory, $"posicao-{instrument.ToLowerInvariant()}.json");
}
