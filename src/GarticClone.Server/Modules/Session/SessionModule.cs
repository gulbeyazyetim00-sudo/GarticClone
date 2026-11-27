using System.Collections.Generic;
using GarticClone.Server.Protocol;
using GarticClone.Server.Protocol.Opcodes;

namespace GarticClone.Server.Modules.Session;

public sealed class SessionModule : IModule
{
    public RoomManager Rooms { get; }

    public string Name => "session";

    public IReadOnlyDictionary<byte, MessageHandler> Handlers { get; }

    public SessionModule(RoomManager? rooms = null)
    {
        Rooms = rooms ?? new RoomManager();
        Handlers = new Dictionary<byte, MessageHandler>
        {
            [SessionOp.RoomCreate] = OnRoomCreate,
            [SessionOp.RoomJoin] = OnRoomJoin,
            [SessionOp.RoomLeave] = OnRoomLeave,
            [SessionOp.GameStart] = OnGameStart,
            [SessionOp.PhaseSubmit] = OnPhaseSubmit,
            [SessionOp.SyncRequest] = OnSyncRequest,
        };
    }

    private void OnRoomCreate(HandlerContext ctx)
    {
        var req = ctx.Message.Payload.Length > 0
            ? JsonPayload.Decode<RoomCreateRequest>(ctx.Message.Payload.Span)
            : new RoomCreateRequest(null);

        string name = ResolvePlayerName(ctx, req?.PlayerName);
        var (room, player) = Rooms.CreateRoom(
            ctx.Session.Id,
            name,
            req?.MaxPlayers ?? 8,
            req?.RoundDurationSec ?? 60);

        SendRoomState(ctx, room, player.PlayerId);
    }

    private void OnRoomJoin(HandlerContext ctx)
    {
        var req = JsonPayload.Decode<RoomJoinRequest>(ctx.Message.Payload.Span);
        if (req is null || string.IsNullOrWhiteSpace(req.RoomId))
        {
            SendError(ctx, "INVALID_REQUEST", "RoomId is required.");
            return;
        }

        string name = ResolvePlayerName(ctx, req.PlayerName);
        var (room, player, error) = Rooms.JoinRoom(ctx.Session.Id, req.RoomId, name);
        if (error is not null || room is null || player is null)
        {
            SendError(ctx, "JOIN_FAILED", error ?? "Unable to join room.");
            return;
        }

        SendRoomState(ctx, room, player.PlayerId);
        BroadcastPlayerStatus(ctx, room, player, "JOINED", exceptSessionId: ctx.Session.Id);
    }

    private void OnRoomLeave(HandlerContext ctx)
    {
        var (room, player, roomClosed) = Rooms.LeaveRoom(ctx.Session.Id);
        if (room is null || player is null || roomClosed) return;

        BroadcastPlayerStatus(ctx, room, player, "LEFT");
    }

    private void OnGameStart(HandlerContext ctx)
    {
        var (room, newPhase, error) = Rooms.StartGame(ctx.Session.Id);
        if (error is not null || room is null || newPhase is null)
        {
            SendError(ctx, "START_FAILED", error ?? "Cannot start game.");
            return;
        }

        BroadcastPhaseChanged(ctx, room, newPhase.Value);
    }

    private void OnPhaseSubmit(HandlerContext ctx)
    {
        var req = JsonPayload.Decode<PhaseSubmitRequest>(ctx.Message.Payload.Span);
        var (room, advanced, currentPhase, error) = Rooms.SubmitPhase(ctx.Session.Id, req?.Content ?? string.Empty);

        if (error is not null || room is null)
        {
            SendError(ctx, "SUBMIT_FAILED", error ?? "Submission failed.");
            return;
        }

        if (advanced)
            BroadcastPhaseChanged(ctx, room, currentPhase);
    }

    private void OnSyncRequest(HandlerContext ctx)
    {
        var req = JsonPayload.Decode<SyncRequestPayload>(ctx.Message.Payload.Span);
        if (req is null || string.IsNullOrWhiteSpace(req.RoomId) || string.IsNullOrWhiteSpace(req.PlayerId))
        {
            SendError(ctx, "INVALID_SYNC", "RoomId and PlayerId are required.");
            return;
        }

        var (room, player, catchUp, error) = Rooms.ReconnectAndSync(
            ctx.Session.Id, req.RoomId, req.PlayerId, req.LastAckSeq);

        if (error is not null || room is null || player is null || catchUp is null)
        {
            SendError(ctx, "SYNC_FAILED", error ?? "Reconnection failed.");
            return;
        }

        SyncSnapshotPayload snapshot;
        lock (room.SyncRoot)
        {
            snapshot = new SyncSnapshotPayload(
                RoomId: room.RoomId,
                Phase: room.Phase.ToString(),
                RoundIndex: room.RoundIndex,
                FullSnapshotIncluded: catchUp.FullSnapshotIncluded,
                KeyframeSnapshotBase64: catchUp.KeyframeSnapshotBase64,
                SnapshotSeq: catchUp.SnapshotSeq,
                LatestDeltaSeq: catchUp.LatestSeq,
                MissedDeltas: catchUp.MissedDeltas,
                Players: room.ToPlayerDtosUnsafe());
        }

        ctx.Reply(SessionOp.SyncSnapshot, JsonPayload.Encode(snapshot));
        BroadcastPlayerStatus(ctx, room, player, "RECONNECTED", exceptSessionId: ctx.Session.Id);
    }

    private static string ResolvePlayerName(HandlerContext ctx, string? requestedName)
    {
        if (!string.IsNullOrWhiteSpace(requestedName)) return requestedName;
        if (ctx.Session.Data.TryGetValue("system.clientName", out var cached) && cached is string s)
            return s;
        return $"Player-{ctx.Session.Id}";
    }

    private static void SendRoomState(HandlerContext ctx, Room room, string playerId)
    {
        RoomStatePayload payload;
        lock (room.SyncRoot)
        {
            payload = new RoomStatePayload(
                room.RoomId,
                playerId,
                room.Phase.ToString(),
                room.RoundIndex,
                room.RoundDurationSec,
                room.DeltaJournal.CurrentSeq,
                room.ToPlayerDtosUnsafe());
        }
        ctx.Reply(SessionOp.RoomState, JsonPayload.Encode(payload));
    }

    private static void BroadcastPhaseChanged(HandlerContext ctx, Room room, GamePhase phase)
    {
        byte[] encoded;
        lock (room.SyncRoot)
        {
            encoded = JsonPayload.Encode(new PhaseChangedPayload(
                room.RoomId, phase.ToString(), room.RoundIndex, room.RoundDurationSec));
        }

        ctx.Services.Sessions.Broadcast(
            SessionOp.PhaseChanged,
            encoded,
            filter: s => room.HasActiveSession(s.Id));
    }

    private static void BroadcastPlayerStatus(
        HandlerContext ctx, Room room, PlayerState player, string status, string? exceptSessionId = null)
    {
        var encoded = JsonPayload.Encode(new PlayerStatusPayload(
            room.RoomId, player.PlayerId, player.Name, status));

        ctx.Services.Sessions.Broadcast(
            SessionOp.PlayerStatus,
            encoded,
            filter: s => room.HasActiveSession(s.Id),
            exceptId: exceptSessionId);
    }

    private static void SendError(HandlerContext ctx, string code, string detail) =>
        ctx.Reply(SystemOp.Error, JsonPayload.Encode(new ErrorPayload(code, detail, ctx.Message.Seq)));

    /// <summary>
    /// WebSocket bağlantısı aniden koptuğunda ServerApp tarafından çağrılır.
    /// Oyuncuyu silmez, "DISCONNECTED" işaretleyip odadakilere bildirir.
    /// </summary>
    public void OnClientDisconnected(string sessionId, SessionRegistry sessions)
    {
        var (room, player) = Rooms.HandleDisconnect(sessionId);
        if (room is null || player is null) return;

        BroadcastPlayerStatus(sessions, room, player, "DISCONNECTED");
    }

    /// <summary>
    /// Periyodik olarak çağrılır; Grace Period (30 sn) süresini aşan kopuk oyuncuları
    /// odadan atar ve kalan oyunculara "EVICTED" bildirimi yollar.
    /// </summary>
    public void EvictExpired(SessionRegistry sessions)
    {
        var evicted = Rooms.EvictExpiredPlayers();
        foreach (var (room, player) in evicted)
        {
            BroadcastPlayerStatus(sessions, room, player, "EVICTED");
        }
    }

    private static void BroadcastPlayerStatus(
        SessionRegistry sessions, Room room, PlayerState player, string status, string? exceptSessionId = null)
    {
        var encoded = JsonPayload.Encode(new PlayerStatusPayload(
            room.RoomId, player.PlayerId, player.Name, status));

        sessions.Broadcast(
            SessionOp.PlayerStatus,
            encoded,
            filter: s => room.HasActiveSession(s.Id),
            exceptId: exceptSessionId);
    }
}