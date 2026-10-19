using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradeGuard.Bridge;
using TradeGuard.Domain;
using TradeGuard.Infrastructure;

namespace TradeGuard.Tests;

public class TradeReconstructorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 13, 0, 0, TimeSpan.Zero);

    private static DealRecord D(ulong ticket, long pid, int minute, Side side, int vol, decimal price, decimal profit, string entry) =>
        new(ticket, pid, "WDOV26", T0.AddMinutes(minute), side, vol, price, profit, entry);

    [Fact]
    public void Simple_round_trip()
    {
        var trades = TradeReconstructor.FromDeals(
        [
            D(1, 100, 0, Side.Buy, 1, 5000m, 0m, "in"),
            D(2, 100, 5, Side.Sell, 1, 5006m, 60m, "out")
        ], "MT5");

        var t = Assert.Single(trades);
        Assert.Equal(100, t.PositionId);
        Assert.Equal(Side.Buy, t.Side);
        Assert.Equal(6m, t.PnlPoints);
        Assert.Equal(60m, t.PnlMoney);
        Assert.Equal(new DateOnly(2026, 9, 24), t.TradingDay);
    }

    [Fact]
    public void Partial_exits_average_the_exit_price()
    {
        var t = Assert.Single(TradeReconstructor.FromDeals(
        [
            D(1, 7, 0, Side.Sell, 2, 5010m, 0m, "in"),
            D(2, 7, 2, Side.Buy, 1, 5005m, 50m, "out"),
            D(3, 7, 4, Side.Buy, 1, 5009m, 10m, "out")
        ], "MT5"));
        Assert.Equal(Side.Sell, t.Side);
        Assert.Equal(2, t.Contracts);
        Assert.Equal(5007m, t.ExitPrice);
        Assert.Equal(3m, t.PnlPoints);
        Assert.Equal(60m, t.PnlMoney);
    }

    [Fact]
    public void Open_position_and_truncated_history_are_ignored()
    {
        Assert.Empty(TradeReconstructor.FromDeals([D(1, 1, 0, Side.Buy, 1, 5000m, 0m, "in")], "MT5"));
        Assert.Empty(TradeReconstructor.FromDeals([D(2, 2, 0, Side.Sell, 1, 5000m, 30m, "out")], "MT5"));
    }

    [Fact]
    public void History_messages_are_parsed()
    {
        var h = Assert.IsType<HistoryDealMessage>(InboundParser.Parse(
            "{\"t\":\"hdeal\",\"seq\":9,\"ticket\":5,\"pid\":77,\"sym\":\"WDOV26\",\"time\":1790200000,\"side\":\"buy\",\"vol\":1,\"price\":5000.0,\"profit\":0,\"entry\":\"in\",\"comment\":\"\"}"));
        Assert.Equal(77, h.Deal.PositionId);
        Assert.Equal("WDOV26", h.Deal.Symbol);
        Assert.IsType<HistoryEndMessage>(InboundParser.Parse("{\"t\":\"hend\",\"seq\":10}"));
    }
}

public class JournalImportTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly JournalStore _journal;

    public JournalImportTests()
    {
        _conn.Open();
        var options = new DbContextOptionsBuilder<TradeGuardDb>().UseSqlite(_conn).Options;
        _journal = new JournalStore(new Factory(options));
    }

    public void Dispose() => _conn.Dispose();

    private static TradeRecord Trade(long pid, decimal pnl) => new()
    {
        PositionId = pid, PnlMoney = pnl, TradingDay = new DateOnly(2026, 9, 24),
        OpenedAtUtc = DateTimeOffset.UtcNow, ClosedAtUtc = DateTimeOffset.UtcNow, Symbol = "WDOV26"
    };

    [Fact]
    public async Task Import_never_duplicates_a_position()
    {
        await _journal.EnsureCreatedAsync();
        Assert.Single(await _journal.ImportAsync([Trade(1, 50)]));
        var second = await _journal.ImportAsync([Trade(1, 50), Trade(2, -20)]);
        Assert.Equal(2, Assert.Single(second).PositionId);
        Assert.Equal(2, (await _journal.AllTradesAsync()).Count);
    }

    [Fact]
    public async Task Old_database_gets_the_new_column_without_losing_data()
    {
        // Simula o banco da versão anterior (sem PositionId) com um trade gravado.
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE Trades (Id TEXT PRIMARY KEY, TradingDay TEXT, Symbol TEXT, Side TEXT, OpenedAtUtc INTEGER,
                  ClosedAtUtc INTEGER, Contracts INTEGER, EntryPrice TEXT, ExitPrice TEXT, InitialStop TEXT, PnlMoney TEXT,
                  PnlPoints TEXT, InitialRiskMoney TEXT, MfePoints TEXT, MaePoints TEXT, Source TEXT, ExitReason TEXT,
                  Setup TEXT, Notes TEXT);
                INSERT INTO Trades VALUES ('0e1a2b3c-0000-0000-0000-000000000001','2026-09-23','WDOV26','Buy',0,0,1,'5000','5005',
                  '4995','50','5','50','5','0','app','','','');
                """;
            cmd.ExecuteNonQuery();
        }

        await _journal.EnsureCreatedAsync();
        Assert.Single(await _journal.AllTradesAsync());
        Assert.Single(await _journal.ImportAsync([Trade(9, 10)]));
    }

    private sealed class Factory(DbContextOptions<TradeGuardDb> options) : IDbContextFactory<TradeGuardDb>
    {
        public TradeGuardDb CreateDbContext() => new(options);
    }
}
