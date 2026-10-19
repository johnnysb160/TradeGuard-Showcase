using System.IO;
using System.Windows;
using Microsoft.Win32;
using TradeGuard.UI;

namespace TradeGuard.Desktop;

/// <summary>Implementação WPF do que a interface Blazor pede para a janela.</summary>
public sealed class DesktopShell : IDesktopShell
{
    private Window? _window;

    public void Attach(Window window) => _window = window;

    public bool Topmost
    {
        get => _window?.Dispatcher.Invoke(() => _window.Topmost) ?? false;
        set => _window?.Dispatcher.Invoke(() => _window.Topmost = value);
    }

    public void CopyText(string text) => _window?.Dispatcher.Invoke(() => Clipboard.SetText(text));

    public Task<bool> SaveFileAsync(string suggestedName, string filter, byte[] content)
    {
        if (_window is null) return Task.FromResult(false);
        return _window.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new SaveFileDialog
            {
                FileName = suggestedName,
                Filter = filter,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dialog.ShowDialog(_window) != true) return false;
            File.WriteAllBytes(dialog.FileName, content);
            return true;
        }).Task;
    }
}
