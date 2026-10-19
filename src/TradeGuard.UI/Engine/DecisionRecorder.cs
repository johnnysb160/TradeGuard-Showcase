using System.Globalization;
using TradeGuard.Domain;

namespace TradeGuard.UI.Engine;

/// <summary>Trilha estruturada para comparar, sem interpretar texto de log, o que a regra decidiu e executou.</summary>
internal static class DecisionRecorder
{
    private static readonly object Sync = new();

    public static void Write(string instrument, DateOnly day, DateTimeOffset atUtc, string setup, string stage,
        int side, decimal adr, decimal? move, decimal? limit, decimal? bid, decimal? ask, string outcome, string detail)
    {
        try
        {
            lock (Sync)
            {
                var dir = Path.Combine(TradeGuardServices.DataDirectory, "decisoes");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"{day:yyyy-MM-dd}-{instrument.ToLowerInvariant()}.csv");
                if (!File.Exists(path))
                    File.WriteAllText(path, "utc;hora_b3;ativo;setup;etapa;lado;adr;movimento;limite;bid;ask;resultado;detalhe\n");
                var b3 = B3Clock.ToB3(atUtc);
                var inv = CultureInfo.InvariantCulture;
                string N(decimal? value) => value?.ToString(inv) ?? "";
                string S(string value) => value.Replace(';', ',').Replace('\r', ' ').Replace('\n', ' ');
                File.AppendAllText(path, string.Join(';', atUtc.ToUniversalTime().ToString("O"), b3.ToString("HH:mm:ss.fff"),
                    instrument, S(setup), stage, side, adr.ToString(inv), N(move), N(limit), N(bid), N(ask), S(outcome), S(detail)) + "\n");
            }
        }
        catch (Exception ex)
        {
            // Não derruba uma posição em andamento, mas bloqueia a próxima entrada: sem trilha não há operação silenciosa.
            AuditStorage.MarkFailure(ex);
        }
    }
}
