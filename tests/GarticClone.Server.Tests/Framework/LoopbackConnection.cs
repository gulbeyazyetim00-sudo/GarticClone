using System.Net;
using System.Net.WebSockets;
using GarticClone.Server.Transport;

namespace GarticClone.Server.Tests.Helpers;


public sealed class LoopbackConnection : IAsyncDisposable
{
    private readonly WebSocketServer _server;
    public ClientWebSocket Client { get; }
    public WebSocketConnection Server { get; }

    private LoopbackConnection(WebSocketServer server, ClientWebSocket client, WebSocketConnection conn)
    {
        _server = server;
        Client = client;
        Server = conn;
    }

    public static async Task<LoopbackConnection> CreateAsync()
    {
        var server = new WebSocketServer(IPAddress.Loopback, 0);
        var opened = new TaskCompletionSource<WebSocketConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ConnectionOpened += (conn, _) => opened.TrySetResult(conn);
        server.Start();

        var client = new ClientWebSocket();
        client.Options.KeepAliveInterval = TimeSpan.Zero;
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/ws"), CancellationToken.None);
        var conn = await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return new LoopbackConnection(server, client, conn);
    }


    public Task<byte[]> ReceiveAsync() => ReceiveMessageAsync(Client);

    public static async Task<byte[]> ReceiveMessageAsync(ClientWebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (r.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException($"closed: {r.CloseStatus}");
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) return ms.ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Abort();
        Client.Dispose();
        await _server.StopAsync(TimeSpan.FromMilliseconds(200));
    }
}
