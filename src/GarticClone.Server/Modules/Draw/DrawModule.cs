using System.Buffers.Binary;
using GarticClone.Server.Modules.Session;
using GarticClone.Server.Protocol;
using GarticClone.Server.Protocol.Opcodes;

namespace GarticClone.Server.Modules.Draw;

/// <summary>
/// Journals every drawing message in the room's DeltaRingBuffer and relays it to the other players.
/// Live relay payload = journalSeq (u32 BE) + the client's original payload.
/// Journal entry     = opcode (1B) + flags (1B) + the client's original payload.
/// </summary>
public sealed class DrawModule : IModule
{
    private readonly RoomManager _rooms;

    public string Name => "draw";
    public IReadOnlyDictionary<byte, MessageHandler> Handlers { get; }

    public DrawModule(RoomManager rooms)
    {
        _rooms = rooms;
        Handlers = new Dictionary<byte, MessageHandler>
        {
            [DrawOp.StrokeStart] = OnDraw,
            [DrawOp.StrokeDeltas] = OnDraw,
            [DrawOp.StrokeEnd] = OnDraw,
            [DrawOp.CanvasClear] = OnDraw,
        };
    }

    private void OnDraw(HandlerContext ctx)
    {
        var msg = ctx.Message;
        var payload = msg.Payload.Span;

        if (!IsValid(msg.Opcode, payload))
        {
            SendError(ctx, "INVALID_DRAW", $"Malformed {OpcodeRegistry.NameOf(msg.Opcode)} payload.");
            return;
        }
        if (!_rooms.TryGetSessionRoom(ctx.Session.Id, out var room, out _))
        {
            SendError(ctx, "NOT_IN_ROOM", "Join a room before drawing.");
            return;
        }

        var entry = new byte[2 + payload.Length];
        entry[0] = msg.Opcode;
        entry[1] = (byte)msg.Flags;
        payload.CopyTo(entry.AsSpan(2));

        var wire = new byte[4 + payload.Length];
        payload.CopyTo(wire.AsSpan(4));

        uint seq;
        // One lock around append + broadcast keeps relay order identical to journal order.
        lock (room.SyncRoot)
        {
            if (msg.Opcode == DrawOp.CanvasClear) room.DeltaJournal.Clear();
            seq = room.DeltaJournal.Append(entry);
            BinaryPrimitives.WriteUInt32BigEndian(wire, seq);

            // TODO(compression): pass msg.Flags through once Broadcast accepts flags.
            ctx.Services.Sessions.Broadcast(
                msg.Opcode,
                wire,
                filter: s => room.HasActiveSession(s.Id),
                exceptId: ctx.Session.Id);
        }

        // The drawer doesn't get its own strokes back; an ack at stroke end lets it advance LastAckSeq.
        if (msg.Opcode is DrawOp.StrokeEnd or DrawOp.CanvasClear)
        {
            var ack = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(ack, seq);
            ctx.Reply(DrawOp.DrawAck, ack);
        }
    }

    private static bool IsValid(byte opcode, ReadOnlySpan<byte> p) => opcode switch
    {
        DrawOp.StrokeStart => p.Length == 8,
        DrawOp.StrokeDeltas => p.Length >= 7 && p.Length == 7 + 2 * p[6],
        DrawOp.StrokeEnd => p.Length == 2,
        DrawOp.CanvasClear => p.Length == 0,
        _ => false,
    };

    private static void SendError(HandlerContext ctx, string code, string detail) =>
        ctx.Reply(SystemOp.Error, JsonPayload.Encode(new ErrorPayload(code, detail, ctx.Message.Seq)));
}
