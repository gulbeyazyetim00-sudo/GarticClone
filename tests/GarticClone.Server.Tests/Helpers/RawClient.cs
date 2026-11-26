using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using GarticClone.Server.Transport;

namespace GarticClone.Server.Tests.Helpers;

public sealed class RawClient : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly FrameParser _parser = new(16 << 20, requireMask: false);
    private readonly List<Frame> _frames = [];
    private readonly SemaphoreSlim _arrived = new(0);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Status { get; private set; }
    public string Head { get; private set; } = "";
    public string ExpectedAccept { get; private set; } = "";
    public Task Closed => _closed.Task;

    private RawClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public static async Task<RawClient> ConnectAsync(int port, string path = "/ws")
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port);
        var client = new RawClient(tcp);

        string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        client.ExpectedAccept = Handshake.ComputeAcceptKey(key);
        string request =
            $"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            $"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n";
        await client._stream.WriteAsync(Encoding.ASCII.GetBytes(request));

        
        var buffer = new List<byte>();
        var chunk = new byte[4096];
        int end;
        while ((end = IndexOfHeadEnd(buffer)) < 0)
        {
            int n = await client._stream.ReadAsync(chunk);
            if (n == 0) break;
            buffer.AddRange(chunk.AsSpan(0, n).ToArray());
        }
        if (end < 0) end = buffer.Count;
        client.Head = Encoding.ASCII.GetString(buffer.GetRange(0, end).ToArray());
        client.Status = int.Parse(client.Head.Split(' ')[1]);

        if (end + 4 < buffer.Count) client.Ingest(buffer.GetRange(end + 4, buffer.Count - end - 4).ToArray());
        _ = Task.Run(client.ReadLoopAsync);
        return client;
    }

    private static int IndexOfHeadEnd(List<byte> b)
    {
        for (int i = 0; i + 3 < b.Count; i++)
            if (b[i] == '\r' && b[i + 1] == '\n' && b[i + 2] == '\r' && b[i + 3] == '\n') return i;
        return -1;
    }

    private async Task ReadLoopAsync()
    {
        var buf = new byte[16384];
        try
        {
            int n;
            while ((n = await _stream.ReadAsync(buf)) > 0) Ingest(buf.AsSpan(0, n).ToArray());
        }
        catch { /* closed */ }
        _closed.TrySetResult();
    }

    private void Ingest(byte[] data)
    {
        var frames = _parser.Push(data);
        lock (_frames) _frames.AddRange(frames);
        if (frames.Count > 0) _arrived.Release(frames.Count);
    }

    
    public async Task<Frame> WaitForAsync(Func<Frame, bool> predicate, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            lock (_frames)
            {
                int i = _frames.FindIndex(f => predicate(f));
                if (i >= 0)
                {
                    var f = _frames[i];
                    _frames.RemoveAt(i);
                    return f;
                }
            }
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !await _arrived.WaitAsync(left))
                throw new TimeoutException("WaitForAsync timed out");
        }
    }

    public void SendFrame(byte opcode, ReadOnlySpan<byte> payload, bool fin = true, bool masked = true) =>
        _stream.Write(FrameEncoder.Encode(opcode, payload, fin, masked ? RandomNumberGenerator.GetBytes(4) : null));

    public void Dispose() => _tcp.Dispose();
}
