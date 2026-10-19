namespace TradeGuard.Domain;

/// <summary>Relógio no fuso da B3 (America/Sao_Paulo). Injetável via TimeProvider para testes.</summary>
public sealed class B3Clock(TimeProvider time)
{
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public DateTimeOffset UtcNow => time.GetUtcNow();

    public DateTimeOffset Now => TimeZoneInfo.ConvertTime(time.GetUtcNow(), Zone);

    public DateOnly TradingDay => DateOnly.FromDateTime(Now.DateTime);

    public TimeOnly TimeOfDay => TimeOnly.FromDateTime(Now.DateTime);

    public static DateTimeOffset ToB3(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone);

    public static DateOnly WeekStart(DateOnly day) =>
        day.AddDays(-(((int)day.DayOfWeek + 6) % 7)); // segunda-feira

    public static DateOnly MonthStart(DateOnly day) => new(day.Year, day.Month, 1);
}

public sealed record NewsEvent(DateTimeOffset TimeUtc, string Currency, int Importance, string Name);
