using System;
using System.Collections.Generic;
using System.Linq;

namespace GarticClone.Server.Modules.Session;

public enum GamePhase
{
    Lobby,
    Prompting,
    Drawing,
    Guessing,
    Reveal
}

public sealed class PlayerState
{
    public required string PlayerId { get; init; }
    public required string SessionId { get; set; }
    public required string Name { get; set; }
    public bool IsConnected { get; set; } = true;
    public DateTimeOffset? DisconnectedAt { get; set; }
    public bool HasSubmitted { get; set; }
}

public sealed class Room
{
    public object SyncRoot { get; } = new();

    public string RoomId { get; }
    public string HostPlayerId { get; set; }
    public int MaxPlayers { get; }
    public int RoundDurationSec { get; set; }

    public GamePhase Phase { get; set; } = GamePhase.Lobby;
    public int RoundIndex { get; set; }

    public Dictionary<string, PlayerState> Players { get; } = new(); // PlayerId -> PlayerState
    public Dictionary<string, string> CurrentRoundSubmissions { get; } = new(); // PlayerId -> Content
    public DeltaRingBuffer DeltaJournal { get; } = new(capacity: 500);

    public Room(string roomId, string hostPlayerId, int maxPlayers, int roundDurationSec)
    {
        RoomId = roomId;
        HostPlayerId = hostPlayerId;
        MaxPlayers = Math.Clamp(maxPlayers, 2, 16);
        RoundDurationSec = Math.Clamp(roundDurationSec, 10, 180);
    }

    public bool HasActiveSession(string sessionId)
    {
        lock (SyncRoot)
            return Players.Values.Any(p => p.IsConnected && p.SessionId == sessionId);
    }

    public IReadOnlyList<PlayerDto> ToPlayerDtosUnsafe() =>
        Players.Values
            .Select(p => new PlayerDto(p.PlayerId, p.Name, p.PlayerId == HostPlayerId, p.IsConnected, p.HasSubmitted))
            .ToList();

    /// <summary>
    /// Tur geçiş kuralı: Lobby -> Prompting -> Drawing -> Guessing -> Reveal -> Lobby
    /// </summary>
    public GamePhase AdvancePhaseUnsafe()
    {
        Phase = Phase switch
        {
            GamePhase.Lobby => GamePhase.Prompting,
            GamePhase.Prompting => GamePhase.Drawing,
            GamePhase.Drawing => GamePhase.Guessing,
            GamePhase.Guessing => GamePhase.Reveal,
            GamePhase.Reveal => GamePhase.Lobby,
            _ => GamePhase.Lobby
        };

        RoundIndex = Phase == GamePhase.Lobby ? 0 : RoundIndex + 1;
        CurrentRoundSubmissions.Clear();
        foreach (var p in Players.Values) p.HasSubmitted = false;

        if (Phase == GamePhase.Drawing)
            DeltaJournal.Clear();

        return Phase;
    }

    /// <summary>
    /// Host odadan çıkarsa veya atılırsa host yetkisini bağlı ilk oyuncuya devreder.
    /// </summary>
    public void ReassignHostIfNeededUnsafe()
    {
        if (Players.TryGetValue(HostPlayerId, out var currentHost) && currentHost.IsConnected)
            return;

        var nextHost = Players.Values.FirstOrDefault(p => p.IsConnected);
        if (nextHost is not null)
            HostPlayerId = nextHost.PlayerId;
    }
}