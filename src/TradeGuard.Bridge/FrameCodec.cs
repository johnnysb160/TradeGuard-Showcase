using System.Security.Cryptography;
using System.Text;

namespace TradeGuard.Bridge;

/// <summary>
/// Protocolo de linha: <c>{json}\t{hmac-sha256-hex}\n</c>.
/// O HMAC cobre os bytes exatos do JSON, então não há problema de canonicalização entre MQL5 e .NET.
/// </summary>
public static class FrameCodec
{
    public static string Encode(string json, ReadOnlySpan<byte> key) =>
        json + "\t" + Mac(json, key) + "\n";

    public static bool TryDecode(string line, ReadOnlySpan<byte> key, out string json)
    {
        json = "";
        var tab = line.LastIndexOf('\t');
        if (tab <= 0) return false;

        var body = line[..tab];
        var mac = line[(tab + 1)..].Trim();
        var expected = Mac(body, key);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(mac.ToLowerInvariant())))
            return false;

        json = body;
        return true;
    }

    public static string Mac(string json, ReadOnlySpan<byte> key) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(json)));

    /// <summary>Chave da sessão: HMAC(master, "tg-session|{nonceEA}|{nonceServidor}").</summary>
    public static byte[] DeriveSessionKey(ReadOnlySpan<byte> masterKey, string eaNonce, string serverNonce) =>
        HMACSHA256.HashData(masterKey, Encoding.UTF8.GetBytes($"tg-session|{eaNonce}|{serverNonce}"));

    public static string NewNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
