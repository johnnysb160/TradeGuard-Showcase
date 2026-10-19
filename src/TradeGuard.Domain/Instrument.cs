namespace TradeGuard.Domain;

public enum Side
{
    Buy,
    Sell
}

public static class SideExtensions
{
    /// <summary>+1 para compra, -1 para venda. Multiplica deslocamentos de preço "a favor".</summary>
    public static int Sign(this Side side) => side == Side.Buy ? 1 : -1;

    public static Side Opposite(this Side side) => side == Side.Buy ? Side.Sell : Side.Buy;
}

/// <summary>Especificação do contrato, informada pelo MT5 no handshake.</summary>
public sealed record InstrumentSpec(
    string Symbol,
    decimal TickSize,
    decimal TickValue,
    DateTimeOffset? Expiration)
{
    /// <summary>Valor financeiro de 1 ponto para 1 contrato (WDO: 5 / 0,5 = R$ 10).</summary>
    public decimal PointValue => TickSize == 0 ? 0 : TickValue / TickSize;

    public decimal RoundToTick(decimal price) =>
        TickSize == 0 ? price : Math.Round(price / TickSize, MidpointRounding.AwayFromZero) * TickSize;

    /// <summary>Arredonda sempre para o lado que protege (stop mais apertado).</summary>
    public decimal RoundStopTowardsEntry(decimal stop, Side side)
    {
        if (TickSize == 0) return stop;
        var ticks = stop / TickSize;
        ticks = side == Side.Buy ? Math.Ceiling(ticks) : Math.Floor(ticks);
        return ticks * TickSize;
    }

    public decimal Money(decimal points, int contracts) => points * PointValue * contracts;

    /// <summary>
    /// Custo conservador estimado do ciclo completo por contrato, igual ao usado na pesquisa histórica.
    /// O valor efetivo só existe depois do fechamento e vem nos negócios informados pelo MT5.
    /// </summary>
    public decimal EstimatedRoundTripCostPerContract =>
        Symbol.StartsWith("WDO", StringComparison.OrdinalIgnoreCase) ? 5m :
        Symbol.StartsWith("WIN", StringComparison.OrdinalIgnoreCase) ? 2m : 0m;

    public decimal EstimatedRoundTripCost(int contracts) => EstimatedRoundTripCostPerContract * Math.Max(0, contracts);
}
