using System.Collections.Generic;

namespace GarticClone.Server.Modules.Session;

// --- İstemciden Gelen İstekler (Requests) ---
public sealed record RoomCreateRequest(string? PlayerName, int MaxPlayers = 8, int RoundDurationSec = 60);
public sealed record RoomJoinRequest(string RoomId, string? PlayerName);
public sealed record PhaseSubmitRequest(string Content); // Metin, tahmin veya çizim özeti
public sealed record SyncRequestPayload(string RoomId, string PlayerId, uint LastAckSeq);

// --- Sunucudan Giden Yanıtlar (Responses / Broadcasts) ---
public sealed record PlayerDto(
    string PlayerId,
    string Name,
    bool IsHost,
    bool IsConnected,
    bool HasSubmitted);

public sealed record RoomStatePayload(
    string RoomId,
    string YourPlayerId,
    string Phase,
    int RoundIndex,
    int RoundDurationSec,
    uint CurrentDeltaSeq,
    IReadOnlyList<PlayerDto> Players);

public sealed record PhaseChangedPayload(
    string RoomId,
    string Phase,
    int RoundIndex,
    int RoundDurationSec);

public sealed record PlayerStatusPayload(
    string RoomId,
    string PlayerId,
    string PlayerName,
    string Status); // "JOINED", "LEFT", "DISCONNECTED", "RECONNECTED", "EVICTED"

public sealed record DeltaItemDto(uint Seq, string PayloadBase64);

public sealed record SyncSnapshotPayload(
    string RoomId,
    string Phase,
    int RoundIndex,
    bool FullSnapshotIncluded,
    string? KeyframeSnapshotBase64,
    uint SnapshotSeq,
    uint LatestDeltaSeq,
    IReadOnlyList<DeltaItemDto> MissedDeltas,
    IReadOnlyList<PlayerDto> Players);