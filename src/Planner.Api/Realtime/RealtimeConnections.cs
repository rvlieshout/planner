using System.Collections.Concurrent;

namespace Planner.Api.Realtime;

/// <summary>Which groups each live connection on this server was put into, and on whose behalf.
///
/// SignalR can add a connection to a group and take it out again, but it cannot say which groups a
/// connection is in. Without that, access that is taken away cannot be taken away from the socket:
/// a user removed from a team would keep receiving its traffic until they happened to reconnect.
///
/// In memory, because a connection lives on exactly one server and this API runs as one instance. A
/// scaled-out deployment with a backplane would need this in shared storage instead.</summary>
public sealed class RealtimeConnections
{
    private readonly ConcurrentDictionary<string, RealtimeConnection> _connections = new();

    public RealtimeConnection Register(string connectionId, Guid userId) =>
        _connections[connectionId] = new RealtimeConnection(connectionId, userId);

    public void Unregister(string connectionId) => _connections.TryRemove(connectionId, out _);

    public RealtimeConnection? Find(string connectionId) =>
        _connections.TryGetValue(connectionId, out var connection) ? connection : null;

    public IReadOnlyList<RealtimeConnection> ForUser(Guid userId) =>
        _connections.Values.Where(c => c.UserId == userId).ToList();
}

public sealed class RealtimeConnection(string connectionId, Guid userId)
{
    public string ConnectionId { get; } = connectionId;

    public Guid UserId { get; } = userId;

    /// <summary>Held while the groups below are being changed. A hub call from the connection itself
    /// and an endpoint changing its user's access can arrive at the same moment, and each reads the
    /// sets before it writes the groups.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>Teams whose group this connection is in.</summary>
    public HashSet<Guid> Teams { get; set; } = [];

    /// <summary>Issue groups joined on demand, with the team each belongs to, so losing a team also
    /// drops its issues.</summary>
    public Dictionary<Guid, Guid> Issues { get; } = [];
}
