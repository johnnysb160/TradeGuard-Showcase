using System.Diagnostics;
using Microsoft.Win32;

namespace TradeGuard.UI.Mt5;

public sealed record Mt5Paths(string InstallDir, string DataDir)
{
    public string Terminal => Path.Combine(InstallDir, "terminal64.exe");
    public string MetaEditor => Path.Combine(InstallDir, "MetaEditor64.exe");
    public string Experts => Path.Combine(DataDir, "MQL5", "Experts");
    public string Presets => Path.Combine(DataDir, "MQL5", "Presets");
    public string Profiles => Path.Combine(DataDir, "MQL5", "Profiles", "Charts");
    public string Config => Path.Combine(DataDir, "config");
    public string ExpertsLog => Path.Combine(DataDir, "MQL5", "Logs");
}

/// <summary>Encontra o MT5 instalado sem perguntar nada ao usuário.</summary>
public static class Mt5Locator
{
    public static Mt5Paths? Find(string? configuredInstallDir)
    {
        var install = Candidates(configuredInstallDir)
            .Where(d => File.Exists(Path.Combine(d, "terminal64.exe")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(d => d.Contains("Clear", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        return install is null ? null : new Mt5Paths(install, DataDirFor(install));
    }

    private static IEnumerable<string> Candidates(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured.TrimEnd('\\');

        // 1) MT5 já aberto
        foreach (var p in Process.GetProcessesByName("terminal64"))
        {
            string? dir = null;
            try { dir = Path.GetDirectoryName(p.MainModule?.FileName); } catch { /* sem acesso */ }
            if (dir is not null) yield return dir;
        }

        // 2) Programas instalados (registro do Windows)
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        foreach (var path in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var key = root.OpenSubKey(path);
            if (key is null) continue;
            foreach (var name in key.GetSubKeyNames())
            {
                using var app = key.OpenSubKey(name);
                var display = app?.GetValue("DisplayName") as string ?? "";
                var location = app?.GetValue("InstallLocation") as string;
                if (location is not null && (display.Contains("MT5") || display.Contains("MetaTrader")))
                    yield return location.TrimEnd('\\');
            }
        }

        // 3) Pastas padrão
        foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
        {
            if (!Directory.Exists(pf)) continue;
            foreach (var dir in Directory.EnumerateDirectories(pf))
                if (dir.Contains("MT5", StringComparison.OrdinalIgnoreCase) || dir.Contains("MetaTrader", StringComparison.OrdinalIgnoreCase))
                    yield return dir;
        }
    }

    /// <summary>
    /// Pasta de dados: normalmente %APPDATA%\MetaQuotes\Terminal\{hash}; em instalação "portátil" (ou rodando
    /// como administrador) fica na própria pasta do programa. Decide por onde o MQL5 realmente existe.
    /// </summary>
    private static string DataDirFor(string install)
    {
        var terminals = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MetaQuotes", "Terminal");
        if (Directory.Exists(terminals))
        {
            foreach (var dir in Directory.EnumerateDirectories(terminals))
            {
                var origin = Path.Combine(dir, "origin.txt");
                if (!File.Exists(origin)) continue;
                var target = File.ReadAllText(origin).Trim().TrimStart('﻿').TrimEnd('\\');
                if (string.Equals(target, install, StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(Path.Combine(dir, "MQL5")))
                    return dir;
            }
        }
        return install;
    }
}
