using System.Security.Cryptography;
using System.Text;

namespace Arrowgene.DJMaxOnline.Server.Korea400;

/// <summary>
/// Issues short-lived launcher tickets for the game's 27-byte field and turns a
/// consumed ticket into a connection-bound session that can cross server sockets.
/// </summary>
public sealed class LoginTicketService
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DefaultReconnectGrace = TimeSpan.FromSeconds(30);
    public const int TokenLength = 22;
    private const int MaxOverlappingConnections = 2;

    private readonly object _lock = new();
    private readonly Dictionary<string, TicketEntry> _tickets = [];
    private readonly Dictionary<string, SessionEntry> _sessions = [];
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _lifetime;
    private readonly TimeSpan _reconnectGrace;

    public LoginTicketService(
        TimeSpan? lifetime = null,
        TimeProvider? timeProvider = null,
        TimeSpan? reconnectGrace = null)
    {
        _lifetime = lifetime ?? DefaultLifetime;
        if (_lifetime <= TimeSpan.Zero || _lifetime > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(lifetime), "Ticket lifetime must be between zero and five minutes.");
        }
        _reconnectGrace = reconnectGrace ?? DefaultReconnectGrace;
        if (_reconnectGrace <= TimeSpan.Zero || _reconnectGrace > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(reconnectGrace),
                "Reconnect grace must be between zero and five minutes.");
        }
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Issue(uint userId)
    {
        if (userId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }

        byte[] random = RandomNumberGenerator.GetBytes(16);
        string token = Convert.ToBase64String(random)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        CryptographicOperations.ZeroMemory(random);
        if (token.Length != TokenLength)
        {
            throw new CryptographicException("Unexpected login-ticket encoding length.");
        }

        string digest = Digest(token);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            RemoveExpired(now);
            // Launcher retries for the same account replace that account's stale
            // pending ticket. Different accounts are intentionally left separate so a
            // ticketless connection cannot be assigned to an arbitrary player.
            foreach (string staleTicket in _tickets
                         .Where(pair => pair.Value.UserId == userId)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _tickets.Remove(staleTicket);
            }
            _tickets.Add(digest, new TicketEntry(userId, now + _lifetime));
        }
        return token;
    }

    public bool TryConsume(string token, out uint userId)
    {
        userId = 0;
        if (!IsWellFormed(token))
        {
            return false;
        }

        string digest = Digest(token);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            RemoveExpired(now);
            if (!_tickets.Remove(digest, out TicketEntry? entry) || entry.ExpiresAt <= now)
            {
                return false;
            }
            userId = entry.UserId;
            return true;
        }
    }

    /// <summary>
    /// Consumes a newly issued ticket or resumes the authenticated session that
    /// ticket created. Two sockets may overlap so the client can connect to the
    /// selected channel before its server-list socket has completely closed.
    /// </summary>
    public bool TryOpenSession(
        string token,
        out uint userId,
        out LoginSessionLease? lease)
    {
        userId = 0;
        lease = null;
        if (!IsWellFormed(token))
        {
            return false;
        }

        string digest = Digest(token);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            RemoveExpired(now);

            SessionEntry session;
            bool isReconnect;
            if (_tickets.Remove(digest, out TicketEntry? ticket) &&
                ticket.ExpiresAt > now)
            {
                session = new SessionEntry(
                    ticket.UserId, now + _reconnectGrace, now);
                _sessions.Add(digest, session);
                isReconnect = false;
            }
            else
            {
                if (!_sessions.TryGetValue(digest, out session!) ||
                    (session.ConnectionIds.Count == 0 &&
                     session.ReconnectUntil <= now) ||
                    session.ConnectionIds.Count >= MaxOverlappingConnections)
                {
                    return false;
                }

                // A live authenticated socket permits exactly one overlapping handoff
                // regardless of how long the player sat at server select. Once no socket
                // remains, only the reconnect grace can admit the replacement.
                session.ReconnectUntil = DateTimeOffset.MinValue;
                isReconnect = true;
            }

            Guid connectionId = Guid.NewGuid();
            session.ConnectionIds.Add(connectionId);
            session.LastAttachedAt = now;
            userId = session.UserId;
            lease = new LoginSessionLease(
                digest, connectionId, session.UserId, isReconnect);
            return true;
        }
    }

    /// <summary>
    /// Opens the one unambiguous launcher context for a client that cannot present a
    /// ticket. Once the server-list socket has consumed the ticket, the selected-channel
    /// socket can join the sole live launcher session without being given a placeholder
    /// identity.
    /// </summary>
    public bool TryOpenSoleLauncherSession(out uint userId, out LoginSessionLease? lease)
    {
        userId = 0;
        lease = null;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            RemoveExpired(now);
            if (_tickets.Count == 1)
            {
                KeyValuePair<string, TicketEntry> only = _tickets.First();
                _tickets.Remove(only.Key);
                SessionEntry session = new(only.Value.UserId, now + _reconnectGrace, now);
                _sessions.Add(only.Key, session);
                Guid connectionId = Guid.NewGuid();
                session.ConnectionIds.Add(connectionId);
                session.LastAttachedAt = now;
                userId = session.UserId;
                lease = new LoginSessionLease(
                    only.Key, connectionId, session.UserId, isReconnect: false);
                return true;
            }

            if (_tickets.Count != 0)
            {
                return false;
            }

            KeyValuePair<string, SessionEntry>[] candidates = _sessions
                .Where(pair => pair.Value.ConnectionIds.Count < MaxOverlappingConnections &&
                               (pair.Value.ConnectionIds.Count != 0 ||
                                pair.Value.ReconnectUntil > now))
                .ToArray();
            if (candidates.Length != 1)
            {
                return false;
            }

            KeyValuePair<string, SessionEntry> candidate = candidates[0];
            SessionEntry existing = candidate.Value;
            existing.ReconnectUntil = DateTimeOffset.MinValue;
            existing.LastAttachedAt = now;
            Guid existingConnectionId = Guid.NewGuid();
            existing.ConnectionIds.Add(existingConnectionId);
            userId = existing.UserId;
            lease = new LoginSessionLease(
                candidate.Key, existingConnectionId, existing.UserId, isReconnect: true);
            return true;
        }
    }

    /// <summary>
    /// Opens the channel socket used by the retail client after server selection.
    /// That socket sends LogInReq with the authenticated user id but does not resend
    /// the launcher ticket, so it must claim the already-open transition session.
    /// </summary>
    public bool TryOpenTransition(
        uint userId,
        out LoginSessionLease? lease)
    {
        lease = null;
        if (userId == 0)
        {
            return false;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            RemoveExpired(now);
            KeyValuePair<string, SessionEntry> candidate = _sessions
                .Where(pair => pair.Value.UserId == userId &&
                               pair.Value.ConnectionIds.Count <
                                   MaxOverlappingConnections &&
                               (pair.Value.ConnectionIds.Count != 0 ||
                                pair.Value.ReconnectUntil > now))
                .OrderByDescending(pair => pair.Value.ConnectionIds.Count != 0)
                .ThenByDescending(pair => pair.Value.LastAttachedAt)
                .FirstOrDefault();
            SessionEntry? session = candidate.Value;
            if (session == null)
            {
                return false;
            }

            session.ReconnectUntil = DateTimeOffset.MinValue;
            session.LastAttachedAt = now;
            Guid connectionId = Guid.NewGuid();
            session.ConnectionIds.Add(connectionId);
            lease = new LoginSessionLease(
                candidate.Key, connectionId, session.UserId, isReconnect: true);
            return true;
        }
    }

    /// <summary>
    /// Releases one authenticated socket and permits a replacement socket during
    /// the short reconnect grace period.
    /// </summary>
    public bool Release(LoginSessionLease? lease)
    {
        if (lease == null)
        {
            return false;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            RemoveExpired(now);
            if (!_sessions.TryGetValue(lease.SessionKey, out SessionEntry? session) ||
                session.UserId != lease.UserId ||
                !session.ConnectionIds.Remove(lease.ConnectionId))
            {
                return false;
            }

            session.ReconnectUntil = now + _reconnectGrace;
            return true;
        }
    }

    private static bool IsWellFormed(string token) =>
        token is { Length: TokenLength } && token.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string Digest(string token)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(token);
        byte[] digest = SHA256.HashData(bytes);
        CryptographicOperations.ZeroMemory(bytes);
        string key = Convert.ToHexString(digest);
        CryptographicOperations.ZeroMemory(digest);
        return key;
    }

    /// <summary>
    /// Throws away every ticket and reconnect session belonging to one account, so the next
    /// LogInReq has nothing to resume and is refused outright.
    ///
    /// This is what makes a ban stick. Dropping a banned player's socket is not enough on
    /// its own: their session survives the disconnect by design, so the client reconnects,
    /// TryResolveAuthenticatedUser resumes it, and the whole login runs again before the
    /// server can drop them a second time - a reconnect loop rather than a kick.
    /// </summary>
    /// <returns>How many sessions and tickets were discarded.</returns>
    public int Revoke(uint userId)
    {
        lock (_lock)
        {
            string[] tickets = _tickets
                .Where(pair => pair.Value.UserId == userId)
                .Select(pair => pair.Key)
                .ToArray();
            string[] sessions = _sessions
                .Where(pair => pair.Value.UserId == userId)
                .Select(pair => pair.Key)
                .ToArray();

            foreach (string key in tickets)
            {
                _tickets.Remove(key);
            }
            foreach (string key in sessions)
            {
                _sessions.Remove(key);
            }
            return tickets.Length + sessions.Length;
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (string key in _tickets
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _tickets.Remove(key);
        }

        foreach (string key in _sessions
                     .Where(pair => pair.Value.ConnectionIds.Count == 0 &&
                                    pair.Value.ReconnectUntil <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _sessions.Remove(key);
        }
    }

    private sealed record TicketEntry(uint UserId, DateTimeOffset ExpiresAt);

    private sealed class SessionEntry(
        uint userId,
        DateTimeOffset reconnectUntil,
        DateTimeOffset lastAttachedAt)
    {
        public uint UserId { get; } = userId;
        public HashSet<Guid> ConnectionIds { get; } = [];
        public DateTimeOffset ReconnectUntil { get; set; } = reconnectUntil;
        public DateTimeOffset LastAttachedAt { get; set; } = lastAttachedAt;
    }
}

/// <summary>
/// Opaque ownership proof for one socket attached to an authenticated launcher
/// session. It contains no raw launcher token.
/// </summary>
public sealed class LoginSessionLease
{
    internal LoginSessionLease(
        string sessionKey,
        Guid connectionId,
        uint userId,
        bool isReconnect)
    {
        SessionKey = sessionKey;
        ConnectionId = connectionId;
        UserId = userId;
        IsReconnect = isReconnect;
    }

    internal string SessionKey { get; }
    internal Guid ConnectionId { get; }
    public uint UserId { get; }
    public bool IsReconnect { get; }
}
