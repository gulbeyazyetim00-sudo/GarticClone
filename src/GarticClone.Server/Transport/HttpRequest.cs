using System.Text;

namespace GarticClone.Server.Transport;


public sealed class HttpRequest
{
    public required string Method { get; init; }
    public required string Target { get; init; }
    public required string Path { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;

 
    public static HttpRequest? Parse(string head)
    {
        var lines = head.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            string name = lines[i][..colon].Trim();
            string value = lines[i][(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out var prev) ? $"{prev}, {value}" : value;
        }

        string target = requestLine[1];
        int q = target.IndexOf('?');
        return new HttpRequest
        {
            Method = requestLine[0],
            Target = target,
            Path = q >= 0 ? target[..q] : target,
            Headers = headers,
        };
    }

   
    public static async Task<(HttpRequest? Request, byte[] Leftover)> ReadAsync(
        Stream stream, int maxHeadBytes, CancellationToken ct)
    {
        var buffer = new byte[maxHeadBytes];
        int length = 0;

        while (length < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (n == 0) return (null, []);
            length += n;

            int end = IndexOfHeadEnd(buffer.AsSpan(0, length));
            if (end >= 0)
            {
                string head = Encoding.ASCII.GetString(buffer, 0, end);
                byte[] leftover = buffer.AsSpan(end + 4, length - end - 4).ToArray();
                return (Parse(head), leftover);
            }
        }
        return (null, []); // head too large
    }

    private static int IndexOfHeadEnd(ReadOnlySpan<byte> data) => data.IndexOf("\r\n\r\n"u8);
}
