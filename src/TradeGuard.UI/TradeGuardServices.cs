using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradeGuard.Bridge;
using TradeGuard.Domain;
using TradeGuard.Infrastructure;
using TradeGuard.UI.Engine;

namespace TradeGuard.UI;

/// <summary>Chave da ponte em hex, para colar no EA.</summary>
public sealed record BridgeKey(string Hex);

/// <summary>O que a interface pede para a janela do Windows.</summary>
public interface IDesktopShell
{
    bool Topmost { get; set; }

    void CopyText(string text);

    /// <summary>Abre "Salvar como". Devolve false se o usuário cancelar.</summary>
    Task<bool> SaveFileAsync(string suggestedName, string filter, byte[] content);
}

internal sealed class ShiftedTime(TimeSpan shift) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + shift;
}

public static class TradeGuardServices
{
    /// <summary>
    /// Raiz única do TradeGuard. Na instalação normal ela é a pasta pai de app\;
    /// TRADEGUARD_HOME_DIR existe apenas para instalações deliberadamente personalizadas.
    /// </summary>
    public static string HomeDirectory { get; } = ResolveHomeDirectory();

    /// <summary>
    /// Pasta de dados (banco, chave, logs). Fora do AppData de propósito: o Windows redireciona o AppData de
    /// programas abertos por apps empacotados, e aí o mesmo TradeGuard via outro banco. Aqui o caminho é sempre o mesmo.
    /// TRADEGUARD_DATA_DIR troca a pasta (instância de teste, sem tocar nos seus dados).
    /// </summary>
    public static string DataDirectory { get; } =
        Environment.GetEnvironmentVariable("TRADEGUARD_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(HomeDirectory, "dados");

    private static readonly string[] LegacyDataDirectories =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "TradeGuard", "dados"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TradeGuard")
    ];

    private static string ResolveHomeDirectory()
    {
        if (Environment.GetEnvironmentVariable("TRADEGUARD_HOME_DIR") is { Length: > 0 } configured)
            return Path.GetFullPath(configured);

        var app = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (app.Name.Equals("app", StringComparison.OrdinalIgnoreCase) && app.Parent is not null)
            return app.Parent.FullName;

        // Execução de desenvolvimento: mantém o comportamento antigo e não grava dentro de bin\Debug.
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "TradeGuard");
    }

    /// <summary>
    /// Traz os dados de uma instalação antiga uma única vez, sem perder nada: copia tudo,
    /// confere o banco e só então renomeia a origem para ".migrado" (fica como cópia de segurança).
    /// Devolve o que fez, para o registro.
    /// </summary>
    public static string? MigrateLegacyData()
    {
        if (Environment.GetEnvironmentVariable("TRADEGUARD_DATA_DIR") is { Length: > 0 }) return null;
        var newDb = Path.Combine(DataDirectory, "tradeguard.db");
        if (File.Exists(newDb)) return null;
        var legacy = LegacyDataDirectories.FirstOrDefault(d =>
            !Path.GetFullPath(d).Equals(Path.GetFullPath(DataDirectory), StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(d, "tradeguard.db")));
        if (legacy is null) return null;

        Directory.CreateDirectory(DataDirectory);
        foreach (var file in Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(DataDirectory, Path.GetRelativePath(legacy, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
        if (new FileInfo(newDb).Length != new FileInfo(Path.Combine(legacy, "tradeguard.db")).Length)
            throw new IOException("Cópia do banco não confere; a pasta antiga foi mantida intacta.");

        var archived = legacy + ".migrado";
        if (Directory.Exists(archived)) archived += "-" + DateTime.Now.ToString("yyyyMMddHHmmss");
        Directory.Move(legacy, archived);
        return $"Dados trazidos de {legacy} para {DataDirectory} (a antiga ficou em {archived}).";
    }

    public static IServiceCollection AddTradeGuard(this IServiceCollection services, BridgeOptions bridgeOptions, Mt5.Mt5Options mt5Options)
    {
        services.AddSingleton(mt5Options);
        services.AddSingleton<PortfolioRiskCoordinator>();
        services.AddSingleton<ParityAuditStore>();
        services.AddSingleton<ShadowLearningService>();
        services.AddHostedService<ShadowLearningCycleHost>();
        services.AddHostedService<Mt5.Mt5Orchestrator>();

        Directory.CreateDirectory(DataDirectory);
        var masterKey = new SecretStore(Path.Combine(DataDirectory, "bridge.key")).GetOrCreateMasterKey();

        services.AddDbContextFactory<TradeGuardDb>(o =>
            o.UseSqlite($"Data Source={Path.Combine(DataDirectory, "tradeguard.db")}"));
        services.AddSingleton(sp => new JournalStore(
            sp.GetRequiredService<IDbContextFactory<TradeGuardDb>>(),
            Path.Combine(DataDirectory, "backups")));
        services.AddSingleton<SettingsStore>();
        // Só para o teste de ponta a ponta (instância de teste): simula outro horário do pregão.
        services.AddSingleton(Environment.GetEnvironmentVariable("TRADEGUARD_TEST_TIME_OFFSET_MINUTES") is { } offset
                              && Environment.GetEnvironmentVariable("TRADEGUARD_DATA_DIR") is { Length: > 0 }
                              && int.TryParse(offset, out var minutes)
            ? new ShiftedTime(TimeSpan.FromMinutes(minutes))
            : TimeProvider.System);
        services.AddSingleton<B3Clock>();
        services.AddSingleton(bridgeOptions);
        services.AddSingleton(new BridgeKey(Convert.ToHexStringLower(masterKey)));
        services.AddSingleton(sp => new BridgeServer(bridgeOptions, masterKey, sp.GetRequiredService<ILogger<BridgeServer>>()));
        services.AddSingleton<TradingEngine>();
        services.AddHostedService(sp => sp.GetRequiredService<TradingEngine>());

        // Segundo motor: mini índice (WIN), no seu próprio canal. Mesmas regras, mesmo diário, travas do dia somadas.
        services.AddSingleton(sp =>
        {
            var winOptions = new BridgeOptions
            {
                PipeName = bridgeOptions.PipeName + Mt5.Mt5Orchestrator.WinPipeSuffix,
                HandshakeTimeoutSeconds = bridgeOptions.HandshakeTimeoutSeconds,
                IdleTimeoutSeconds = bridgeOptions.IdleTimeoutSeconds
            };
            var engine = new TradingEngine(
                new BridgeServer(winOptions, masterKey, sp.GetRequiredService<ILogger<BridgeServer>>()),
                sp.GetRequiredService<JournalStore>(), sp.GetRequiredService<SettingsStore>(), sp.GetRequiredService<B3Clock>(),
                sp.GetRequiredService<PortfolioRiskCoordinator>(),
                sp.GetRequiredService<ParityAuditStore>(),
                sp.GetRequiredService<ShadowLearningService>(),
                sp.GetRequiredService<ILogger<TradingEngine>>()) { IsPrimary = false, Label = "WIN" };
            return new SecondaryEngines([engine]);
        });
        // Tipo próprio: o .NET ignora um segundo serviço de fundo registrado com o mesmo tipo (TradingEngine).
        services.AddHostedService<SecondaryEnginesHost>();
        return services;
    }
}

/// <summary>Motores além do principal (hoje: WIN). A tela mostra um resumo de cada um.</summary>
public sealed record SecondaryEngines(IReadOnlyList<TradingEngine> All);

/// <summary>Liga e desliga os motores extras junto com o app.</summary>
public sealed class SecondaryEnginesHost(SecondaryEngines engines) : Microsoft.Extensions.Hosting.IHostedService
{
    public Task StartAsync(CancellationToken ct) => Task.WhenAll(engines.All.Select(e => e.StartAsync(ct)));

    public Task StopAsync(CancellationToken ct) => Task.WhenAll(engines.All.Select(e => e.StopAsync(ct)));
}
