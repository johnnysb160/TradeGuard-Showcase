using System.ComponentModel;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TradeGuard.UI.Engine;

namespace TradeGuard.Desktop;

public partial class MainWindow : Window
{
    private readonly TradingEngine _engine;
    private readonly TrayNotifier _tray;
    private readonly SoundAttention _attention;
    private string _lastTitle = "";

    public MainWindow(IServiceProvider services, string startPath = "/")
    {
        InitializeComponent();
        WebView.BlazorWebViewInitializing += (_, e) =>
        {
            // Cache da tela junto com os dados (nada no AppData).
            e.UserDataFolder = System.IO.Path.Combine(TradeGuard.UI.TradeGuardServices.DataDirectory, "webview");
            // Só na instância de teste (tools\e2e): porta de depuração para o teste dirigir a interface.
            if (Environment.GetEnvironmentVariable("TRADEGUARD_DATA_DIR") is { Length: > 0 }
                && Environment.GetEnvironmentVariable("TRADEGUARD_CDP_PORT") is { Length: > 0 } port)
                e.EnvironmentOptions = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions($"--remote-debugging-port={port}");
        };
        WebView.StartPath = startPath;
        WebView.Services = services;
        _engine = services.GetRequiredService<TradingEngine>();
        services.GetRequiredService<DesktopShell>().Attach(this);

        _tray = new TrayNotifier(this);
        _engine.Alerted += _tray.Notify;
        var others = services.GetRequiredService<TradeGuard.UI.SecondaryEngines>().All;
        _attention = new SoundAttention(this, _engine, others);
        // Avisos do segundo motor (WIN) também chegam no Windows.
        foreach (var other in others) other.Alerted += _tray.Notify;
        _engine.Changed += OnEngineChanged;
        OnEngineChanged();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowChrome.ApplyDark(this);
    }

    /// <summary>Status na barra de título, na barra de tarefas e no ícone da bandeja.</summary>
    private void OnEngineChanged()
    {
        var status = StatusText.From(_engine.State);
        var title = $"TradeGuard · {status.Headline}";
        if (title == _lastTitle) return;
        _lastTitle = title;
        Dispatcher.BeginInvoke(() => Title = title);
        _tray.SetTooltip(title);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_engine.State.Position is not null)
        {
            var answer = MessageBox.Show(
                "Há uma posição aberta.\n\nO stop continua no MT5, mas sem o TradeGuard não há break-even, " +
                "parcial nem travas do dia.\n\nFechar mesmo assim?",
                "TradeGuard", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        _engine.Changed -= OnEngineChanged;
        _engine.Alerted -= _tray.Notify;
        _attention.Dispose();
        _tray.Dispose();
        base.OnClosing(e);
    }
}
