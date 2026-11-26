using GarticClone.Server.Protocol;
using GarticClone.Server.Protocol.Opcodes;

namespace GarticClone.Server.Modules.Core;

public sealed record HelloPayload(string? ClientName);

public sealed record WelcomePayload(string ClientId, int ProtocolVersion, long ServerTime, double? HeartbeatIntervalMs);


public sealed class SystemModule : IModule
{
    public string Name => "system";

    public IReadOnlyDictionary<byte, MessageHandler> Handlers { get; } = new Dictionary<byte, MessageHandler>
    {
        [SystemOp.Hello] = OnHello,
        [SystemOp.PingApp] = OnPingApp,
        [SystemOp.TelemetryRequest] = OnTelemetryRequest,
    };

    private static void OnHello(HandlerContext ctx)
    {
        var hello = ctx.Message.Payload.Length > 0
            ? JsonPayload.Decode<HelloPayload>(ctx.Message.Payload.Span)
            : null;

        if (hello?.ClientName is { } name)
            ctx.Session.Data["system.clientName"] = name.Length > 32 ? name[..32] : name;

        ctx.Reply(SystemOp.Welcome, JsonPayload.Encode(new WelcomePayload(
            ctx.Session.Id,
            ProtocolInfo.Version,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ctx.Services.Heartbeat?.Interval.TotalMilliseconds)));
    }

   
    private static void OnPingApp(HandlerContext ctx) =>
        ctx.Reply(SystemOp.PongApp, ctx.Message.Payload.Span);

    private static void OnTelemetryRequest(HandlerContext ctx) =>
        ctx.Reply(SystemOp.TelemetryReport, JsonPayload.Encode(ctx.Services.Bandwidth.Snapshot(ctx.Session.Id)));
}
