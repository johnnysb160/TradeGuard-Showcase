using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using TradeGuard.Domain;
using TradeGuard.UI;
using TradeGuard.UI.Engine;

namespace TradeGuard.Desktop;

/// <summary>
/// Todos os sons do TradeGuard com o volume próprio (Regras → Sons): sinal, entrada, saída, conexão perdida/voltou e
/// demais avisos. O balão do Windows fica mudo (o som dele saía no volume do sistema, alto demais).
/// </summary>
internal sealed class SoundAttention : IDisposable
{
    private static readonly string Media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
    // Avisos que já têm som próprio aqui (não tocam o som genérico do aviso).
    private static readonly string[] OwnSound = ["Sinal de", "Entrada automática", "Trade no", "Sem conexão", "MT5 desconectado", "Teste de aviso"];

    private readonly Window _window;
    private readonly TradingEngine _primary;
    private readonly List<TradingEngine> _engines;
    private readonly Dictionary<TradingEngine, bool> _hadPosition;
    private readonly List<MediaPlayer> _players = [];
    private bool? _connected; // null até a primeira conexão completa: abrir o app não toca "conexão perdida"

    public SoundAttention(Window window, TradingEngine primary, IEnumerable<TradingEngine> others)
    {
        _window = window;
        _primary = primary;
        _engines = [primary, .. others];
        _hadPosition = _engines.ToDictionary(e => e, e => e.State.Position is not null);
        foreach (var e in _engines)
        {
            e.Changed += OnChanged;
            e.SignalRaised += OnSignal;
            e.Alerted += OnAlert;
        }
        AlertSounds.PreviewRequested += OnPreview;
    }

    private RiskSettings Settings => _primary.State.Settings;

    private void OnPreview(string file, int volume) => _window.Dispatcher.BeginInvoke(() => Play(file, volume));

    private void OnSignal(PendingSignal signal) => _window.Dispatcher.BeginInvoke(() =>
    {
        if (Settings.SignalSound || signal.Signal.Setup == "Teste")
            Play(signal.Signal.Side == Side.Buy ? "Windows Notify Calendar.wav" : "Windows Notify Email.wav");
        if (!_window.IsActive) Flash();
    });

    private void OnAlert(string title, string text) => _window.Dispatcher.BeginInvoke(() =>
    {
        if (!OwnSound.Any(title.StartsWith)) Play(AlertSounds.Connection);
    });

    private void OnChanged() => _window.Dispatcher.BeginInvoke(() =>
    {
        foreach (var e in _engines)
        {
            var has = e.State.Position is not null;
            if (has == _hadPosition[e]) continue;
            _hadPosition[e] = has;
            Play(has ? Settings.EntrySound : Settings.ExitSound);
        }

        var connected = _engines.All(e => e.State.Connected && e.State.BrokerConnected);
        if (_connected is null)
        {
            if (connected) _connected = true;
            return;
        }
        if (connected == _connected) return;
        _connected = connected;
        Play(AlertSounds.Connection); // perdeu e voltou: mesmo som do aviso, no volume do app
    });

    private void Play(string file) => Play(file, Settings.SoundVolumePercent);

    private void Play(string file, int volumePercent)
    {
        if (string.IsNullOrEmpty(file) || volumePercent <= 0) return;
        var path = Path.Combine(Media, file);
        if (!File.Exists(path)) return;
        try
        {
            var player = new MediaPlayer { Volume = Math.Clamp(volumePercent, 0, 100) / 100d };
            void Done() { player.Close(); _players.Remove(player); }
            player.MediaEnded += (_, _) => Done();
            player.MediaFailed += (_, _) => Done();
            _players.Add(player); // mantém a referência até terminar de tocar
            player.Open(new Uri(path));
            player.Play();
        }
        catch
        {
            // Som nunca interfere na operação.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    private void Flash()
    {
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Hwnd = new WindowInteropHelper(_window).Handle,
            Flags = 0x3 | 0xC, // FLASHW_ALL | FLASHW_TIMERNOFG: pisca até a janela vir para frente
            Count = 0,
            Timeout = 0
        };
        FlashWindowEx(ref info);
    }

    public void Dispose()
    {
        foreach (var e in _engines)
        {
            e.Changed -= OnChanged;
            e.SignalRaised -= OnSignal;
            e.Alerted -= OnAlert;
        }
        AlertSounds.PreviewRequested -= OnPreview;
        foreach (var p in _players.ToArray()) p.Close();
        _players.Clear();
    }
}
