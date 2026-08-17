using Arrowgene.DJMaxOnline.Server.Japan400.Packets;
using Arrowgene.Logging;

namespace Arrowgene.DJMaxOnline.Server.Japan400;

/// <summary>
/// Opens or resumes a launcher-authenticated session, resolves its SQLite user,
/// and keeps one synchronized store per account.
/// </summary>
public sealed class LocalAccountResolver
{
    private readonly object _lock = new();
    private readonly IPlayerRepository? _repository;
    private readonly ShopCatalog _shop;
    private readonly LoginTicketService _tickets;
    private readonly bool _itemsNeverExpire;
    private readonly Dictionary<uint, LocalPlayerStore> _stores = [];

    private static readonly ServerLogger Logger =
        LogProvider.Logger<ServerLogger>(typeof(LocalAccountResolver));

    /// <summary>
    /// Account-class bits granted to every account on login, from
    /// <c>Setting.DefaultAccountClassFlags</c>. Applied in memory only - never written
    /// back - so clearing the setting takes the privilege away again.
    /// </summary>
    public uint DefaultAccountClass { get; set; }

    public LocalAccountResolver(
        LocalPlayerStore? fallback,
        ShopCatalog shop,
        IPlayerRepository? repository,
        LoginTicketService tickets,
        bool itemsNeverExpire = false)
    {
        _shop = shop ?? throw new ArgumentNullException(nameof(shop));
        _repository = repository;
        _tickets = tickets ?? throw new ArgumentNullException(nameof(tickets));
        _itemsNeverExpire = itemsNeverExpire;
        // Only the legacy single-player mode supplies a fallback, and only then is it
        // registered. With a database every account is loaded on demand from its ticket,
        // so seeding here would just shadow the row holding the same id.
        if (_repository == null && fallback != null)
        {
            _stores.Add(fallback.Profile.UserId, fallback);
        }
    }

    private void ApplyDefaultAccountClass(LocalPlayerProfile profile)
    {
        if (DefaultAccountClass == 0 ||
            (profile.AccountClass & DefaultAccountClass) == DefaultAccountClass)
        {
            return;
        }
        uint before = profile.AccountClass;
        profile.AccountClass |= DefaultAccountClass;
        Logger.Info(
            $"Granted default account class to {profile.AccountId}: " +
            $"0x{before:X} -> 0x{profile.AccountClass:X} " +
            $"[{string.Join(", ", AccountClassInfo.ToNames(profile.AccountClass))}].");
    }

    /// <summary>
    /// The first connection consumes the random launcher ticket. Subsequent calls
    /// only work while its bounded transition/reconnect session is valid.
    /// </summary>
    public bool TryResolveTicket(
        string ticket,
        out LocalPlayerStore? store,
        out LoginSessionLease? lease)
    {
        store = null;
        lease = null;
        if (_repository == null ||
            !_tickets.TryOpenSession(ticket, out uint userId, out lease) ||
            lease == null)
        {
            return false;
        }

        lock (_lock)
        {
            if (_stores.TryGetValue(userId, out store))
            {
                return true;
            }

            if (!_repository.TryLoad(userId, out LocalPlayerProfile? profile) || profile == null)
            {
                _tickets.Release(lease);
                lease = null;
                return false;
            }

            ApplyDefaultAccountClass(profile);
            store = new LocalPlayerStore(
                profile, _shop, repository: _repository, itemsNeverExpire: _itemsNeverExpire);
            _stores.Add(profile.UserId, store);
            return true;
        }
    }

    /// <summary>
    /// Translates a wire id back to the persistent account key. Session ids are randomised
    /// per login and are the only id the client ever quotes back, but tickets, stores and
    /// every database row are keyed on the account. An id that is already an account key
    /// passes straight through.
    /// </summary>
    private uint ResolveAccountKey(uint userId)
    {
        lock (_lock)
        {
            if (_stores.ContainsKey(userId))
            {
                return userId;
            }
            foreach (KeyValuePair<uint, LocalPlayerStore> entry in _stores)
            {
                if (entry.Value.Profile.WireUserId == userId)
                {
                    return entry.Key;
                }
            }
        }
        return userId;
    }

    /// <summary>
    /// Binds a connection that never presented a ticket to the sole pending or active
    /// launcher login. See LoginTicketService.TryOpenSoleLauncherSession for why this
    /// leg exists.
    /// </summary>
    public bool TryResolveSoleLauncherSession(
        out LocalPlayerStore? store,
        out LoginSessionLease? lease)
    {
        store = null;
        lease = null;
        if (_repository == null ||
            !_tickets.TryOpenSoleLauncherSession(out uint userId, out lease) ||
            lease == null)
        {
            return false;
        }

        lock (_lock)
        {
            if (_stores.TryGetValue(userId, out store))
            {
                return true;
            }

            if (!_repository.TryLoad(userId, out LocalPlayerProfile? profile) ||
                profile == null)
            {
                _tickets.Release(lease);
                lease = null;
                return false;
            }

            ApplyDefaultAccountClass(profile);
            store = new LocalPlayerStore(
                profile, _shop, repository: _repository, itemsNeverExpire: _itemsNeverExpire);
            _stores.Add(profile.UserId, store);
            return true;
        }
    }

    /// <summary>
    /// Resolves the channel socket's LogInReq against a launcher session opened by
    /// the preceding server-list socket. The retail client supplies only its user id
    /// during this leg of the handoff.
    /// </summary>
    public bool TryResolveAuthenticatedUser(
        uint userId,
        out LocalPlayerStore? store,
        out LoginSessionLease? lease)
    {
        store = null;
        lease = null;
        if (_repository == null)
        {
            return false;
        }

        // The client quotes whatever id OnLogInAck last gave it, which is the SESSION id -
        // so a player moving to another channel logs in with an id the ticket service has
        // never seen, and the socket was closed with "no active launcher session". Map it
        // back to the account key before opening the transition.
        userId = ResolveAccountKey(userId);

        if (!_tickets.TryOpenTransition(userId, out lease) || lease == null)
        {
            return false;
        }

        lock (_lock)
        {
            if (_stores.TryGetValue(userId, out store))
            {
                return true;
            }

            if (!_repository.TryLoad(userId, out LocalPlayerProfile? profile) ||
                profile == null)
            {
                _tickets.Release(lease);
                lease = null;
                return false;
            }

            ApplyDefaultAccountClass(profile);
            store = new LocalPlayerStore(
                profile, _shop, repository: _repository, itemsNeverExpire: _itemsNeverExpire);
            _stores.Add(profile.UserId, store);
            return true;
        }
    }
}
