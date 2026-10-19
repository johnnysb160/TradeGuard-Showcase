namespace TradeGuard.Domain;

public sealed record Bar(DateTimeOffset StartUtc, decimal Open, decimal High, decimal Low, decimal Close);

/// <summary>Monta candles de 1 minuto a partir das cotações (para o trailing por candle).</summary>
public sealed class MinuteBarBuilder
{
    private Bar? _current;

    public Bar? Previous { get; private set; }

    public void Add(DateTimeOffset timeUtc, decimal price)
    {
        if (price <= 0) return;
        var minute = new DateTimeOffset(timeUtc.Year, timeUtc.Month, timeUtc.Day, timeUtc.Hour, timeUtc.Minute, 0, TimeSpan.Zero);

        if (_current is null || minute > _current.StartUtc)
        {
            if (_current is not null) Previous = _current;
            _current = new Bar(minute, price, price, price, price);
            return;
        }
        if (minute < _current.StartUtc) return; // cotação atrasada

        _current = _current with
        {
            High = Math.Max(_current.High, price),
            Low = Math.Min(_current.Low, price),
            Close = price
        };
    }
}
