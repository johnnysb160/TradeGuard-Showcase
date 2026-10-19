using System.Globalization;

namespace TradeGuard.UI.Mt5;

public sealed record Mt5Account(long Login, string Server);

/// <summary>
/// Lembra a última conta (número + servidor) em que o EA conectou, para o MT5 abrir sempre nela.
/// A senha NÃO fica aqui: o MT5 usa a que ele mesmo guarda cifrada ("Salvar senha" no login).
/// </summary>
public static class Mt5AccountMemory
{
    private static readonly string File = Path.Combine(TradeGuardServices.DataDirectory, "mt5-account.txt");
    private static readonly object Sync = new();

    public static Mt5Account? Load()
    {
        lock (Sync) return LoadCore();
    }

    private static Mt5Account? LoadCore()
    {
        try
        {
            if (!System.IO.File.Exists(File)) return null;
            var parts = System.IO.File.ReadAllText(File).Split(';', 2);
            return parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var login)
                   && login > 0 && parts[1].Length > 0
                ? new Mt5Account(login, parts[1].Trim())
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static void Save(long login, string server)
    {
        if (login <= 0 || string.IsNullOrWhiteSpace(server)) return;
        lock (Sync)
        {
            if (LoadCore() is { } current && current.Login == login && current.Server == server) return;
            Directory.CreateDirectory(TradeGuardServices.DataDirectory);
            var temp = File + ".tmp";
            System.IO.File.WriteAllText(temp, $"{login.ToString(CultureInfo.InvariantCulture)};{server}");
            System.IO.File.Move(temp, File, true);
        }
    }
}
