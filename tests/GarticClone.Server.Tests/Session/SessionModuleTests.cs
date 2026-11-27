using GarticClone.Server.Modules.Session;
using GarticClone.Server.Protocol;
using GarticClone.Server.Protocol.Opcodes;
using GarticClone.Server.Telemetry;
using GarticClone.Server.Tests.Framework;
using GarticClone.Server.Tests.Helpers;

namespace GarticClone.Server.Tests.Session;

public class SessionModuleTests
{
    private static ServerServices Services() => new()
    {
        Sessions = new SessionRegistry(),
        Bandwidth = new BandwidthMonitor()
    };

    [Test("room creation, joining and capacity enforcement")]
    public void RoomCreateAndJoin()
    {
        var manager = new RoomManager();
        var (room, host) = manager.CreateRoom("c1", "Esra", maxPlayers: 2, roundDurationSec: 45);

        Assert.Equal(GamePhase.Lobby, room.Phase);
        Assert.Equal(host.PlayerId, room.HostPlayerId);

        var (joinedRoom, player2, err2) = manager.JoinRoom("c2", room.RoomId, "Gulbeyaz");
        Assert.True(err2 is null);
        Assert.Equal(room.RoomId, Assert.NotNull(joinedRoom).RoomId);
        Assert.True(player2 is not null);

        // Oda 2 kişilik olduğu için 3. kişi reddedilmeli
        var (_, _, err3) = manager.JoinRoom("c3", room.RoomId, "Mustafa");
        Assert.Equal("Room is full.", err3);
    }

    [Test("phase state machine advances only when all connected players submit")]
    public void PhaseStateMachine()
    {
        var manager = new RoomManager();
        var (room, _) = manager.CreateRoom("c1", "Esra", maxPlayers: 4, roundDurationSec: 60);
        manager.JoinRoom("c2", room.RoomId, "Halil");

        // Host olmayan başlatamaz
        var (_, _, nonHostErr) = manager.StartGame("c2");
        Assert.Equal("Only the host can start the game.", nonHostErr);

        // Host başlatınca Lobby -> Prompting
        var (_, phase1, startErr) = manager.StartGame("c1");
        Assert.True(startErr is null);
        Assert.Equal(GamePhase.Prompting, phase1);

        // Sadece 1 kişi gönderince tur ilerlemez
        var (_, adv1, cur1, _) = manager.SubmitPhase("c1", "Ucan kedi");
        Assert.True(!adv1);
        Assert.Equal(GamePhase.Prompting, cur1);

        // 2. kişi de gönderince Prompting -> Drawing
        var (_, adv2, cur2, _) = manager.SubmitPhase("c2", "Uzayda cay");
        Assert.True(adv2);
        Assert.Equal(GamePhase.Drawing, cur2);
    }

    [Test("ring buffer serves partial deltas on brief drop and keyframe snapshot on overflow")]
    public void RingBufferCatchUpAndOverflow()
    {
        var buffer = new DeltaRingBuffer(capacity: 3);
        buffer.SaveKeyframe("BASE64_KEYFRAME_V1");

        uint s1 = buffer.Append([1, 2]);
        uint s2 = buffer.Append([3, 4]);
        uint s3 = buffer.Append([5, 6]);

        Assert.Equal(1u, s1);
        Assert.Equal(3u, s3);

        // Kısa kopma: İstemci 1'i almış, sadece 2 ve 3 gitmeli (Full snapshot gerekmez)
        var partial = buffer.GetCatchUp(lastAckSeq: 1);
        Assert.True(!partial.FullSnapshotIncluded);
        Assert.Equal(2, partial.MissedDeltas.Count);
        Assert.Equal(2u, partial.MissedDeltas[0].Seq);
        Assert.Equal(3u, partial.MissedDeltas[1].Seq);

        // Tampon taşması: 2 paket daha eklenince eski 1 ve 2 ezilir (tamponda 3, 4, 5 kalır)
        buffer.Append([7, 8]);
        buffer.Append([9, 10]);

        // İstemci 1'de kaldıysa artık 2 kayıp; sistem otomatik Keyframe Snapshot vermeli
        var full = buffer.GetCatchUp(lastAckSeq: 1);
        Assert.True(full.FullSnapshotIncluded);
        Assert.Equal("BASE64_KEYFRAME_V1", full.KeyframeSnapshotBase64);
        Assert.Equal(5u, full.LatestSeq);
    }

    [Test("disconnect reassigns host and eviction removes expired peers")]
    public void DisconnectAndEviction()
    {
        var manager = new RoomManager { DisconnectGracePeriod = TimeSpan.Zero };
        var (room, host) = manager.CreateRoom("c1", "Esra", maxPlayers: 4, roundDurationSec: 60);
        var (_, player2, _) = manager.JoinRoom("c2", room.RoomId, "Berke");

        // Host koptuğunda yetki player2'ye geçmeli
        var (_, disconnected) = manager.HandleDisconnect("c1");
        Assert.True(disconnected is not null);
        Assert.True(!disconnected!.IsConnected);
        Assert.Equal(player2!.PlayerId, room.HostPlayerId);

        // Grace period dolduğu için eviction listesinde host çıkmalı
        var evicted = manager.EvictExpiredPlayers();
        Assert.Equal(1, evicted.Count);
        Assert.Equal(host.PlayerId, evicted[0].EvictedPlayer.PlayerId);
    }

    [Test("end-to-end reconnect via Dispatcher delivers missed deltas in SyncSnapshot")]
    public async Task EndToEndReconnectSync()
    {
        await using var pair1 = await LoopbackConnection.CreateAsync();
        await using var pair2 = await LoopbackConnection.CreateAsync();

        var services = Services();
        var dispatcher = new Dispatcher(services);
        var module = new SessionModule();
        dispatcher.RegisterModule(module);

        var session1 = new ClientSession(pair1.Server);
        services.Sessions.Add(session1);

        // 1. Oda oluştur
        dispatcher.Dispatch(session1, MessageCodec.Encode(
            SessionOp.RoomCreate, JsonPayload.Encode(new RoomCreateRequest("Esra"))));

        var createReply = MessageCodec.Decode(await pair1.ReceiveAsync());
        Assert.Equal(SessionOp.RoomState, createReply.Opcode);
        var roomState = Assert.NotNull(JsonPayload.Decode<RoomStatePayload>(createReply.Payload.Span));

        // 2. Çizim deltalarını kaydet (Seq 1 ve Seq 2)
        module.Rooms.RecordDrawingDelta(session1.Id, [10, 20]);
        module.Rooms.RecordDrawingDelta(session1.Id, [30, 40]);

        // 3. Bağlantı koptu
        module.OnClientDisconnected(session1.Id, services.Sessions);
        services.Sessions.Remove(session1.Id);

        // 4. Yeni WebSocket oturumuyla geri bağlan ve Seq 1'den sonrasını iste
        var session2 = new ClientSession(pair2.Server);
        services.Sessions.Add(session2);

        dispatcher.Dispatch(session2, MessageCodec.Encode(
            SessionOp.SyncRequest,
            JsonPayload.Encode(new SyncRequestPayload(roomState.RoomId, roomState.YourPlayerId, LastAckSeq: 1))));

        var syncReply = MessageCodec.Decode(await pair2.ReceiveAsync());
        Assert.Equal(SessionOp.SyncSnapshot, syncReply.Opcode);

        var snapshot = Assert.NotNull(JsonPayload.Decode<SyncSnapshotPayload>(syncReply.Payload.Span));
        Assert.Equal(1, snapshot.MissedDeltas.Count);
        Assert.Equal(2u, snapshot.MissedDeltas[0].Seq);
        Assert.Equal(2u, snapshot.LatestDeltaSeq);
    }
}