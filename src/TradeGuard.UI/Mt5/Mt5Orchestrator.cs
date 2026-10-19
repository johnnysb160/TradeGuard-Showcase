using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradeGuard.Bridge;
using TradeGuard.UI.Engine;

namespace TradeGuard.UI.Mt5;

public sealed class Mt5Options
{
    /// <summary>Pasta do MT5. Vazio = encontra sozinho.</summary>
    public string? InstallDir { get; set; }
    /// <summary>Gráfico inicial. A série contínua serve: o EA troca sozinho para o contrato vigente.</summary>
    public string Symbol { get; set; } = "WDO$";
    /// <summary>Abre o MT5 junto com o TradeGuard.</summary>
    public bool AutoStart { get; set; } = true;
    /// <summary>Reabre o MT5 se ele fechar e reinicia se o EA não conectar.</summary>
    public bool Watchdog { get; set; } = true;
}

/// <summary>
/// Faz o "trabalho braçal" do MT5 sozinho: encontra a instalação, instala/atualiza e compila o EA,
/// grava a chave, abre o MT5 com o gráfico e o EA prontos, e vigia para reabrir se cair.
/// </summary>
public sealed class Mt5Orchestrator(
    Mt5Options options,
    BridgeOptions bridgeOptions,
    BridgeKey key,
    TradingEngine engine,
    SecondaryEngines secondaryEngines,
    TradeGuard.Infrastructure.SettingsStore settings,
    TradeGuard.Domain.B3Clock clock,
    BridgeServer bridge,
    ILogger<Mt5Orchestrator> logger) : BackgroundService
{
    private const string EaName = "TradeGuardBridge";
    private const string ProfileName = "TradeGuard";
    private static readonly TimeSpan NotConnectedRestartAfter = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MinRestartInterval = TimeSpan.FromMinutes(5);

    private Mt5Paths? _paths;
    private bool _restartPending;
    private string _restartReason = "EA atualizado — reiniciando o MT5 para carregar a nova versão";
    private int _seenRejections;
    private bool _presetAllowReal;
    private TimeOnly _presetHardClose = new(17, 50);
    private DateTimeOffset _lastStart = DateTimeOffset.MinValue;
    private readonly Dictionary<string, DateTimeOffset> _disconnectedSince = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _brokerOkAt = DateTimeOffset.UtcNow;
    private bool _loginAlertSent;
    private bool _maximizePending = true;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.AutoStart) return;
        await Task.Delay(TimeSpan.FromSeconds(1), ct); // deixa o motor subir antes

        _paths = Mt5Locator.Find(options.InstallDir);
        if (_paths is null)
        {
            engine.Report("error", "MT5 não encontrado neste PC. Instale o MetaTrader 5 da Clear.");
            return;
        }
        logger.LogInformation("MT5 em {Install} (dados em {Data})", _paths.InstallDir, _paths.DataDir);
        TickHistory.Mt5Directory = Path.Combine(_paths.DataDir, "MQL5", "Files", "TradeGuard", "ticks");

        try
        {
            var eaChanged = await DeployExpertAsync(_paths, ct);
            var current = await settings.EffectiveAsync(clock.TradingDay);
            _presetAllowReal = current.AllowRealAccount;
            _presetHardClose = current.EmergencyFlatten;
            WritePreset(_paths);
            WriteStartupConfig(_paths);

            if (FindTerminal(_paths) is null)
                Start(_paths, "abrindo o MT5 com o TradeGuard");
            else if (eaChanged)
                _restartPending = true; // EA novo só entra reiniciando o MT5
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha preparando o MT5");
            engine.Report("error", "Falha preparando o MT5: " + ex.Message);
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { Supervise(_paths); }
            catch (Exception ex) { logger.LogWarning(ex, "Vigia do MT5"); }
        }
    }

    private void Supervise(Mt5Paths paths)
    {
        var state = engine.State;
        var allEngines = new[] { engine }.Concat(secondaryEngines.All).ToList();
        var states = allEngines.Select(e => (Engine: e, State: e.State)).ToList();
        var now = DateTimeOffset.UtcNow;
        foreach (var item in states)
        {
            if (item.State.Connected) _disconnectedSince[item.Engine.Label] = now;
            else _disconnectedSince.TryAdd(item.Engine.Label, now);
        }

        var terminal = FindTerminal(paths);
        if (terminal is null)
        {
            if (options.Watchdog && now - _lastStart > TimeSpan.FromSeconds(30))
                Start(paths, "o MT5 foi fechado — reabrindo");
            return;
        }

        // Liberou/bloqueou conta real nas Regras: o EA precisa receber a mesma decisão (entra reiniciando o MT5).
        // Mudou o horário de zerar: a zeragem de emergência do EA acompanha (entra reiniciando o MT5 sem posição).
        if (state.TradingDay != default && state.Settings.EmergencyFlatten != _presetHardClose)
        {
            _presetHardClose = state.Settings.EmergencyFlatten;
            WritePreset(paths);
            _restartPending = true;
            engine.Report("info", $"Zeragem de emergência do EA passa para {_presetHardClose:HH\\:mm} (aplica no próximo momento sem posição).");
        }

        if (state.TradingDay != default && state.Settings.AllowRealAccount != _presetAllowReal)
        {
            _presetAllowReal = state.Settings.AllowRealAccount;
            WritePreset(paths);
            _restartPending = true;
            engine.Report("info", _presetAllowReal
                ? "Conta real liberada: o EA será atualizado no próximo momento sem posição."
                : "Conta real bloqueada: o EA será atualizado no próximo momento sem posição.");
        }

        // EA recusado pela chave: ele carregou uma chave antiga. Reinicia já (o preset tem a chave certa), sem esperar 90 s.
        if (bridge.RejectedHandshakes > _seenRejections)
        {
            _seenRejections = bridge.RejectedHandshakes;
            if (!state.Connected)
            {
                _restartPending = true;
                _restartReason = "EA com chave antiga — reiniciando o MT5 com a chave certa";
            }
        }

        WatchLogin(state, now);

        if (_maximizePending && state.Connected)
        {
            _maximizePending = false;
            MaximizeChart(terminal);
        }

        var stuckLabel = states.Where(x => !x.State.Connected
                && now - _disconnectedSince[x.Engine.Label] > NotConnectedRestartAfter)
            .Select(x => x.Engine.Label).FirstOrDefault();
        var stuck = stuckLabel is not null;

        if ((_restartPending || (options.Watchdog && stuck)) && now - _lastStart > MinRestartInterval)
        {
            if (states.Any(x => x.State.Position is not null && x.State.Connected))
            {
                return; // nunca reinicia com posição aberta e EA funcionando; espera zerar
            }
            Restart(terminal, paths, _restartPending ? _restartReason
                : $"EA do {stuckLabel} sem conectar há mais de 90 s — reiniciando o MT5");
            _restartPending = false;
            _restartReason = "EA atualizado — reiniciando o MT5 para carregar a nova versão";
        }
    }

    /// <summary>
    /// MT5 aberto e EA conectado, mas sem entrar na conta por 60 s: quase sempre é senha não salva ou trocada.
    /// Avisa uma vez, dizendo exatamente o que fazer (a senha é do MT5; o TradeGuard não guarda nem digita senha).
    /// </summary>
    private void WatchLogin(EngineState state, DateTimeOffset now)
    {
        if (!state.Connected || state.BrokerConnected)
        {
            _brokerOkAt = now;
            if (state.BrokerConnected) _loginAlertSent = false;
            return;
        }
        if (_loginAlertSent || now - _brokerOkAt < TimeSpan.FromSeconds(60)) return;

        _loginAlertSent = true;
        var account = Mt5AccountMemory.Load();
        var which = account is null ? "na sua conta" : $"na conta {account.Login} ({account.Server})";
        var text = $"O MT5 não conseguiu entrar {which}. Abra o MT5 (Arquivo → Conectar-se a uma conta de negociação), " +
                   "digite a senha UMA vez e marque 'Salvar senha'. Depois disso ele entra sozinho.";
        engine.Report("error", text);
        engine.RaiseAlert("MT5 sem login", text);
    }

    // ---------------------------------------------------------------- EA

    private async Task<bool> DeployExpertAsync(Mt5Paths paths, CancellationToken ct)
    {
        var source = ReadEmbeddedEa();
        Directory.CreateDirectory(paths.Experts);
        var mq5 = Path.Combine(paths.Experts, EaName + ".mq5");
        var ex5 = Path.Combine(paths.Experts, EaName + ".ex5");

        var same = File.Exists(mq5) && File.Exists(ex5) && Hash(File.ReadAllBytes(mq5)) == Hash(source);
        if (same) return false;

        await File.WriteAllBytesAsync(mq5, source, ct);
        var ok = await CompileAsync(paths, mq5, ct);
        engine.Report(ok ? "info" : "error", ok
            ? "EA do MT5 instalado/atualizado e compilado."
            : $"Falha ao compilar o EA no MetaEditor (veja os logs em {TradeGuardServices.DataDirectory}\\logs).");
        return ok;
    }

    private async Task<bool> CompileAsync(Mt5Paths paths, string mq5, CancellationToken ct)
    {
        // O MetaEditor não aceita espaços nos caminhos da linha de comando: usa o nome curto (8.3).
        var log = Path.Combine(Path.GetTempPath(), "tradeguard-compile.log");
        var psi = new ProcessStartInfo(paths.MetaEditor,
            $"/compile:\"{ShortPath(mq5)}\" /inc:\"{ShortPath(Path.Combine(paths.DataDir, "MQL5"))}\" /log:\"{log}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync(ct);

        var text = File.Exists(log) ? await File.ReadAllTextAsync(log, Encoding.Unicode, ct) : "";
        logger.LogInformation("Compilação do EA: {Log}", text.Trim());
        return System.Text.RegularExpressions.Regex.IsMatch(text, @"Result:\s*0 errors") && File.Exists(Path.ChangeExtension(mq5, ".ex5"));
    }

    private static byte[] ReadEmbeddedEa()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(EaName + ".mq5")
                      ?? throw new InvalidOperationException("EA embutido não encontrado.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private void WritePreset(Mt5Paths paths)
    {
        Directory.CreateDirectory(paths.Presets);
        var set = $"""
            InpPipe={bridgeOptions.PipeName}
            InpSecret={key.Hex}
            InpAllowReal={(_presetAllowReal ? "true" : "false")}
            InpHardClose={_presetHardClose:HH\:mm}
            InpB3UtcOffsetMinutes={(int)TradeGuard.Domain.B3Clock.Zone.GetUtcOffset(clock.UtcNow).TotalMinutes}
            InpFailSafeStopPts=10.0
            InpAppTimeoutSec=10
            InpPushNotifications=true
            InpRolloverDays=2
            InpHistoryDays=30
            InpDeviationTicks=2
            InpOpenCharts=WIN$
            """;
        File.WriteAllText(Path.Combine(paths.Presets, EaName + ".set"), set, Encoding.Unicode);
        WriteWinTemplate(paths);
    }

    /// <summary>Canal do mini índice: o EA do WIN conecta num pipe próprio, atendido pelo segundo motor.</summary>
    public const string WinPipeSuffix = ".WIN";

    /// <summary>
    /// Modelo de gráfico com o EA configurado para o WIN (canal próprio, stop de emergência na escala do índice).
    /// O EA do dólar abre o gráfico do WIN$ e aplica este modelo.
    /// </summary>
    private void WriteWinTemplate(Mt5Paths paths)
    {
        var templates = Path.Combine(paths.DataDir, "MQL5", "Profiles", "Templates");
        var baseTpl = Path.Combine(templates, "default.tpl");
        if (!File.Exists(baseTpl)) return;
        var text = File.ReadAllText(baseTpl, Encoding.Unicode);
        var close = text.LastIndexOf("</chart>", StringComparison.Ordinal);
        if (close < 0) return;
        var expert = $"""
            <expert>
            name={EaName}
            path=Experts\{EaName}.ex5
            expertmode=5
            <inputs>
            InpPipe={bridgeOptions.PipeName}{WinPipeSuffix}
            InpSecret={key.Hex}
            InpAllowReal={(_presetAllowReal ? "true" : "false")}
            InpHardClose={_presetHardClose:HH\:mm}
            InpB3UtcOffsetMinutes={(int)TradeGuard.Domain.B3Clock.Zone.GetUtcOffset(clock.UtcNow).TotalMinutes}
            InpFailSafeStopPts=400.0
            InpAppTimeoutSec=10
            InpPushNotifications=true
            InpRolloverDays=2
            InpHistoryDays=30
            InpDeviationTicks=2
            InpOpenCharts=
            </inputs>
            </expert>

            """;
        File.WriteAllText(Path.Combine(templates, "TradeGuardWIN.tpl"), text[..close] + expert + text[close..], Encoding.Unicode);
    }

    private string StartupConfigPath(Mt5Paths paths) => Path.Combine(paths.Config, "tradeguard-start.ini");

    private void WriteStartupConfig(Mt5Paths paths)
    {
        Directory.CreateDirectory(paths.Config);
        // Abre sempre na última conta usada (demo ou real). Sem senha aqui: o MT5 usa a que ele guarda cifrada.
        var account = Mt5AccountMemory.Load() is { } a ? $"[Common]{Environment.NewLine}Login={a.Login}{Environment.NewLine}Server={a.Server}{Environment.NewLine}" : "";
        var ini = account + $"""
            [Experts]
            AllowLiveTrading=1
            AllowDllImport=0
            Enabled=1
            [StartUp]
            Expert={EaName}
            ExpertParameters={EaName}.set
            Symbol={options.Symbol}
            Period=M5
            """;
        File.WriteAllText(StartupConfigPath(paths), ini, Encoding.Unicode);
    }

    // ---------------------------------------------------------------- processo

    private static Process? FindTerminal(Mt5Paths paths) =>
        Process.GetProcessesByName("terminal64").FirstOrDefault(p =>
        {
            try { return string.Equals(p.MainModule?.FileName, paths.Terminal, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        });

    private void Start(Mt5Paths paths, string reason)
    {
        // Perfil próprio, limpo a cada abertura: um único gráfico com o EA (o MT5 não salva EA vindo da inicialização
        // e acumularia gráficos). Os gráficos pessoais do perfil "Default" não são tocados.
        WriteStartupConfig(paths); // conta mais recente
        var profile = Path.Combine(paths.Profiles, ProfileName);
        Directory.CreateDirectory(profile);
        foreach (var chr in Directory.EnumerateFiles(profile, "*.chr")) File.Delete(chr);

        Process.Start(new ProcessStartInfo(paths.Terminal,
            $"/profile:{ProfileName} /config:\"{StartupConfigPath(paths)}\"")
        {
            UseShellExecute = false,
            WorkingDirectory = paths.InstallDir
        });
        _lastStart = DateTimeOffset.UtcNow;
        _disconnectedSince[engine.Label] = _lastStart;
        foreach (var secondary in secondaryEngines.All) _disconnectedSince[secondary.Label] = _lastStart;
        _maximizePending = true;
        engine.Report("info", "MT5: " + reason + ".");
    }

    private void Restart(Process terminal, Mt5Paths paths, string reason)
    {
        engine.Report("warn", "MT5: " + reason + "…");
        terminal.CloseMainWindow();
        if (!terminal.WaitForExit(TimeSpan.FromSeconds(25)))
        {
            engine.Report("error", "O MT5 não fechou sozinho (talvez uma janela aberta nele). Reinício adiado.");
            _lastStart = DateTimeOffset.UtcNow;
            return;
        }
        Start(paths, "reaberto");
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    // ---------------------------------------------------------------- janela do gráfico

    private const int WmMdiMaximize = 0x0225;
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>O perfil do robô começa limpo, então o gráfico abre pequeno: maximiza dentro do MT5.</summary>
    private static void MaximizeChart(Process terminal)
    {
        terminal.Refresh();
        var main = terminal.MainWindowHandle;
        if (main == IntPtr.Zero) return;

        var mdi = IntPtr.Zero;
        EnumChildWindows(main, (h, _) =>
        {
            var sb = new StringBuilder(64);
            GetClassName(h, sb, sb.Capacity);
            if (sb.ToString() != "MDIClient") return true;
            mdi = h;
            return false;
        }, IntPtr.Zero);
        if (mdi == IntPtr.Zero) return;

        var chart = GetWindow(mdi, 5); // GW_CHILD: janela de gráfico ativa
        if (chart != IntPtr.Zero) SendMessage(mdi, WmMdiMaximize, chart, IntPtr.Zero);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint size);

    private static string ShortPath(string path)
    {
        var sb = new StringBuilder(1024);
        return GetShortPathName(path, sb, (uint)sb.Capacity) > 0 ? sb.ToString() : path;
    }
}
