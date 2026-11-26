using GarticClone.Server.Protocol.Opcodes;
using GarticClone.Server.Telemetry;
using GarticClone.Server.Transport;
using GarticClone.Server.Utils;

namespace GarticClone.Server.Protocol;

public sealed class ServerServices
{
    public required SessionRegistry Sessions { get; init; }
    public required BandwidthMonitor Bandwidth { get; init; }
    public Heartbeat? Heartbeat { get; init; }
}

public sealed class HandlerContext
{
    public required ClientSession Session { get; init; }
    public required AppMessage Message { get; init; }
    public required ServerServices Services { get; init; }

    public int Reply(byte opcode, ReadOnlySpan<byte> payload = default, MessageFlags flags = MessageFlags.None) =>
        Session.Send(opcode, payload, flags);
}

public delegate void MessageHandler(HandlerContext ctx);


public interface IModule
{
    string Name { get; }
    IReadOnlyDictionary<byte, MessageHandler> Handlers { get; }
}

public static class ErrorCodes
{
    public const string DecodeFailed = "DECODE_FAILED";
    public const string UnknownOpcode = "UNKNOWN_OPCODE";
    public const string HandlerFailed = "HANDLER_FAILED";
}

public sealed record ErrorPayload(string Code, string Message, uint? RefSeq);


public sealed class Dispatcher(ServerServices services)
{
    private readonly Dictionary<byte, (string Module, MessageHandler Handler)> _handlers = [];
    private readonly Logger _log = Log.For("dispatcher");

    public void Register(byte opcode, MessageHandler handler, string moduleName = "anonymous")
    {
        if (_handlers.TryGetValue(opcode, out var existing))
            throw new InvalidOperationException(
                $"Handler for {OpcodeRegistry.NameOf(opcode)} already registered by module \"{existing.Module}\"");
        _handlers[opcode] = (moduleName, handler);
    }

    public void RegisterModule(IModule module)
    {
        foreach (var (opcode, handler) in module.Handlers)
            Register(opcode, handler, module.Name);
        _log.Debug($"module \"{module.Name}\" registered ({module.Handlers.Count} handlers)");
    }

    public void Dispatch(ClientSession session, byte[] bytes)
    {
        AppMessage message;
        try
        {
            message = MessageCodec.Decode(bytes);
        }
        catch (ProtocolDecodeException ex)
        {
            Interlocked.Increment(ref session.Stats.DecodeErrors);
            SendError(session, ErrorCodes.DecodeFailed, ex.Message, null);
            return;
        }

        session.TrackInboundSeq(message.Seq);
        services.Bandwidth.RecordApp(session.Id, TrafficDirection.In, message.Opcode, message.ByteLength);

        if (!_handlers.TryGetValue(message.Opcode, out var entry))
        {
            Interlocked.Increment(ref session.Stats.UnknownOpcodes);
            SendError(session, ErrorCodes.UnknownOpcode, OpcodeRegistry.NameOf(message.Opcode), message.Seq);
            return;
        }

        try
        {
            entry.Handler(new HandlerContext { Session = session, Message = message, Services = services });
        }
        catch (Exception ex)
        {
            _log.Error($"handler {OpcodeRegistry.NameOf(message.Opcode)} ({entry.Module}) threw", ex);
            SendError(session, ErrorCodes.HandlerFailed, OpcodeRegistry.NameOf(message.Opcode), message.Seq);
        }
    }

    private static void SendError(ClientSession session, string code, string detail, uint? refSeq) =>
        session.Send(SystemOp.Error, JsonPayload.Encode(new ErrorPayload(code, detail, refSeq)));
}
