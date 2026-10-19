using System.Collections.Concurrent;
using System.IO;
using Microsoft.Extensions.Logging;

namespace TradeGuard.Desktop;

/// <summary>
/// Log diário em &lt;install-root&gt;\dados\logs\AAAA-MM-DD.log na instalação padrão (guarda 30 dias).
/// Serve para diagnosticar qualquer problema sem precisar reproduzir.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();

    public FileLoggerProvider(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        foreach (var old in new DirectoryInfo(dir).GetFiles("*.log").OrderByDescending(f => f.Name).Skip(30))
            old.Delete();
    }

    public ILogger CreateLogger(string category) => _loggers.GetOrAdd(category, c => new FileLogger(this, c));

    public void Dispose() { }

    private void Write(string line)
    {
        lock (_gate)
            File.AppendAllText(Path.Combine(_dir, $"{DateTime.Now:yyyy-MM-dd}.log"), line + Environment.NewLine);
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        private readonly string _short = category[(category.LastIndexOf('.') + 1)..];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
        {
            if (!IsEnabled(level)) return;
            var line = $"{DateTime.Now:HH:mm:ss.fff} {level.ToString()[..4].ToUpperInvariant()} {_short}: {fmt(state, ex)}";
            if (ex is not null) line += Environment.NewLine + ex;
            try { owner.Write(line); } catch (IOException) { /* nunca derruba o app por causa do log */ }
        }
    }
}
