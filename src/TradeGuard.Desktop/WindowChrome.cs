using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TradeGuard.Desktop;

/// <summary>Barra de título escura e na cor do app (Windows 11; no Windows 10 cai no modo escuro padrão).</summary>
internal static class WindowChrome
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void ApplyDark(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var on = 1;
        DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref on, sizeof(int));
        var caption = Bgr(0x11, 0x16, 0x1d); // --tg-surface
        DwmSetWindowAttribute(hwnd, DwmCaptionColor, ref caption, sizeof(int));
        var text = Bgr(0xe6, 0xed, 0xf3);   // --tg-text
        DwmSetWindowAttribute(hwnd, DwmTextColor, ref text, sizeof(int));
    }

    private static int Bgr(byte r, byte g, byte b) => r | (g << 8) | (b << 16);
}
