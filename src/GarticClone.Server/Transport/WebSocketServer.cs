using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GarticClone.Server.Utils;

namespace GarticClone.Server.Transport;


public sealed record HttpResponse(int Status, string ContentType, string Body);


public sealed class WebSocketServer
{
    private readonly TcpListener _listener;
    private readonly string _wsPath;
    private readonly int _maxMessageSize;
    private readonly IReadOnlyCollection<string>? _allowedOrigins;
    private readonly CancellationTokenSource _cts = new();
    private readonly Logger _log = Log.For("transport");

    public ConcurrentDictionary<int, WebSocketConnection> Clients { get; } = new();

    
    public event Action<WebSocketConnection, HttpRequest>? ConnectionOpened;

   
    public event Action<int, string, string>? Rejected;

    
    public Func<HttpRequest, HttpResponse?>? HttpHandler { get; set; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public WebSocketServer(IPAddress address, int port, string wsPath = "/ws",
        int maxMessageSize = 1 << 20, IReadOnlyCollection<string>? allowedOrigins = null)
    {
        _listener = new TcpListener(address, port);
        _wsPath = wsPath;
        _maxMessageSize = maxMessageSize;
        _allowedOrigins = allowedOrigins;
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptSocketAsync(_cts.Token);
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException ex)
            {
                _log.Warn($"accept failed: {ex.Message}");
                continue;
            }
            _ = Task.Run(() => HandleClientAsync(socket));
        }
    }

    private async Task HandleClientAsync(Socket socket)
    {
        var stream = new NetworkStream(socket, ownsSocket: true);
        string remote = socket.RemoteEndPoint?.ToString() ?? "unknown";
        try
        {
            using var headTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            headTimeout.CancelAfter(TimeSpan.FromSeconds(10));

            var (request, leftover) = await HttpRequest.ReadAsync(stream, 8192, headTimeout.Token);
            if (request is null)
            {
                await RejectAsync(stream, 400, "Bad Request", remote);
                return;
            }

            if (request.Header("Upgrade") is not null)
            {
                await HandleUpgradeAsync(socket, stream, request, leftover, remote);
                return; 
            }

            await HandleHttpAsync(stream, request);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            stream.Dispose();
        }
    }

    private async Task HandleUpgradeAsync(Socket socket, NetworkStream stream, HttpRequest request, byte[] leftover, string remote)
    {
        if (request.Path != _wsPath)
        {
            await RejectAsync(stream, 404, "Not Found", remote);
            return;
        }

        var result = Handshake.Validate(request, _allowedOrigins);
        if (!result.Ok)
        {
            await RejectAsync(stream, result.Status, result.Reason, remote, result.ExtraHeaders);
            return;
        }

        await stream.WriteAsync(Encoding.ASCII.GetBytes(Handshake.BuildAcceptResponse(result.Key!)));

        var conn = new WebSocketConnection(socket, stream, _maxMessageSize);
        Clients[conn.Id] = conn;
        conn.Closed += (_, _) => Clients.TryRemove(conn.Id, out _);

        
        ConnectionOpened?.Invoke(conn, request);
        conn.Start(leftover);
    }

    private async Task HandleHttpAsync(NetworkStream stream, HttpRequest request)
    {
        var response = request.Method == "GET" ? HttpHandler?.Invoke(request) : null;
        response ??= new HttpResponse(404, "text/plain", "Not Found");

        byte[] body = Encoding.UTF8.GetBytes(response.Body);
        string head =
            $"HTTP/1.1 {response.Status} {(response.Status == 200 ? "OK" : "Not Found")}\r\n" +
            $"Content-Type: {response.ContentType}; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(body);
        stream.Socket.Shutdown(SocketShutdown.Send);
        stream.Dispose();
    }

    private async Task RejectAsync(NetworkStream stream, int status, string reason, string remote,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        Rejected?.Invoke(status, reason, remote);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(Handshake.BuildRejectResponse(status, reason, headers)));
        stream.Socket.Shutdown(SocketShutdown.Send);
        stream.Dispose();
    }

    public async Task StopAsync(TimeSpan? timeout = null)
    {
        _cts.Cancel();
        _listener.Stop();
        foreach (var conn in Clients.Values) conn.Close(CloseCode.GoingAway, "server shutting down");

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        while (!Clients.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(20);
        foreach (var conn in Clients.Values) conn.Terminate();
    }
}
