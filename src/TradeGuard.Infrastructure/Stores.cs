using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradeGuard.Domain;

namespace TradeGuard.Infrastructure;

/// <summary>Diário de trades, travas do dia e trilha de auditoria.</summary>
public sealed class JournalStore(
    IDbContextFactory<TradeGuardDb> dbFactory,
    string? backupDirectory = null)
{
    public string BackupDirectory { get; } = backupDirectory
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TradeGuard", "Backups");

    public async Task EnsureCreatedAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        // Bancos criados por versões anteriores: acrescenta o que faltar sem perder dados.
        var columns = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('Trades')").ToListAsync();
        if (!columns.Contains("PositionId"))
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Trades ADD COLUMN PositionId INTEGER NULL");
            await db.Database.ExecuteSqlRawAsync(
                "CREATE UNIQUE INDEX IF NOT EXISTS IX_Trades_PositionId ON Trades (PositionId) WHERE PositionId IS NOT NULL");
        }
        if (!columns.Contains("Excluded"))
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Trades ADD COLUMN Excluded INTEGER NOT NULL DEFAULT 0");
    }

    public async Task SetExcludedAsync(Guid id, bool excluded)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var t = await db.Trades.FindAsync(id);
        if (t is null) return;
        t.Excluded = excluded;
        await db.SaveChangesAsync();
        db.Audit.Add(new AuditEntry
        {
            AtUtc = DateTimeOffset.UtcNow, Level = "info", Category = "journal",
            Message = $"trade {id} {(excluded ? "desconsiderado" : "reconsiderado")} (R$ {t.PnlMoney})"
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Grava só os trades cuja posição do MT5 ainda não está no diário. Devolve os gravados.</summary>
    public async Task<IReadOnlyList<TradeRecord>> ImportAsync(IEnumerable<TradeRecord> trades)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var known = (await db.Trades.Where(t => t.PositionId != null).Select(t => t.PositionId!.Value).ToListAsync()).ToHashSet();
        var fresh = trades.Where(t => t.PositionId is { } id && !known.Contains(id)).ToList();
        if (fresh.Count == 0) return [];
        db.Trades.AddRange(fresh);
        await db.SaveChangesAsync();
        return fresh;
    }

    /// <summary>Uma cópia consistente por dia (API de backup do SQLite), mantém as 30 mais recentes.</summary>
    public async Task<string?> BackupAsync(DateOnly day)
    {
        Directory.CreateDirectory(BackupDirectory);
        var target = Path.Combine(BackupDirectory, $"tradeguard-{day:yyyy-MM-dd}.db");
        if (File.Exists(target)) return null;

        await using var db = await dbFactory.CreateDbContextAsync();
        var source = (Microsoft.Data.Sqlite.SqliteConnection)db.Database.GetDbConnection();
        await source.OpenAsync();
        await using (var destination = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={target};Pooling=False"))
        {
            await destination.OpenAsync();
            source.BackupDatabase(destination);
        }

        foreach (var old in new DirectoryInfo(BackupDirectory).GetFiles("tradeguard-*.db")
                     .OrderByDescending(f => f.Name).Skip(30))
            old.Delete();
        return target;
    }

    public async Task AddTradeAsync(TradeRecord trade)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Trades.Add(trade);
        await db.SaveChangesAsync();
    }

    public async Task UpdateTradeNotesAsync(Guid id, string? setup, string? notes)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var t = await db.Trades.FindAsync(id);
        if (t is null) return;
        t.Setup = setup;
        t.Notes = notes;
        await db.SaveChangesAsync();
    }

    public async Task<List<TradeRecord>> TradesAsync(DateOnly from, DateOnly to)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Trades.AsNoTracking()
            .Where(t => t.TradingDay >= from && t.TradingDay <= to)
            .OrderBy(t => t.OpenedAtUtc)
            .ToListAsync();
    }

    public async Task<List<TradeRecord>> AllTradesAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Trades.AsNoTracking().OrderBy(t => t.OpenedAtUtc).ToListAsync();
    }

    public async Task<DayLockEntity?> DayLockAsync(DateOnly day)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.DayLocks.AsNoTracking().FirstOrDefaultAsync(l => l.TradingDay == day);
    }

    public async Task SaveDayLockAsync(DateOnly day, LockReason reason, DateTimeOffset atUtc)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.DayLocks.AnyAsync(l => l.TradingDay == day)) return;
        db.DayLocks.Add(new DayLockEntity { TradingDay = day, Reason = reason, LockedAtUtc = atUtc });
        await db.SaveChangesAsync();
    }

    public async Task DeleteDayLockAsync(DateOnly day)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.DayLocks.Where(l => l.TradingDay == day).ExecuteDeleteAsync();
        db.Audit.Add(new AuditEntry { AtUtc = DateTimeOffset.UtcNow, Level = "info", Category = "lock", Message = $"trava de {day:yyyy-MM-dd} desfeita (trades desconsiderados)" });
        await db.SaveChangesAsync();
    }

    public async Task AuditAsync(string level, string category, string message)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Audit.Add(new AuditEntry { AtUtc = DateTimeOffset.UtcNow, Level = level, Category = category, Message = message });
        await db.SaveChangesAsync();
    }

    public async Task<List<AuditEntry>> RecentAuditAsync(int take)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Audit.AsNoTracking().OrderByDescending(a => a.Id).Take(take).ToListAsync();
    }
}

public sealed record PendingSettings(RiskSettings Settings, DateOnly EffectiveFrom);

public sealed record SettingsSaveResult(bool AppliedNow, DateOnly EffectiveFrom, IReadOnlyList<string> Loosened, IReadOnlyList<string> Errors);

/// <summary>
/// Configurações versionadas. Apertar regras vale na hora; afrouxar só vale no próximo pregão.
/// </summary>
public sealed class SettingsStore(IDbContextFactory<TradeGuardDb> dbFactory)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Regras valendo no dia: a versão salva por último entre as que já começaram a valer.</summary>
    public async Task<RiskSettings> EffectiveAsync(DateOnly day)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.SettingsVersions.AsNoTracking()
            .Where(s => s.EffectiveFrom <= day)
            .OrderByDescending(s => s.Id)
            .FirstOrDefaultAsync();
        return row is null ? RiskSettings.Default : JsonSerializer.Deserialize<RiskSettings>(row.Json, Json)!;
    }

    /// <summary>Mudança agendada: só se o último salvamento ainda não começou a valer.</summary>
    public async Task<PendingSettings?> PendingAsync(DateOnly today)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.SettingsVersions.AsNoTracking().OrderByDescending(s => s.Id).FirstOrDefaultAsync();
        return row is null || row.EffectiveFrom <= today
            ? null
            : new(JsonSerializer.Deserialize<RiskSettings>(row.Json, Json)!, row.EffectiveFrom);
    }

    /// <summary>O que você deixou salvo (agendado, se houver; senão o que vale hoje). É isso que a tela de Regras mostra.</summary>
    public async Task<RiskSettings> DesiredAsync(DateOnly today) =>
        (await PendingAsync(today))?.Settings ?? await EffectiveAsync(today);

    /// <summary>
    /// Salva tudo. O que aperta (ou não muda o risco) vale na hora; cada campo que afrouxa fica agendado
    /// para o próximo pregão (anti-tilt), sem segurar o resto.
    /// </summary>
    /// <param name="applyNow">
    /// Só para mudança pedida explicitamente pelo dono (ex.: ligar o automático já): grava tudo para valer hoje,
    /// sem a espera do anti-tilt. A tela de Regras nunca usa.
    /// </param>
    public async Task<SettingsSaveResult> SaveAsync(RiskSettings proposed, DateOnly today, bool applyNow = false)
    {
        var errors = proposed.Validate();
        if (errors.Count > 0) return new(false, today, [], errors);

        var current = await EffectiveAsync(today);
        var loosened = applyNow ? [] : current.LoosenedBy(proposed);
        var now = applyNow ? proposed : current.ApplicableNow(proposed);
        var from = NextWeekday(today);

        await using var db = await dbFactory.CreateDbContextAsync();
        if (now.Validate().Count == 0) db.SettingsVersions.Add(Row(now, today));
        if (loosened.Count > 0) db.SettingsVersions.Add(Row(proposed, from)); // entra depois (Id maior)
        await db.SaveChangesAsync();
        return new(loosened.Count == 0, loosened.Count == 0 ? today : from, loosened, []);
    }

    private static SettingsVersionEntity Row(RiskSettings s, DateOnly from) => new()
    {
        EffectiveFrom = from,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Json = JsonSerializer.Serialize(s, Json)
    };

    private static DateOnly NextWeekday(DateOnly d)
    {
        do d = d.AddDays(1); while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        return d;
    }
}

/// <summary>
/// Chave mestra da ponte (32 bytes aleatórios), protegida com DPAPI no perfil do usuário Windows.
/// Só este usuário, nesta máquina, consegue decifrar o arquivo.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class SecretStore(string path)
{
    private static readonly byte[] Entropy = "TradeGuard.Bridge.v1"u8.ToArray();

    public byte[] GetOrCreateMasterKey()
    {
        if (File.Exists(path))
            return ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);

        var key = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));
        return key;
    }

    public byte[] Rotate()
    {
        if (File.Exists(path)) File.Delete(path);
        return GetOrCreateMasterKey();
    }
}
