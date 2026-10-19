using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TradeGuard.Bridge;
using TradeGuard.Domain;

namespace TradeGuard.Tests;

public class FrameCodecTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Roundtrip()
    {
        var frame = FrameCodec.Encode("{\"t\":\"hb\",\"seq\":1}", Key);
        Assert.EndsWith("\n", frame);
        Assert.True(FrameCodec.TryDecode(frame.TrimEnd('\n'), Key, out var json));
        Assert.Equal("{\"t\":\"hb\",\"seq\":1}", json);
    }

    [Fact]
    public void Tampered_payload_is_rejected()
    {
        var frame = FrameCodec.Encode("{\"t\":\"open\",\"vol\":1}", Key).Replace("\"vol\":1", "\"vol\":9");
        Assert.False(FrameCodec.TryDecode(frame.TrimEnd('\n'), Key, out _));
    }

    [Fact]
    public void Wrong_key_is_rejected()
    {
        var frame = FrameCodec.Encode("{}", Key);
        Assert.False(FrameCodec.TryDecode(frame.TrimEnd('\n'), RandomNumberGenerator.GetBytes(32), out _));
    }

    [Fact]
    public void Known_hmac_vector()
    {
        // RFC 4231, caso 2: garante que o HMAC do .NET e o do MQL5 (manual) seguem o padrão.
        var mac = FrameCodec.Mac("what do ya want for nothing?", "Jefe"u8);
        Assert.Equal("5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843", mac);
    }

    [Fact]
    public void Command_json_is_flat_ascii()
    {
        var json = CommandWriter.ToJson(new OpenCommand("abc", Side.Buy, 1, 5170.5m, 5180m, "Ação \"x\""), 7);
        Assert.Equal("{\"t\":\"open\",\"seq\":7,\"id\":\"abc\",\"side\":\"buy\",\"vol\":1,\"sl\":5170.5,\"tp\":5180,\"comment\":\"Acao x\"}", json);
    }

    [Fact]
    public void Parses_inbound_messages()
    {
        var pos = Assert.IsType<PositionMessage>(InboundParser.Parse(
            "{\"t\":\"pos\",\"seq\":3,\"side\":\"sell\",\"vol\":2,\"price\":5173.500,\"sl\":5178.000,\"tp\":5163.000,\"profit\":-10.00}"));
        Assert.Equal(Side.Sell, pos.AsSide);
        Assert.Equal(2, pos.Volume);
        Assert.Equal(5178m, pos.StopLoss);
        Assert.Equal(0, pos.PositionId); // compatível com EA anterior

        var identified = Assert.IsType<PositionMessage>(InboundParser.Parse(
            "{\"t\":\"pos\",\"seq\":4,\"side\":\"buy\",\"vol\":1,\"price\":5200,\"sl\":5195,\"tp\":5700,\"profit\":2,\"pid\":987,\"opened\":1790200000,\"comment\":\"TG|R03\"}"));
        Assert.Equal(987, identified.PositionId);
        Assert.Equal("TG|R03", identified.Comment);
        Assert.Equal(1790200000, identified.OpenedAtServer);

        var deal = Assert.IsType<DealMessage>(InboundParser.Parse(
            "{\"t\":\"deal\",\"seq\":5,\"ticket\":123,\"time\":1790200000,\"side\":\"buy\",\"vol\":1,\"price\":5170.0,\"profit\":35.00,\"entry\":\"out\",\"comment\":\"tp\"}"));
        Assert.Equal("out", deal.Entry);
        Assert.Equal(35m, deal.Profit);

        var acct = Assert.IsType<AccountMessage>(InboundParser.Parse(
            "{\"t\":\"acct\",\"seq\":5,\"balance\":1,\"equity\":1,\"free\":1,\"srv\":0,\"gmt\":0,\"conn\":0,\"algo\":1}"));
        Assert.False(acct.BrokerConnected);
        Assert.True(acct.AlgoAllowed);

        // EA antigo sem os campos: assume ok (não bloqueia por falta de informação).
        var legacy = Assert.IsType<AccountMessage>(InboundParser.Parse(
            "{\"t\":\"acct\",\"seq\":6,\"balance\":1,\"equity\":1,\"free\":1,\"srv\":0,\"gmt\":0}"));
        Assert.True(legacy.BrokerConnected);
    }
}

public class BridgeServerTests
{
    private static readonly byte[] Master = RandomNumberGenerator.GetBytes(32);

    private static string Hello(string nonce) =>
        "{\"t\":\"hello\",\"seq\":0,\"v\":1,\"nonce\":\"" + nonce + "\",\"acct\":42,\"server\":\"ClearInvestimentos-DEMO\"," +
        "\"company\":\"Clear\",\"demo\":1,\"netting\":1,\"sym\":\"WDOV26\",\"tick\":0.5,\"tickval\":5,\"digits\":3," +
        "\"exp\":0,\"srv\":1790200000,\"gmt\":1790210800}";

    private static (BridgeServer server, CancellationTokenSource cts, string pipe) Start()
    {
        var pipe = "TradeGuard.Test." + Guid.NewGuid().ToString("N");
        var server = new BridgeServer(new BridgeOptions { PipeName = pipe }, Master, NullLogger<BridgeServer>.Instance);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = server.RunAsync(cts.Token);
        return (server, cts, pipe);
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipe, CancellationToken ct)
    {
        var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000, ct);
        return client;
    }

    [Fact]
    public void Default_pipe_name_is_exclusive()
    {
        Assert.Equal("TradeGuard.Bridge", new BridgeOptions().PipeName);
    }

    [Fact]
    public async Task Handshake_then_signed_messages_flow_both_ways()
    {
        var (server, cts, pipe) = Start();
        await using var stream = await ConnectAsync(pipe, cts.Token);
        var reader = new StreamReader(stream, Encoding.UTF8);

        const string nonce = "aabbccddeeff00112233445566778899";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(FrameCodec.Encode(Hello(nonce), Master)));

        var welcomeLine = await reader.ReadLineAsync(cts.Token);
        Assert.True(FrameCodec.TryDecode(welcomeLine!, Master, out var welcome));
        var serverNonce = System.Text.Json.JsonDocument.Parse(welcome).RootElement.GetProperty("nonce").GetString()!;
        var session = FrameCodec.DeriveSessionKey(Master, nonce, serverNonce);

        var connected = Assert.IsType<BridgeConnected>(await server.Events.ReadAsync(cts.Token));
        Assert.Equal("WDOV26", connected.Hello.Symbol);
        Assert.Equal(-10800, connected.Hello.ServerOffsetSeconds);

        await stream.WriteAsync(Encoding.UTF8.GetBytes(FrameCodec.Encode(
            "{\"t\":\"tick\",\"seq\":1,\"bid\":5173,\"ask\":5173.5,\"last\":5173,\"tms\":0}", session)));
        var tick = Assert.IsType<TickMessage>(Assert.IsType<BridgeInbound>(await server.Events.ReadAsync(cts.Token)).Message);
        Assert.Equal(5173.5m, tick.Ask);

        Assert.True(await server.SendAsync(new ModifyCommand("id1", 5170m, 5180m)));
        var cmdLine = await reader.ReadLineAsync(cts.Token);
        Assert.True(FrameCodec.TryDecode(cmdLine!, session, out var cmd));
        Assert.Contains("\"t\":\"modify\"", cmd);

        await cts.CancelAsync();
    }

    [Fact]
    public async Task Wrong_key_is_refused()
    {
        var (server, cts, pipe) = Start();
        await using var stream = await ConnectAsync(pipe, cts.Token);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(FrameCodec.Encode(Hello("00"), RandomNumberGenerator.GetBytes(32))));

        var buffer = new byte[64];
        var read = await stream.ReadAsync(buffer, cts.Token);
        Assert.Equal(0, read); // servidor fechou sem responder
        Assert.False(server.IsConnected);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task Replayed_sequence_drops_connection()
    {
        var (server, cts, pipe) = Start();
        await using var stream = await ConnectAsync(pipe, cts.Token);
        var reader = new StreamReader(stream, Encoding.UTF8);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(FrameCodec.Encode(Hello("11"), Master)));
        FrameCodec.TryDecode((await reader.ReadLineAsync(cts.Token))!, Master, out var welcome);
        var serverNonce = System.Text.Json.JsonDocument.Parse(welcome).RootElement.GetProperty("nonce").GetString()!;
        var session = FrameCodec.DeriveSessionKey(Master, "11", serverNonce);
        await server.Events.ReadAsync(cts.Token); // connected

        var frame = FrameCodec.Encode("{\"t\":\"newsend\",\"seq\":5}", session);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(frame));
        await stream.WriteAsync(Encoding.UTF8.GetBytes(frame)); // replay

        Assert.IsType<BridgeInbound>(await server.Events.ReadAsync(cts.Token));
        Assert.IsType<BridgeDisconnected>(await server.Events.ReadAsync(cts.Token));
        await cts.CancelAsync();
    }

    [Fact]
    public async Task Reconnects_after_client_leaves()
    {
        var (server, cts, pipe) = Start();
        for (var i = 0; i < 2; i++)
        {
            await using var stream = await ConnectAsync(pipe, cts.Token);
            var reader = new StreamReader(stream, Encoding.UTF8);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(FrameCodec.Encode(Hello("r" + i), Master)));
            Assert.NotNull(await reader.ReadLineAsync(cts.Token));
            Assert.IsType<BridgeConnected>(await server.Events.ReadAsync(cts.Token));
            stream.Close();
            Assert.IsType<BridgeDisconnected>(await server.Events.ReadAsync(cts.Token));
        }
        await cts.CancelAsync();
    }

    [Fact]
    public async Task Pipe_in_use_is_reported()
    {
        var pipe = "TradeGuard.Test." + Guid.NewGuid().ToString("N");
        using var blocker = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1);
        var server = new BridgeServer(new BridgeOptions { PipeName = pipe }, Master, NullLogger<BridgeServer>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = server.RunAsync(cts.Token);
        var failed = Assert.IsType<BridgeFailed>(await server.Events.ReadAsync(cts.Token));
        Assert.Contains("em uso", failed.Reason);
        await cts.CancelAsync();
    }
}
