using System.Security.Cryptography;
using System.Text;

namespace GarticClone.Server.Transport;

public sealed record HandshakeResult(
    bool Ok,
    string? Key,
    int Status,
    string Reason,
    IReadOnlyDictionary<string, string>? ExtraHeaders = null)
{
    public static HandshakeResult Accept(string key) => new(true, key, 101, "Switching Protocols");
    public static HandshakeResult Reject(int status, string reason, IReadOnlyDictionary<string, string>? headers = null) =>
        new(false, null, status, reason, headers);
}

public static class Handshake
{
    
    public static string ComputeAcceptKey(string secWebSocketKey) =>
        Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(secWebSocketKey + WsConstants.Guid)));

    public static HandshakeResult Validate(HttpRequest req, IReadOnlyCollection<string>? allowedOrigins = null)
    {
        if (req.Method != "GET")
            return HandshakeResult.Reject(405, "Method Not Allowed");

        bool upgradeOk = string.Equals(req.Header("Upgrade"), "websocket", StringComparison.OrdinalIgnoreCase);
        bool connectionOk = (req.Header("Connection") ?? "")
            .Split(',', StringSplitOptions.TrimEntries)
            .Contains("upgrade", StringComparer.OrdinalIgnoreCase);
        if (!upgradeOk || !connectionOk)
            return HandshakeResult.Reject(400, "Bad Request");

        if (req.Header("Sec-WebSocket-Version") != "13")
            return HandshakeResult.Reject(426, "Upgrade Required",
                new Dictionary<string, string> { ["Sec-WebSocket-Version"] = "13" });

        string? key = req.Header("Sec-WebSocket-Key");
        if (key is null || !IsValidKey(key))
            return HandshakeResult.Reject(400, "Bad Request");

        if (allowedOrigins is not null && !allowedOrigins.Contains(req.Header("Origin") ?? ""))
            return HandshakeResult.Reject(403, "Forbidden");

        return HandshakeResult.Accept(key);
    }

    private static bool IsValidKey(string key)
    {
        Span<byte> decoded = stackalloc byte[32];
        return Convert.TryFromBase64String(key, decoded, out int written) && written == 16;
    }

    public static string BuildAcceptResponse(string key) =>
        "HTTP/1.1 101 Switching Protocols\r\n" +
        "Upgrade: websocket\r\n" +
        "Connection: Upgrade\r\n" +
        $"Sec-WebSocket-Accept: {ComputeAcceptKey(key)}\r\n\r\n";

    public static string BuildRejectResponse(int status, string reason, IReadOnlyDictionary<string, string>? headers = null)
    {
        var sb = new StringBuilder($"HTTP/1.1 {status} {reason}\r\nConnection: close\r\nContent-Length: 0\r\n");
        if (headers is not null)
            foreach (var (k, v) in headers) sb.Append($"{k}: {v}\r\n");
        return sb.Append("\r\n").ToString();
    }
}
