using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradeGuard.Bridge;
using TradeGuard.UI;
using TradeGuard.UI.Mt5;

namespace TradeGuard.Desktop;

public partial class App : Application
{
    // Dois TradeGuard abertos brigariam pelo mesmo canal e mandariam ordens em dobro.
    // Instância de teste (TRADEGUARD_DATA_DIR) tem banco e canal próprios, então usa outro nome.
    private static readonly Mutex SingleInstance = new(true,
        @"Local\TradeGuard.SingleInstance" + (Environment.GetEnvironmentVariable("TRADEGUARD_DATA_DIR") is { Length: > 0 } d ? "." + d.GetHashCode() : ""),
        out IsFirstInstance);
    private static readonly bool IsFirstInstance;

    private IHost? _host;
    private ILogger? _log;

    public App()
    {
        // Números e datas no formato brasileiro (R$ 1.234,56), independente do idioma do Windows.
        // Precisa ser aqui (síncrono): dentro de um método async a cultura não se propaga para o resto do app.
        var ptBr = CultureInfo.GetCultureInfo("pt-BR");
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = ptBr;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = ptBr;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Aberto de dentro de um app empacotado: o Windows daria outra pasta de dados (outro banco, outra chave).
        if (SystemIntegration.RunsInsidePackage() && Environment.GetEnvironmentVariable("TRADEGUARD_DATA_DIR") is null)
        {
            if (IsFirstInstance) SingleInstance.ReleaseMutex();
            SystemIntegration.RelaunchOutsidePackage(e.Args);
            Shutdown();
            return;
        }

        if (!IsFirstInstance)
        {
            BringExistingToFront();
            Shutdown();
            return;
        }

        // Dados na raiz da instalação (<install-root>\dados). Na primeira vez, traz tudo da pasta antiga.
        string? migrated;
        try { migrated = TradeGuardServices.MigrateLegacyData(); }
        catch (Exception ex)
        {
            MessageBox.Show("Não consegui trazer seus dados da pasta antiga. Nada foi apagado.\n\n" + ex.Message,
                "TradeGuard", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        // Fundo do WebView2 na cor do tema: evita o "piscar branco" ao abrir.
        Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "FF0B0F14");

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
            Args = e.Args
        });
        builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(TradeGuardServices.DataDirectory, "logs")));
        var bridgeOptions = builder.Configuration.GetSection("Bridge").Get<BridgeOptions>() ?? new BridgeOptions();
        var mt5Options = builder.Configuration.GetSection("Mt5").Get<Mt5Options>() ?? new Mt5Options();

        builder.Services.AddWpfBlazorWebView();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif
        builder.Services.AddSingleton<DesktopShell>();
        builder.Services.AddSingleton<IDesktopShell>(sp => sp.GetRequiredService<DesktopShell>());
        builder.Services.AddTradeGuard(bridgeOptions, mt5Options);

        _host = builder.Build();
        _log = _host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("App");
        DispatcherUnhandledException += (_, args) =>
        {
            _log.LogError(args.Exception, "Erro não tratado na interface");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _log.LogCritical(args.ExceptionObject as Exception, "Erro fatal");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log.LogError(args.Exception, "Erro em tarefa de fundo");
            args.SetObserved();
        };

        _log.LogInformation("TradeGuard iniciado ({Exe}) · dados em {Data}", Environment.ProcessPath, TradeGuardServices.DataDirectory);
        if (migrated is not null) _log.LogInformation("{Migrated}", migrated);
        SystemIntegration.KeepAwake(true);
        try { SystemIntegration.EnsureShortcuts(); }
        catch (Exception ex) { _log.LogWarning(ex, "Não foi possível criar os atalhos"); }

        await _host.StartAsync();

        // Atalho opcional: TradeGuard.exe --pagina regras
        var page = e.Args.SkipWhile(a => a != "--pagina").Skip(1).FirstOrDefault();
        var window = new MainWindow(_host.Services, page is null ? "/" : "/" + page.Trim('/'));
        MainWindow = window;
        window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        SystemIntegration.KeepAwake(false);
        if (_host is not null)
        {
            _log?.LogInformation("TradeGuard encerrado");
            await _host.StopAsync(TimeSpan.FromSeconds(3));
            _host.Dispose();
        }
        if (IsFirstInstance)
        {
            try { SingleInstance.ReleaseMutex(); }
            catch (ApplicationException) { } // já liberado ao reabrir fora do pacote
        }
        base.OnExit(e);
    }

    // Abrir o TradeGuard de novo só traz a janela que já está aberta para a frente.
    private static void BringExistingToFront()
    {
        var me = Environment.ProcessId;
        var other = Process.GetProcessesByName("TradeGuard").FirstOrDefault(p => p.Id != me && p.MainWindowHandle != IntPtr.Zero);
        if (other is null) return;
        ShowWindow(other.MainWindowHandle, 9); // SW_RESTORE
        SetForegroundWindow(other.MainWindowHandle);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);
}
