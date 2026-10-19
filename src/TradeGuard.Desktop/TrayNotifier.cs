using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using Forms = System.Windows.Forms;

namespace TradeGuard.Desktop;

/// <summary>
/// Ícone na bandeja + notificações do Windows. Não depende de celular nem de configuração no MT5.
/// </summary>
internal sealed class TrayNotifier : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Window _window;

    public TrayNotifier(Window window)
    {
        _window = window;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "tradeguard.ico");
        _icon = new Forms.NotifyIcon
        {
            Icon = File.Exists(iconPath) ? new System.Drawing.Icon(iconPath) : System.Drawing.SystemIcons.Application,
            Text = "TradeGuard",
            Visible = true
        };
        _icon.DoubleClick += (_, _) => ShowWindow();
        _icon.BalloonTipClicked += (_, _) => ShowWindow();
    }

    public void Notify(string title, string text)
    {
        _window.Dispatcher.BeginInvoke(() =>
        {
            if (!ShowSilent(title, text)) _icon.ShowBalloonTip(6000, title, text, Forms.ToolTipIcon.None);
        });
    }

    // Balão sem o som do Windows (NIIF_NOSOUND): o som, com volume próprio, é tocado pelo SoundAttention.
    // O NotifyIcon não expõe essa opção; pega a janela/ID dele e chama o Shell direto. Se falhar, usa o normal.
    private bool ShowSilent(string title, string text)
    {
        try
        {
            const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Instance;
            var window = (typeof(Forms.NotifyIcon).GetField("_window", F) ?? typeof(Forms.NotifyIcon).GetField("window", F))?.GetValue(_icon) as Forms.NativeWindow;
            var id = (typeof(Forms.NotifyIcon).GetField("_id", F) ?? typeof(Forms.NotifyIcon).GetField("id", F))?.GetValue(_icon);
            if (window is null || window.Handle == IntPtr.Zero || id is null) return false;
            var data = new NotifyIconData
            {
                cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
                hWnd = window.Handle,
                uID = Convert.ToUInt32(id),
                uFlags = 0x10, // NIF_INFO
                szInfo = text.Length > 255 ? text[..255] : text,
                szInfoTitle = title.Length > 63 ? title[..63] : title,
                dwInfoFlags = 0x10 // NIIF_NOSOUND
            };
            return Shell_NotifyIcon(1 /* NIM_MODIFY */, ref data);
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    public void SetTooltip(string text) =>
        _window.Dispatcher.BeginInvoke(() => _icon.Text = text.Length > 63 ? text[..63] : text);

    private void ShowWindow()
    {
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Show();
        _window.Activate();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
