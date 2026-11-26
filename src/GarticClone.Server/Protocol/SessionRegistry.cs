using System.Collections.Concurrent;

namespace GarticClone.Server.Protocol;

public sealed class SessionRegistry
{
    private readonly ConcurrentDictionary<string, ClientSession> _sessions = new();

    public int Count => _sessions.Count;
    public IEnumerable<ClientSession> All => _sessions.Values;

    public void Add(ClientSession session) => _sessions[session.Id] = session;
    public void Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);
    public ClientSession? Get(string sessionId) => _sessions.GetValueOrDefault(sessionId);

    public int Broadcast(byte opcode, ReadOnlySpan<byte> payload, MessageFlags flags = MessageFlags.None,
        Func<ClientSession, bool>? filter = null, string? exceptId = null)
    {
        int sent = 0;
        foreach (var s in _sessions.Values)
        {
            if (s.Id == exceptId || (filter is not null && !filter(s))) continue;
            if (s.Send(opcode, payload, flags) >= 0) sent++;
        }
        return sent;
    }
}
