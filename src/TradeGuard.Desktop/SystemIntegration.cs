using System.IO;
using System.Runtime.InteropServices;

namespace TradeGuard.Desktop;

/// <summary>Integração com o Windows: não dormir enquanto o robô está aberto e atalhos de abertura.</summary>
internal static class SystemIntegration
{
    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        AwayModeRequired = 0x00000040,
        Continuous = 0x80000000
    }

    [DllImport("kernel32.dll")]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, char[]? name);

    /// <summary>
    /// Aberto por um app empacotado (MSIX)? Aí o Windows isola %LOCALAPPDATA% e o TradeGuard veria outro banco
    /// e outra chave. Nunca pode rodar assim.
    /// </summary>
    public static bool RunsInsidePackage()
    {
        var length = 0;
        return GetCurrentPackageFullName(ref length, null) != 15700; // APPMODEL_ERROR_NO_PACKAGE
    }

    /// <summary>Reabre este executável fora do pacote (processo criado pelo WMI não herda o isolamento).</summary>
    public static void RelaunchOutsidePackage(string[] args)
    {
        var exe = Environment.ProcessPath!;
        var line = $"\"{exe}\" {string.Join(' ', args.Select(a => $"\"{a}\""))}".Trim().Replace("'", "''");
        var dir = Path.GetDirectoryName(exe)!.Replace("'", "''");
        var script = $"Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{{CommandLine='{line}'; CurrentDirectory='{dir}'}}";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!.WaitForExit(15000);
    }

    /// <summary>Impede a suspensão do PC (a tela pode apagar normalmente).</summary>
    public static void KeepAwake(bool on) =>
        SetThreadExecutionState(on ? ExecutionState.Continuous | ExecutionState.SystemRequired : ExecutionState.Continuous);

    /// <summary>
    /// Cria atalhos na Área de Trabalho e no Menu Iniciar apontando para este executável.
    /// Não faz nada quando roda de dentro do Visual Studio (pasta bin).
    /// </summary>
    public static void EnsureShortcuts()
    {
        var exe = Environment.ProcessPath;
        if (exe is null || exe.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || Environment.GetEnvironmentVariable("TRADEGUARD_DATA_DIR") is { Length: > 0 })
            return;

        // Inicializar do Windows: o robô abre sozinho ao ligar o PC (o MT5 vem junto). Renda extra sem depender de você.
        foreach (var folder in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                     Environment.GetFolderPath(Environment.SpecialFolder.Startup)
                 })
        {
            var link = Path.Combine(folder, "TradeGuard.lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic shortcut = shell.CreateShortcut(link);
                if (File.Exists(link) && string.Equals((string)shortcut.TargetPath, exe, StringComparison.OrdinalIgnoreCase))
                    continue;
                shortcut.TargetPath = exe;
                shortcut.WorkingDirectory = Path.GetDirectoryName(exe);
                shortcut.IconLocation = exe + ",0";
                shortcut.Description = "TradeGuard — abre o robô e o MT5";
                shortcut.Save();
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}
