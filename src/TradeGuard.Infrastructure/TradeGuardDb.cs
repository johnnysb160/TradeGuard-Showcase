using Microsoft.EntityFrameworkCore;
using TradeGuard.Domain;

namespace TradeGuard.Infrastructure;

public sealed class DayLockEntity
{
    public DateOnly TradingDay { get; set; }
    public LockReason Reason { get; set; }
    public DateTimeOffset LockedAtUtc { get; set; }
}

public sealed class SettingsVersionEntity
{
    public int Id { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string Json { get; set; } = "";
}

public sealed class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset AtUtc { get; set; }
    public string Level { get; set; } = "info";
    public string Category { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class TradeGuardDb(DbContextOptions<TradeGuardDb> options) : DbContext(options)
{
    public DbSet<TradeRecord> Trades => Set<TradeRecord>();
    public DbSet<DayLockEntity> DayLocks => Set<DayLockEntity>();
    public DbSet<SettingsVersionEntity> SettingsVersions => Set<SettingsVersionEntity>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<TradeRecord>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasIndex(t => t.TradingDay);
            e.HasIndex(t => t.PositionId).IsUnique().HasFilter("PositionId IS NOT NULL");
            e.Ignore(t => t.RMultiple);
            e.Ignore(t => t.CountsForLimits);
            e.Property(t => t.Side).HasConversion<string>();
        });
        b.Entity<DayLockEntity>(e =>
        {
            e.HasKey(l => l.TradingDay);
            e.Property(l => l.Reason).HasConversion<string>();
        });
        b.Entity<SettingsVersionEntity>().HasIndex(s => s.EffectiveFrom);
        b.Entity<AuditEntry>().HasIndex(a => a.AtUtc);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        // SQLite não ordena/compara decimal nem DateTimeOffset nativamente: guarda como texto ordenável.
        c.Properties<decimal>().HaveConversion<string>();
        c.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToUtcTicks>();
    }
}

internal sealed class DateTimeOffsetToUtcTicks()
    : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>(
        v => v.UtcTicks,
        v => new DateTimeOffset(v, TimeSpan.Zero));
