using System.Text.Json;
using GarticClone.Server.Modules;
using GarticClone.Server.Protocol;
using GarticClone.Server.Protocol.Opcodes;
using GarticClone.Server.Telemetry;
using GarticClone.Server.Transport;
using GarticClone.Server.Utils;

namespace GarticClone.Server;

public sealed class ServerApp
{
    private readonly Logger _log = Log.For("server");

    public WebSocketServer WebSockets { get; }
    public ServerServices Services { get; }
    public Dispatcher Dispatcher { get; }
    public Heartbeat Heartbeat { get; }
    public int Port => WebSockets.Port;

    private ServerApp(ServerConfig config)
    {
        OpcodeRegistry.Validate(); 

        var sessions = new SessionRegistry();
        var bandwidth = new BandwidthMonitor(config.TelemetryWindowMs, config.TelemetryBucketMs);
        Heartbeat = new Heartbeat(
            config.HeartbeatInterval,
            config.HeartbeatMaxMissed,
            onRtt: bandwidth.RecordRtt,
            onDead: id => _log.Warn($"client {id} missed {config.HeartbeatMaxMissed} heartbeats, terminating"));

        Services = new ServerServices { Sessions = sessions, Bandwidth = bandwidth, Heartbeat = Heartbeat };
        Dispatcher = new Dispatcher(Services);
        foreach (var module in ModuleList.Create()) Dispatcher.RegisterModule(module);

        WebSockets = new WebSocketServer(config.Host, config.Port, config.WsPath, config.MaxMessageSize, config.AllowedOrigins)
        {
            HttpHandler = HandleHttp,
        };
        WebSockets.Rejected += (status, reason, remote) => _log.Warn($"upgrade rejected {status} {reason} from {remote}");
        WebSockets.ConnectionOpened += (conn, _) => OnConnection(conn);
    }

    public static ServerApp Start(ServerConfig config)
    {
        var app = new ServerApp(config);
        app.WebSockets.Start();
        app.Heartbeat.Start();
        app._log.Info($"listening on ws://{config.Host}:{app.Port}{config.WsPath}");
        return app;
    }

    private void OnConnection(WebSocketConnection conn)
    {
        var session = new ClientSession(conn, Services.Bandwidth);
        Services.Sessions.Add(session);
        Services.Bandwidth.Attach(session.Id, conn);
        _log.Info($"connected {session.Id} ({conn.RemoteAddress})");

        conn.MessageReceived += (c, msg) =>
        {
            if (!msg.IsBinary)
            {
                c.Close(CloseCode.UnsupportedData, "binary messages only");
                return;
            }
            Dispatcher.Dispatch(session, msg.Data);
        };

        conn.Closed += (code, reason) =>
        {
            Services.Sessions.Remove(session.Id);
            Services.Bandwidth.Detach(session.Id);
            _log.Info($"disconnected {session.Id} code={code}{(reason.Length > 0 ? $" reason=\"{reason}\"" : "")}");
        };

        Heartbeat.Track(session.Id, conn);
    }

    private HttpResponse? HandleHttp(HttpRequest req) => req.Path switch
    {
        "/health" => Json(new { ok = true, clients = Services.Sessions.Count }),
        "/telemetry" => Json(Services.Bandwidth.SnapshotAll(), indented: true),
        _ => null,
    };

    private static HttpResponse Json(object value, bool indented = false) =>
        new(200, "application/json",
            JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonPayload.Options) { WriteIndented = indented }));

    public async Task StopAsync()
    {
        Heartbeat.Stop();
        await WebSockets.StopAsync();
        _log.Info("stopped");
    }
}
