using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using TradeGuard.Domain;

namespace TradeGuard.UI.Engine;

public sealed record ParitySummary(int CleanPairedDays, int CleanSignals, int Reports, int Issues, int TotalIssues, string Message)
{
    public bool MinimumReached => CleanPairedDays >= 60 && CleanSignals >= 100 && Issues == 0;
    public static ParitySummary Empty { get; } = new(0, 0, 0, 0, 0, "nenhum pregão auditado");
}

public sealed record OperationalReadiness(
    bool StorageHealthy, string StorageMessage, ParitySummary Parity, int FailureDrills, int RolloverChecks)
{
    public bool Qualified => StorageHealthy && Parity.MinimumReached && FailureDrills >= 5 && RolloverChecks >= 2;
    public string Message => Qualified
        ? "critérios técnicos mínimos concluídos; a liberação real ainda exige decisão explícita do dono"
        : $"bloqueado: paridade {Parity.CleanPairedDays}/60 dias e {Parity.CleanSignals}/100 operações completas; " +
          $"simulações de falha {FailureDrills}/5; trocas de contrato {RolloverChecks}/2; armazenamento {(StorageHealthy ? "ok" : "com falha")}";
}

public sealed record FailureDrillEvidence(string Kind, bool Passed, DateTimeOffset AtUtc, string Detail);
public sealed record RolloverEvidence(string Instrument, string FromSymbol, string ToSymbol, DateTimeOffset AtUtc);

/// <summary>Estado compartilhado que transforma falha silenciosa de disco em bloqueio de novas entradas.</summary>
public static class AuditStorage
{
    private static readonly object Sync = new();
    private static bool _healthy = true;
    private static string _message = "gravação de auditoria disponível";
    public static bool Healthy { get { lock (Sync) return _healthy; } }
    public static string Message { get { lock (Sync) return _message; } }

    public static void MarkFailure(Exception ex) => Set(false, "Falha ao gravar auditoria: " + ex.Message);

    public static void Probe()
    {
        try
        {
            Directory.CreateDirectory(TradeGuardServices.DataDirectory);
            var root = Path.GetPathRoot(Path.GetFullPath(TradeGuardServices.DataDirectory))!;
            var drive = new DriveInfo(root);
            var minimum = Math.Max(2L * 1024 * 1024 * 1024, drive.TotalSize / 20); // 2 GB ou 5%
            if (drive.AvailableFreeSpace < minimum)
            {
                Set(false, $"Disco com pouco espaço: {drive.AvailableFreeSpace / 1024d / 1024 / 1024:0.0} GB livres; mínimo {minimum / 1024d / 1024 / 1024:0.0} GB.");
                return;
            }
            var probe = Path.Combine(TradeGuardServices.DataDirectory, ".write-probe");
            File.WriteAllText(probe, DateTimeOffset.UtcNow.ToString("O"));
            File.Delete(probe);
            Set(true, $"gravação ok; {drive.AvailableFreeSpace / 1024d / 1024 / 1024:0.0} GB livres");
        }
        catch (Exception ex) { MarkFailure(ex); }
    }

    private static void Set(bool healthy, string message)
    {
        lock (Sync) { _healthy = healthy; _message = message; }
    }
}

public static class StorageMaintenance
{
    /// <summary>Compacta CSVs de fluxo encerrados há 30 dias. Mantém o nome/data e valida o GZip antes de remover o original.</summary>
    public static void CompactOldFlowFiles(DateTimeOffset nowUtc)
    {
        foreach (var name in new[] { "fluxo", "fluxo-win" })
        {
            var dir = Path.Combine(TradeGuardServices.DataDirectory, name);
            if (!Directory.Exists(dir)) continue;
            foreach (var source in Directory.EnumerateFiles(dir, "*.csv", SearchOption.TopDirectoryOnly))
            {
                if (File.GetLastWriteTimeUtc(source) > nowUtc.UtcDateTime.AddDays(-30)) continue;
                var target = source + ".gz";
                if (File.Exists(target)) continue;
                var temp = target + ".tmp";
                try
                {
                    using (var input = File.OpenRead(source))
                    using (var output = File.Create(temp))
                    using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize)) input.CopyTo(gzip);
                    using (var input = File.OpenRead(temp))
                    using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                    {
                        var bytes = 0L;
                        var buffer = new byte[81920];
                        int read;
                        while ((read = gzip.Read(buffer)) > 0) bytes += read;
                        if (bytes != new FileInfo(source).Length) throw new IOException("compactação não confere");
                    }
                    File.Move(temp, target);
                    File.Delete(source);
                }
                catch (Exception ex)
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    AuditStorage.MarkFailure(ex);
                }
            }
        }
    }
}

public sealed class ParityAuditStore
{
    private readonly object _sync = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public ParitySummary Summary { get; private set; } = ParitySummary.Empty;

    public OperationalReadiness Readiness
    {
        get
        {
            var drills = ReadEvidence<FailureDrillEvidence>("drills")
                .Where(x => x.Passed && !string.IsNullOrWhiteSpace(x.Kind)).Select(x => x.Kind).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var rollovers = ReadEvidence<RolloverEvidence>("rollovers")
                .Where(x => !string.IsNullOrWhiteSpace(x.Instrument) && x.FromSymbol != x.ToSymbol)
                .Select(x => $"{x.Instrument}|{x.FromSymbol}|{x.ToSymbol}").Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return new(AuditStorage.Healthy, AuditStorage.Message, Summary, drills, rollovers);
        }
    }

    /// <summary>Observa a troca real do contrato no handshake. A primeira observação apenas cria a referência.</summary>
    public void ObserveContract(string instrument, string symbol, DateTimeOffset atUtc)
    {
        if (string.IsNullOrWhiteSpace(instrument) || string.IsNullOrWhiteSpace(symbol)) return;
        lock (_sync)
        {
            try
            {
                var root = Path.Combine(TradeGuardServices.DataDirectory, "validacao");
                Directory.CreateDirectory(root);
                var state = Path.Combine(root, $"ultimo-contrato-{instrument.ToLowerInvariant()}.txt");
                var previous = File.Exists(state) ? File.ReadAllText(state).Trim() : "";
                if (previous.Length > 0 && !previous.Equals(symbol, StringComparison.OrdinalIgnoreCase))
                {
                    var evidence = new RolloverEvidence(instrument, previous, symbol, atUtc);
                    var dir = Path.Combine(root, "rollovers");
                    Directory.CreateDirectory(dir);
                    var target = Path.Combine(dir, $"{atUtc:yyyyMMdd-HHmmss}-{instrument.ToLowerInvariant()}.json");
                    AtomicJson(target, evidence);
                }
                var temp = state + ".tmp";
                File.WriteAllText(temp, symbol);
                File.Move(temp, state, true);
            }
            catch (Exception ex) { AuditStorage.MarkFailure(ex); }
        }
    }

    public void AuditAll(IReadOnlyDictionary<string, IReadOnlyList<Candle>> histories, DateOnly today, TimeOnly now)
    {
        lock (_sync)
        {
            try
            {
                var evidenceStart = ReadEvidenceStart();
                var decisions = Path.Combine(TradeGuardServices.DataDirectory, "decisoes");
                if (!Directory.Exists(decisions)) { RefreshSummary(); return; }
                foreach (var file in Directory.EnumerateFiles(decisions, "????-??-??-*.csv"))
                {
                    var stem = Path.GetFileNameWithoutExtension(file);
                    if (stem.Length < 14 || !DateOnly.TryParseExact(stem[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
                    if (day < evidenceStart) continue;
                    var instrument = stem[11..].ToUpperInvariant();
                    if (!histories.TryGetValue(instrument, out var candles)) continue;
                    var lastRule = SetupNames.TimeRules.Where(r => r.Instrument == instrument).Max(r => r.At);
                    if (day > today || (day == today && now < lastRule.AddMinutes(1))) continue;
                    var observed = ReadDecisions(file);
                    var result = TimeRuleParity.Audit(candles, day, instrument, observed,
                        liveAdrDays: AdrSnapshots.Read(instrument, day), liveDayCandles: ReadLiveDayCandles(instrument, day));
                    WriteResult(result);
                }
                RefreshSummary();
            }
            catch (Exception ex) { AuditStorage.MarkFailure(ex); }
        }
    }

    /// <summary>Candles do dia gravados ao vivo (dados\candles ou candles-win), no preço real do contrato.</summary>
    private static IReadOnlyList<Candle>? ReadLiveDayCandles(string instrument, DateOnly day)
    {
        var folder = instrument.Equals("WDO", StringComparison.OrdinalIgnoreCase) ? "candles" : "candles-" + instrument.ToLowerInvariant();
        var path = Path.Combine(TradeGuardServices.DataDirectory, folder, $"{day:yyyy-MM-dd}.csv");
        if (!File.Exists(path)) return null;
        var inv = CultureInfo.InvariantCulture;
        var list = new List<Candle>();
        foreach (var p in File.ReadLines(path).Skip(1).Select(x => x.Split(';')).Where(x => x.Length >= 5))
        {
            if (!TimeOnly.TryParseExact(p[0], "HH:mm", inv, DateTimeStyles.None, out var t)) continue;
            var local = day.ToDateTime(t);
            var open = new DateTimeOffset(local, B3Clock.Zone.GetUtcOffset(local)).ToUniversalTime();
            list.Add(new Candle(open, decimal.Parse(p[1], inv), decimal.Parse(p[2], inv), decimal.Parse(p[3], inv), decimal.Parse(p[4], inv)));
        }
        return list;
    }

    private static IReadOnlyDictionary<string, ObservedRuleDecision> ReadDecisions(string file)
    {
        var rows = File.ReadLines(file).Skip(1).Select(x => x.Split(';')).Where(x => x.Length >= 13).ToList();
        var inv = CultureInfo.InvariantCulture;
        decimal? D(string s) => decimal.TryParse(s, NumberStyles.Any, inv, out var n) ? n : null;
        var result = new Dictionary<string, ObservedRuleDecision>();
        foreach (var group in rows.GroupBy(x => x[3]))
        {
            var evaluation = group.LastOrDefault(x => x[4] == "avaliação");
            if (evaluation is null) continue;
            result[group.Key] = new(group.Key, int.TryParse(evaluation[5], out var side) ? side : 0,
                D(evaluation[6]) ?? 0, D(evaluation[7]), D(evaluation[8]),
                group.Any(x => x[4] == "execução"),
                group.Any(x => x[4] == "execução" && x[11] == "ordem enviada"),
                group.Any(x => x[4] == "posição" && x[11] == "aberta"),
                group.Any(x => x[4] == "posição" && x[11] == "fechada"));
        }
        return result;
    }

    private static void WriteResult(DailyParityResult result)
    {
        var dir = Path.Combine(TradeGuardServices.DataDirectory, "validacao", "paridade");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"{result.Day:yyyy-MM-dd}-{result.Instrument.ToLowerInvariant()}.json");
        AtomicJson(target, result);
    }

    private void RefreshSummary()
    {
        try
        {
            var dir = Path.Combine(TradeGuardServices.DataDirectory, "validacao", "paridade");
            var evidenceStart = ReadEvidenceStart();
            var reports = Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*.json").Select(f => JsonSerializer.Deserialize<DailyParityResult>(File.ReadAllText(f)))
                    .OfType<DailyParityResult>().Where(r => r.Day >= evidenceStart).ToList()
                : [];
            var paired = reports.GroupBy(r => r.Day).Where(g => g.Select(x => x.Instrument).Distinct().Count() == 2).OrderBy(g => g.Key).ToList();
            var cleanRun = paired.AsEnumerable().Reverse().TakeWhile(g => g.All(x => x.Matches)).ToList();
            // O portão real conta apenas operações cuja cadeia ordem → abertura → fechamento foi comprovada.
            // Sinais observados, bloqueados ou aguardando confirmação podem manter o dia coerente, mas não qualificam.
            var signals = cleanRun.Sum(g => g.Sum(x => x.CompletedSignals));
            var totalIssues = reports.Sum(r => r.Issues.Count);
            var currentIssues = paired.LastOrDefault()?.Sum(r => r.Issues.Count) ?? 0;
            Summary = new(cleanRun.Count, signals, reports.Count, currentIssues, totalIssues,
                reports.Count == 0 ? "nenhum pregão auditado" : $"{cleanRun.Count} pregão(ões) pareados sem divergência; {signals} operações completas; {currentIssues} divergência(s) no último pregão ({totalIssues} históricas)");
        }
        catch (Exception ex) { AuditStorage.MarkFailure(ex); Summary = ParitySummary.Empty with { Message = "falha ao ler evidências: " + ex.Message }; }
    }

    private static DateOnly ReadEvidenceStart()
    {
        var path = Path.Combine(TradeGuardServices.DataDirectory, "validacao", "paridade-inicio.txt");
        return File.Exists(path)
            && DateOnly.TryParseExact(File.ReadAllText(path).Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var start)
            ? start
            : DateOnly.MinValue;
    }

    private static IReadOnlyList<T> ReadEvidence<T>(string folder)
    {
        try
        {
            var dir = Path.Combine(TradeGuardServices.DataDirectory, "validacao", folder);
            return Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*.json").Select(f => JsonSerializer.Deserialize<T>(File.ReadAllText(f))).OfType<T>().ToList()
                : [];
        }
        catch (Exception ex) { AuditStorage.MarkFailure(ex); return []; }
    }

    private static void AtomicJson<T>(string target, T value)
    {
        var temp = target + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
        File.Move(temp, target, true);
    }
}

/// <summary>
/// ADR congelado por dia e ativo em dados\validacao\adr\AAAA-MM-DD-wdo.json: o primeiro cálculo do dia vale até o fim
/// do pregão (reinício ou reexportação não o mudam) e a paridade refaz a conta com as mesmas amplitudes.
/// </summary>
public static class AdrSnapshots
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed record Snapshot(DateOnly Day, string Instrument, decimal Adr, DateTimeOffset CreatedAtUtc, List<DayRange> Days);

    private static string PathFor(string instrument, DateOnly day) =>
        Path.Combine(TradeGuardServices.DataDirectory, "validacao", "adr", $"{day:yyyy-MM-dd}-{instrument.ToLowerInvariant()}.json");

    /// <summary>Grava as amplitudes se ainda não houver congelado do dia; devolve as que valem (as já gravadas).</summary>
    public static IReadOnlyList<DayRange> Freeze(string instrument, DateOnly day, IReadOnlyList<DayRange> ranges)
    {
        lock (Sync)
        {
            if (Read(instrument, day) is { Count: > 0 } existing) return existing;
            try
            {
                var path = PathFor(instrument, day);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(
                    new Snapshot(day, instrument, DailyRange.Adr(ranges), DateTimeOffset.UtcNow, ranges.ToList()), Json));
                File.Move(temp, path, true);
            }
            catch (Exception ex) { AuditStorage.MarkFailure(ex); }
            return ranges;
        }
    }

    public static IReadOnlyList<DayRange>? Read(string instrument, DateOnly day)
    {
        try
        {
            var path = PathFor(instrument, day);
            return File.Exists(path) ? JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path))?.Days : null;
        }
        catch (Exception ex) { AuditStorage.MarkFailure(ex); return null; }
    }
}