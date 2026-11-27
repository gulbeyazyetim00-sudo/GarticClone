using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace GarticClone.Server.Modules.Session;

public sealed class RoomManager
{
    private readonly ConcurrentDictionary<string, Room> _rooms = new();
    private readonly ConcurrentDictionary<string, (string RoomId, string PlayerId)> _sessionIndex = new();

    public TimeSpan DisconnectGracePeriod { get; set; } = TimeSpan.FromSeconds(30);

    public (Room Room, PlayerState Player) CreateRoom(string sessionId, string playerName, int maxPlayers, int roundDurationSec)
    {
        string roomId = GenerateRoomCode();
        string playerId = Guid.NewGuid().ToString("N")[..8];

        var player = new PlayerState
        {
            PlayerId = playerId,
            SessionId = sessionId,
            Name = SanitizeName(playerName)
        };

        var room = new Room(roomId, playerId, maxPlayers, roundDurationSec);
        lock (room.SyncRoot)
        {
            room.Players[playerId] = player;
        }

        _rooms[roomId] = room;
        _sessionIndex[sessionId] = (roomId, playerId);
        return (room, player);
    }

    public (Room? Room, PlayerState? Player, string? Error) JoinRoom(string sessionId, string roomId, string playerName)
    {
        string normalizedId = roomId.Trim().ToUpperInvariant();
        if (!_rooms.TryGetValue(normalizedId, out var room))
            return (null, null, "Room not found.");

        lock (room.SyncRoot)
        {
            if (room.Players.Count >= room.MaxPlayers)
                return (null, null, "Room is full.");

            string playerId = Guid.NewGuid().ToString("N")[..8];
            var player = new PlayerState
            {
                PlayerId = playerId,
                SessionId = sessionId,
                Name = SanitizeName(playerName)
            };

            room.Players[playerId] = player;
            _sessionIndex[sessionId] = (normalizedId, playerId);
            return (room, player, null);
        }
    }

    public (Room? Room, PlayerState? Player, bool RoomClosed) LeaveRoom(string sessionId)
    {
        if (!_sessionIndex.TryRemove(sessionId, out var binding))
            return (null, null, false);

        if (!_rooms.TryGetValue(binding.RoomId, out var room))
            return (null, null, false);

        lock (room.SyncRoot)
        {
            room.Players.Remove(binding.PlayerId, out var removed);
            if (room.Players.Count == 0)
            {
                _rooms.TryRemove(binding.RoomId, out _);
                return (room, removed, true);
            }

            room.ReassignHostIfNeededUnsafe();
            return (room, removed, false);
        }
    }

    /// <summary>
    /// Beklenmedik kopmalarda (Wi-Fi kesilmesi vb.) oyuncuyu hemen silmez,
    /// Grace Period boyunca "Disconnected" işaretler ki geri bağlanabilsin.
    /// </summary>
    public (Room? Room, PlayerState? Player) HandleDisconnect(string sessionId)
    {
        if (!_sessionIndex.TryRemove(sessionId, out var binding))
            return (null, null);

        if (!_rooms.TryGetValue(binding.RoomId, out var room))
            return (null, null);

        lock (room.SyncRoot)
        {
            if (!room.Players.TryGetValue(binding.PlayerId, out var player))
                return (null, null);

            player.IsConnected = false;
            player.DisconnectedAt = DateTimeOffset.UtcNow;
            room.ReassignHostIfNeededUnsafe();
            return (room, player);
        }
    }

    /// <summary>
    /// Kopan oyuncunun yeni WebSocket oturumuyla eski kimliğine kavuşmasını sağlar.
    /// </summary>
    public (Room? Room, PlayerState? Player, CatchUpResult? CatchUp, string? Error) ReconnectAndSync(
        string newSessionId, string roomId, string playerId, uint lastAckSeq)
    {
        string normalizedId = roomId.Trim().ToUpperInvariant();
        if (!_rooms.TryGetValue(normalizedId, out var room))
            return (null, null, null, "Room not found.");

        lock (room.SyncRoot)
        {
            if (!room.Players.TryGetValue(playerId, out var player))
                return (null, null, null, "Player not found in room.");

            // Eski oturum index'te kaldıysa temizle ve yeni oturumu bağla
            _sessionIndex.TryRemove(player.SessionId, out _);
            player.SessionId = newSessionId;
            player.IsConnected = true;
            player.DisconnectedAt = null;
            _sessionIndex[newSessionId] = (normalizedId, playerId);

            var catchUp = room.DeltaJournal.GetCatchUp(lastAckSeq);
            return (room, player, catchUp, null);
        }
    }

    public (Room? Room, GamePhase? NewPhase, string? Error) StartGame(string sessionId)
    {
        if (!TryGetSessionRoom(sessionId, out var room, out var playerId))
            return (null, null, "Not in a room.");

        lock (room.SyncRoot)
        {
            if (room.HostPlayerId != playerId)
                return (null, null, "Only the host can start the game.");
            if (room.Phase != GamePhase.Lobby)
                return (null, null, "Game already started.");

            var nextPhase = room.AdvancePhaseUnsafe();
            return (room, nextPhase, null);
        }
    }

    public (Room? Room, bool PhaseAdvanced, GamePhase CurrentPhase, string? Error) SubmitPhase(string sessionId, string content)
    {
        if (!TryGetSessionRoom(sessionId, out var room, out var playerId))
            return (null, false, GamePhase.Lobby, "Not in a room.");

        lock (room.SyncRoot)
        {
            if (!room.Players.TryGetValue(playerId, out var player))
                return (null, false, room.Phase, "Player not found.");

            room.CurrentRoundSubmissions[playerId] = content;
            player.HasSubmitted = true;

            // Bağlı olan tüm oyuncular gönderimini tamamladıysa otomatik sonraki tura geç
            bool allDone = room.Players.Values.Where(p => p.IsConnected).All(p => p.HasSubmitted);
            if (allDone)
            {
                var next = room.AdvancePhaseUnsafe();
                return (room, true, next, null);
            }

            return (room, false, room.Phase, null);
        }
    }

    /// <summary>
    /// Grace Period süresini aşan kopuk oyuncuları odadan temizler (Peer Eviction).
    /// </summary>
    public List<(Room Room, PlayerState EvictedPlayer)> EvictExpiredPlayers()
    {
        var evicted = new List<(Room, PlayerState)>();
        var now = DateTimeOffset.UtcNow;

        foreach (var kvp in _rooms)
        {
            var room = kvp.Value;
            lock (room.SyncRoot)
            {
                var expiredIds = room.Players.Values
                    .Where(p => !p.IsConnected && p.DisconnectedAt.HasValue && (now - p.DisconnectedAt.Value) >= DisconnectGracePeriod)
                    .Select(p => p.PlayerId)
                    .ToList();

                foreach (var pid in expiredIds)
                {
                    if (room.Players.Remove(pid, out var removed))
                        evicted.Add((room, removed));
                }

                if (room.Players.Count == 0)
                    _rooms.TryRemove(kvp.Key, out _);
                else if (expiredIds.Count > 0)
                    room.ReassignHostIfNeededUnsafe();
            }
        }

        return evicted;
    }

    /// <summary>
    /// Çizim modülü (Mustafa/Gülbeyaz) bir delta paketi aldığında bu metodu çağırıp günlüğe kaydeder.
    /// </summary>
    public uint? RecordDrawingDelta(string sessionId, byte[] deltaPayload)
    {
        if (!TryGetSessionRoom(sessionId, out var room, out _))
            return null;

        return room.DeltaJournal.Append(deltaPayload);
    }

    public bool TryGetSessionRoom(string sessionId, out Room room, out string playerId)
    {
        room = null!;
        playerId = string.Empty;
        if (!_sessionIndex.TryGetValue(sessionId, out var binding))
            return false;

        playerId = binding.PlayerId;
        return _rooms.TryGetValue(binding.RoomId, out room!);
    }

    private static string GenerateRoomCode()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return string.Create(6, chars, (span, alphabet) =>
        {
            for (int i = 0; i < span.Length; i++)
                span[i] = alphabet[Random.Shared.Next(alphabet.Length)];
        });
    }

    private static string SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Guest";
        string trimmed = name.Trim();
        return trimmed.Length > 24 ? trimmed[..24] : trimmed;
    }
}