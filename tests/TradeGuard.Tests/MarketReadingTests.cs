using TradeGuard.Domain;

namespace TradeGuard.Tests;

public class MarketReadingTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    // O app roda em pt-BR (vírgula decimal); os textos são testados do mesmo jeito.
    public MarketReadingTests() =>
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");

    private static List<Candle> Rising()
    {
        var bars = new List<Candle>();
        decimal p = 5000;
        for (var i = 0; i < 40; i++) { bars.Add(new(Start.AddMinutes(5 * i), p, p + 2, p - 1, p + 1.5m)); p += 1.5m; }
        return bars;
    }

    [Fact]
    public void Too_little_data_says_loading() =>
        Assert.Contains("Carregando", MarketReader.Read([], null, [], 0, 0, 10, []).Headline);

    [Fact]
    public void Uptrend_without_setup_says_waiting_to_buy_and_why_not_now()
    {
        var bars = Rising();
        var last = bars[^1].Close;
        var levels = new DayLevels(5000, last + 4, 4990, null, null, null); // máxima 4 pts acima
        var r = MarketReader.Read(bars, levels, [], last - 0.5m, last, 10, []);

        Assert.Equal(Trend.Up, r.Trend);
        Assert.Equal("Sem sinal agora. Mercado subindo. Esperando um ponto bom para COMPRAR.", r.Headline);
        Assert.Equal(ReadingTone.Blocked, r.Tone);
        Assert.StartsWith("Mas agora não: só", r.Subline);
        Assert.Equal(StepState.Waiting, r.Steps[1].State);
        Assert.Equal(StepState.Blocked, r.Steps[2].State);
    }

    [Fact]
    public void Armed_setup_headline_tells_the_price_and_the_distance()
    {
        var bars = Rising();
        var armed = new[] { new SetupCandidate(SetupNames.ContinuousPoint, Side.Buy, 5062m, 5055m, Start.AddHours(4), "") };
        var r = MarketReader.Read(bars, null, armed, 5059.5m, 5060m, 10, []);

        Assert.Equal("Sem sinal agora. Mercado subindo. Se o preço subir acima de 5062,0, o robô avisa para COMPRAR.", r.Headline);
        Assert.EndsWith("Até o aviso, não entre.", r.Subline);
        Assert.StartsWith("Faltam 2,0 pts", r.Subline);
        Assert.Contains("voltou até a média", r.Subline);
        Assert.Equal(StepState.Waiting, r.Steps[1].State); // armado ainda não é sinal
    }

    [Fact]
    public void Active_lock_is_the_reason_shown()
    {
        var bars = Rising();
        var blocks = new[] { new Block(BlockReason.Cooldown, "Pausa após perda até 12:10.") };
        var r = MarketReader.Read(bars, null, [], 5060, 5060.5m, 10, blocks);
        Assert.Equal("Mas agora não: Pausa após perda até 12:10.", r.Subline);
        Assert.Equal(StepState.Blocked, r.Steps[3].State);
    }
}
