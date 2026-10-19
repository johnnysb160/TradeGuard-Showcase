using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TradeGuard.Bridge;

public sealed class BridgeOptions
{
    /// <summary>
    /// Nome do named pipe (\\.\pipe\{PipeName}). Canal local do Windows: sem IP e sem porta, então não
    /// conflita com nada que você for depurar, e o MT5 não exige lista de WebRequest.
    /// </summary>
    public string PipeName { get; set; } = "TradeGuard.Bridge";
    public int HandshakeTimeoutSeconds { get; set; } = 5;
    /// <summary>Sem nenhuma mensagem do MT5 por esse tempo, a conexão é derrubada.</summary>
    public int IdleTimeoutSeconds { get; set; } = 10;
}

public abstract record BridgeEvent;

public sealed record BridgeConnected(HelloMessage Hello) : BridgeEvent;

public sealed record BridgeDisconnected(string Reason) : BridgeEvent;

public sealed record BridgeInbound(InboundMessage Message) : BridgeEvent;

/// <summary>A ponte não conseguiu nem começar (ex.: outro programa já usa o pipe).</summary>
public sealed record BridgeFailed(string Reason) : BridgeEvent;

/// <summary>
/// Servidor de named pipe; o EA no MT5 é o cliente. Uma conexão por vez.
/// O pipe só aceita o usuário atual do Windows. Autenticação: hello/welcome assinados com a chave
/// mestra; depois tudo é assinado com a chave de sessão e a sequência precisa sempre crescer (anti-replay).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BridgeServer(BridgeOptions options, byte[] masterKey, ILogger<BridgeServer> logger) : IAsyncDisposable
{
    private readonly Channel<BridgeEvent> _events = Channel.CreateUnbounded<BridgeEvent>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Session? _session;
    private int _rejectedHandshakes;

    public ChannelReader<BridgeEvent> Events => _events.Reader;

    /// <summary>Quantas conexões foram recusadas por chave errada desde que o app abriu.</summary>
    public int RejectedHandshakes => Volatile.Read(ref _rejectedHandshakes);

    public bool IsConnected => _session is { Closed: false };

    public string PipePath => $@"\\.\pipe\{options.PipeName}";

    public async Task RunAsync(CancellationToken ct)
    {
        logger.LogInformation("Ponte MT5 em {Pipe}", PipePath);
        var firstAttempt = true;

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (firstAttempt)
                {
                    var reason = $"O canal {PipePath} já está em uso por outro programa ({ex.Message}).";
                    logger.LogError("{Reason}", reason);
                    await _events.Writer.WriteAsync(new BridgeFailed(reason), ct);
                }
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                continue;
            }
            firstAttempt = false;

            try
            {
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }

            await HandleClientAsync(pipe, ct);
        }
    }

    /// <summary>Pipe restrito ao usuário atual (nem outros usuários da máquina, nem rede).</summary>
    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        var me = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));

        return NamedPipeServerStreamAcl.Create(options.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: 64 * 1024, outBufferSize: 64 * 1024, security);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        var session = new Session(pipe);

        try
        {
            using var hsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hsCts.CancelAfter(TimeSpan.FromSeconds(options.HandshakeTimeoutSeconds));
            var line = await reader.ReadLineAsync(hsCts.Token);
            if (line is null || !FrameCodec.TryDecode(line, masterKey, out var json)
                || InboundParser.Parse(json) is not HelloMessage hello)
            {
                Interlocked.Increment(ref _rejectedHandshakes);
                logger.LogWarning("Conexão recusada: handshake inválido ({Why})",
                    line is null ? "EA não enviou nada" : line.Length < 20 ? $"linha curta: {line}" : "assinatura não confere: EA com chave antiga");
                await session.CloseAsync();
                return;
            }

            var serverNonce = FrameCodec.NewNonce();
            var welcome = CommandWriter.ToJson(new WelcomeCommand(serverNonce), 0);
            await session.WriteRawAsync(FrameCodec.Encode(welcome, masterKey), ct);
            session.Key = FrameCodec.DeriveSessionKey(masterKey, hello.Nonce, serverNonce);
            _session = session;

            logger.LogInformation("MT5 conectado: conta {Account} ({Server}) {Symbol} demo={Demo}",
                hello.Account, hello.Server, hello.Symbol, hello.IsDemo);
            await _events.Writer.WriteAsync(new BridgeConnected(hello), ct);

            while (!ct.IsCancellationRequested)
            {
                using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idleCts.CancelAfter(TimeSpan.FromSeconds(options.IdleTimeoutSeconds));
                line = await reader.ReadLineAsync(idleCts.Token);
                if (line is null) throw new IOException("MT5 fechou a conexão");

                if (!FrameCodec.TryDecode(line, session.Key, out json))
                    throw new InvalidDataException("assinatura inválida");
                var msg = InboundParser.Parse(json);
                if (msg.Seq <= session.LastInboundSeq)
                    throw new InvalidDataException($"sequência fora de ordem ({msg.Seq})");
                session.LastInboundSeq = msg.Seq;

                await _events.Writer.WriteAsync(new BridgeInbound(msg), ct);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await DisconnectAsync(session, "MT5 sem responder (timeout)");
        }
        catch (OperationCanceledException)
        {
            await session.CloseAsync();
        }
        catch (Exception ex)
        {
            await DisconnectAsync(session, ex.Message);
        }
    }

    private async Task DisconnectAsync(Session session, string reason)
    {
        await session.CloseAsync();
        if (ReferenceEquals(_session, session))
        {
            _session = null;
            logger.LogWarning("MT5 desconectado: {Reason}", reason);
            await _events.Writer.WriteAsync(new BridgeDisconnected(reason));
        }
    }

    public async Task<bool> SendAsync(BridgeCommand command, CancellationToken ct = default)
    {
        var session = _session;
        if (session is null || session.Closed) return false;

        await _sendLock.WaitAsync(ct);
        try
        {
            var json = CommandWriter.ToJson(command, ++session.LastOutboundSeq);
            await session.WriteRawAsync(FrameCodec.Encode(json, session.Key), ct);
            return true;
        }
        catch (Exception ex)
        {
            await DisconnectAsync(session, "falha ao enviar: " + ex.Message);
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is { } s) await s.CloseAsync();
        _events.Writer.TryComplete();
    }

    private sealed class Session(NamedPipeServerStream pipe)
    {
        public byte[] Key { get; set; } = [];
        public long LastInboundSeq { get; set; }
        public long LastOutboundSeq { get; set; }
        public bool Closed { get; private set; }

        public async Task WriteRawAsync(string frame, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(frame);
            await pipe.WriteAsync(bytes, ct);
            await pipe.FlushAsync(ct);
        }

        public async Task CloseAsync()
        {
            if (Closed) return;
            Closed = true;
            try { if (pipe.IsConnected) pipe.Disconnect(); } catch (IOException) { }
            await pipe.DisposeAsync();
        }
    }
}
