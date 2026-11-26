using System.Net;

namespace GarticClone.Server;

public sealed record ServerConfig
{
    public IPAddress Host { get; init; } = IPAddress.Any;
    public int Port { get; init; } = 8080;
    public string WsPath { get; init; } = "/ws";
    public IReadOnlyCollection<string>? AllowedOrigins { get; init; }

    public int MaxMessageSize { get; init; } = 1 << 20; 

    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);
    public int HeartbeatMaxMissed { get; init; } = 3;

    public double TelemetryWindowMs { get; init; } = 5000; 
    public double TelemetryBucketMs { get; init; } = 250;

    public static ServerConfig FromEnvironment()
    {
        static string? Env(string name) => Environment.GetEnvironmentVariable(name);
        static int Int(string name, int fallback) => int.TryParse(Env(name), out var v) ? v : fallback;

        var c = new ServerConfig();
        return c with
        {
            Host = IPAddress.TryParse(Env("HOST"), out var host) ? host : c.Host,
            Port = Int("PORT", c.Port),
            WsPath = Env("WS_PATH") ?? c.WsPath,
            AllowedOrigins = Env("ALLOWED_ORIGINS")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            MaxMessageSize = Int("MAX_MESSAGE_SIZE", c.MaxMessageSize),
            HeartbeatInterval = TimeSpan.FromMilliseconds(Int("HEARTBEAT_INTERVAL_MS", (int)c.HeartbeatInterval.TotalMilliseconds)),
            HeartbeatMaxMissed = Int("HEARTBEAT_MAX_MISSED", c.HeartbeatMaxMissed),
        };
    }
}
