using System.Text;
using Arrowgene.DJMaxOnline.Server.Korea400.Packets;
using Arrowgene.Logging;

namespace Arrowgene.DJMaxOnline.Server.Korea400;

/// <summary>A connected, fully authenticated account visible to administration tools.</summary>
public sealed record OnlinePlayerSummary(
    uint UserId,
    uint WireUserId,
    string AccountId,
    string Nickname);

/// <summary>The outcome of one server-wide announcement attempt.</summary>
public sealed record AdministrationBroadcastResult(int Delivered, int Failed);

/// <summary>
/// Reusable live-administration boundary. The CLI is one adapter; a future management
/// pipe or HTTP adapter can call the same methods without placing Discord-specific code
/// or credentials inside the game server project.
/// </summary>
public sealed class ServerAdministrationService
{
    private static readonly ServerLogger Logger =
        LogProvider.Logger<ServerLogger>(typeof(ServerAdministrationService));

    /// <summary>
    /// Time the reason packet gets to arrive and be stored before the socket is dropped.
    /// The drop is what triggers the dialog, so it must not happen until the reason has
    /// landed - but nothing else is sent in the meantime, so this needs no more than a
    /// comfortable network round trip.
    /// </summary>
    private const int BanKickGraceMs = 1000;

    /// <summary>Gap between attempts at the channel director's sink.</summary>
    private const int DialogRetryMs = 400;

    /// <summary>
    /// How many attempts. The client cycles lobby to server list and back until it is told
    /// to stop, and only part of each cycle can receive the pair, so this covers several
    /// cycles rather than betting on one guessed moment.
    /// </summary>
    private const int DialogAttempts = 20;

    /// <summary>
    /// The ONE reason sub_4499DA acts on. It is hardcoded to 22 there, so this value is not
    /// a choice - it is the key that unlocks the exit. The reason the player actually sees
    /// is written straight afterwards.
    /// </summary>
    private const short ChannelExitReason = 22;

    private readonly IPlayerRepository _players;
    private readonly ClientLookup _clients;
    private readonly LoginTicketService? _loginTickets;

    /// <param name="loginTickets">
    /// Optional, and only so existing tests can construct this without one. A ban cannot be
    /// made to stick without it - see SetAccountLock.
    /// </param>
    public ServerAdministrationService(
        IPlayerRepository players,
        ClientLookup clients,
        LoginTicketService? loginTickets = null)
    {
        _players = players ?? throw new ArgumentNullException(nameof(players));
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _loginTickets = loginTickets;
    }

    public IReadOnlyList<PlayerAccountSummary> ListAccounts() => _players.ListUsers();

    public IReadOnlyList<OnlinePlayerSummary> ListOnlinePlayers()
    {
        List<OnlinePlayerSummary> online = [];
        foreach (Client client in AuthenticatedClients())
        {
            if (client.PlayerStore == null)
            {
                continue;
            }

            try
            {
                online.Add(client.PlayerStore.Read(profile => new OnlinePlayerSummary(
                    profile.UserId,
                    profile.WireUserId,
                    profile.AccountId,
                    profile.Nickname)));
            }
            catch (Exception exception)
            {
                Logger.Exception(client, exception);
            }
        }
        return online
            .OrderBy(player => player.Nickname, StringComparer.OrdinalIgnoreCase)
            .ThenBy(player => player.UserId)
            .ToArray();
    }

    public LocalPlayerProfile CreateAccount(
        string accountId,
        string nickname,
        string password,
        byte gender = 1)
    {
        accountId = NormalizeIdentity(accountId, nameof(accountId));
        nickname = NormalizeIdentity(nickname, nameof(nickname));
        if (gender > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(gender), "Gender must be 0 (female) or 1 (male).");
        }

        // Validate every fallible user-controlled field before inserting the row. The
        // credential write is separate from profile persistence, so this prevents an
        // invalid password or wire-incompatible name from leaving a disabled account.
        LocalPlayerProfile.ValidateAscii(
            accountId, OnUserInfoInfPacket.AccountIdSize, nameof(accountId), false);
        LocalPlayerProfile.ValidateAscii(
            nickname, OnUserIdInfoInfPacket.NicknameSize, nameof(nickname), false);
        PasswordSecurity.ValidateNewPassword(password);

        foreach (PlayerAccountSummary existing in _players.ListUsers())
        {
            if (Matches(existing.AccountId, accountId) ||
                Matches(existing.Nickname, accountId))
            {
                throw new InvalidOperationException(
                    $"Account id '{accountId}' is already in use as an account or nickname.");
            }
            if (Matches(existing.AccountId, nickname) ||
                Matches(existing.Nickname, nickname))
            {
                throw new InvalidOperationException(
                    $"Nickname '{nickname}' is already in use as an account or nickname.");
            }
        }

        LocalPlayerProfile profile = _players.Create(accountId, nickname, gender);
        try
        {
            _players.SetPasswordCredential(
                PasswordSecurity.Create(profile.UserId, password));
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Account '{accountId}' was created, but its password could not be saved. " +
                "Use the live account password command to finish recovery.",
                exception);
        }
        return profile;
    }

    public LocalPlayerProfile SetPassword(string selector, string password)
    {
        selector = NormalizeIdentity(selector, nameof(selector));
        PasswordSecurity.ValidateNewPassword(password);
        LocalPlayerProfile? profile;
        bool found = uint.TryParse(selector, out uint userId)
            ? _players.TryLoad(userId, out profile)
            : _players.TryLoad(selector, out profile);
        if (!found || profile == null)
        {
            throw new KeyNotFoundException($"No account matches '{selector}'.");
        }

        _players.SetPasswordCredential(
            PasswordSecurity.Create(profile.UserId, password));
        return profile;
    }

    /// <summary>
    /// Locks or unlocks an account, and disconnects the player if they are online.
    ///
    /// Kicking matters: without it a ban only takes effect at the player's next login,
    /// so someone banned mid-session keeps playing until they choose to leave.
    /// </summary>
    public LocalPlayerProfile SetAccountLock(
        string selector, AccountLockState state, string reason = "")
    {
        selector = NormalizeIdentity(selector, nameof(selector));
        LocalPlayerProfile? profile;
        bool found = uint.TryParse(selector, out uint userId)
            ? _players.TryLoad(userId, out profile)
            : _players.TryLoad(selector, out profile);
        if (!found || profile == null)
        {
            throw new KeyNotFoundException($"No account matches '{selector}'.");
        }

        profile.LockState = state;
        // Clearing a lock clears its reason; keeping stale text against an active
        // account is how an old note gets mistaken for a current one.
        profile.LockReason = state == AccountLockState.None ? string.Empty : reason.Trim();
        _players.Save(profile);
        // The reason is logged server-side only. It is deliberately absent from every
        // packet this method sends.
        Logger.Info(
            $"Account '{profile.AccountId}' (user {profile.UserId}) is now " +
            $"{AccountLockReasons.Describe(state)}" +
            (profile.LockReason.Length == 0 ? "." : $": {profile.LockReason}"));

        if (AccountLockReasons.ReasonFor(state) is { } disconnectReason)
        {
            // REVOKE THE LAUNCHER SESSION FIRST, before anyone is disconnected.
            //
            // Dropping the socket alone does not remove a player: the session deliberately
            // outlives a disconnect so a channel change can resume it, so the client simply
            // reconnects, LogInReq resumes the session, the whole login runs again and the
            // server drops them a second time. That is the reconnect loop. With the session
            // gone there is nothing to resume, and the reconnect is refused at LogInReq
            // before any of the login work happens.
            if (_loginTickets == null)
            {
                Logger.Error(
                    $"No login ticket service: the launcher session for user " +
                    $"{profile.UserId} cannot be revoked, so they can reconnect until it " +
                    "expires on its own.");
            }
            else
            {
                int revoked = _loginTickets.Revoke(profile.UserId);
                Logger.Info(
                    $"Revoked {revoked} launcher ticket(s) and session(s) for user " +
                    $"{profile.UserId}; reconnects will be refused.");
            }

            Client[] target = [.. _clients.GetAll()
                .Where(online => online.PlayerStore?.Profile.UserId == profile.UserId)];

            // ANNOUNCE FIRST, and never to the person being removed.
            //
            // By NICKNAME ONLY - the reason is operator-only. A ban notice is a
            // deterrent, not a place to publish what somebody did.
            BroadcastChat(
                state == AccountLockState.Locked
                    ? $"{profile.Nickname} has been banned."
                    : $"{profile.Nickname} has been suspended pending review.",
                ChatMessageType.Alert,
                target);

            // THE BAN DIALOG. Three packets, in this order, with a pause in the middle.
            //
            // Read out of the client, and it is this convoluted because only ONE scene can
            // reach the disconnection screen:
            //
            //  1. OnLogOutAck. sub_431720 sets dword_6846E8, which the lobby update
            //     sub_444C5E turns into exit code 0 - and DJMaxApp::Run (sub_42D5E4) sends
            //     code 0 back to the channel/server-list director. The LOBBY itself can
            //     never show the dialog: its packet sink sub_4442D8 has no case for id 8,
            //     and its only exit codes are 0 and 1, never the -1 the app needs.
            //
            //  2. OnDisconnectPeerInf with reason 22, once the channel director is up. Its
            //     sink sub_4499DA is the one place that answers id 8 with an exit: on
            //     reason exactly 22 it clears the sink (sub_42F220(0)) and calls
            //     sub_4ACE60(-1). Code -1 is what DJMaxApp::Run routes to
            //     DJMaxApp::RunDisconnection (sub_42D45F).
            //
            //  3. OnDisconnectPeerInf with the REAL reason. sub_4318E0 writes net+895300
            //     before it looks at the sink, and the sink is now null, so this overwrites
            //     22 with 27 or 28 and triggers nothing further. DisconnectionDirector then
            //     reads the field in sub_44D012 and draws DISCONNECTMSG5 / MSG6.
            //
            // Nothing of ours may be sent in between - hence MarkKicked before the first
            // packet, and the alert above excluding them.
            foreach (Client online in target)
            {
                online.MarkKicked();
                // Pushes them out of the lobby. The lobby can never show the dialog itself,
                // so the first job is simply to get them to the channel director.
                online.Send(OnLogOutAckPacket.Build());

                Client leaving = online;
                short shown = disconnectReason;
                _ = Task.Run(async () =>
                {
                    // REPEATED, not sent once on a timer.
                    //
                    // Leaving the lobby sets dword_6846E8, which nothing clears except a
                    // successful login, so the client cycles lobby -> server list -> lobby.
                    // Only the channel director's sink can act on the pair, and it owns the
                    // sink for part of each cycle, so a single attempt at a guessed moment
                    // usually misses. Retrying lands one inside that window instead.
                    //
                    // Repeats are harmless once it has worked: the sink is cleared by then,
                    // so the packets only rewrite net+895300, and the dialog has already
                    // taken its text. Every pass ends on the real reason, never on 22.
                    for (int attempt = 0; attempt < DialogAttempts; attempt++)
                    {
                        await Task.Delay(DialogRetryMs);
                        leaving.Send(OnDisconnectPeerInfPacket.Build(ChannelExitReason));
                        leaving.Send(OnDisconnectPeerInfPacket.Build(shown));
                    }
                    leaving.SendThenClose(
                        OnDisconnectPeerInfPacket.Build(shown), BanKickGraceMs);
                });

                Logger.Info(online,
                    $"Disconnecting: account was locked (reason {disconnectReason} " +
                    "queued for the client's dialog).");
            }
        }
        return profile;
    }

    public AdministrationBroadcastResult BroadcastChat(
        string message,
        ChatMessageType type = ChatMessageType.Notice,
        IReadOnlyCollection<Client>? except = null)
    {
        message = NormalizeAnnouncement(message, nameof(message));
        if (type is not (ChatMessageType.System or ChatMessageType.Notice or
            ChatMessageType.Alert))
        {
            throw new ArgumentOutOfRangeException(
                nameof(type), "Console announcements support System, Notice, or Alert.");
        }

        int bytes = Encoding.ASCII.GetByteCount(message);
        if (bytes > ChatInfPacket.MaximumTextLength)
        {
            throw new ArgumentException(
                $"Announcement text cannot exceed {ChatInfPacket.MaximumTextLength} " +
                "ASCII bytes.", nameof(message));
        }
        return Broadcast(OnChatInfPacket.BuildAscii(message, type), except);
    }

    public AdministrationBroadcastResult BroadcastBigNews(string title, string body)
    {
        title = (title ?? string.Empty).Trim();
        body = NormalizeAnnouncement(body, nameof(body));
        if (title.Any(character => character > 0x7F))
        {
            throw new ArgumentException(
                "This client announcement packet supports ASCII titles only.",
                nameof(title));
        }
        if (Encoding.ASCII.GetByteCount(title) >= OnBigNewsInfPacket.TitleSize)
        {
            throw new ArgumentException(
                $"Big-news titles cannot exceed {OnBigNewsInfPacket.TitleSize - 1} " +
                "ASCII bytes.", nameof(title));
        }
        if (Encoding.ASCII.GetByteCount(body) >= OnBigNewsInfPacket.BodySize)
        {
            throw new ArgumentException(
                $"Big-news bodies cannot exceed {OnBigNewsInfPacket.BodySize - 1} " +
                "ASCII bytes.", nameof(body));
        }
        return Broadcast(OnBigNewsInfPacket.Build(body, title));
    }

    private AdministrationBroadcastResult Broadcast(
        Packet packet, IReadOnlyCollection<Client>? except = null)
    {
        int delivered = 0;
        int failed = 0;
        foreach (Client client in AuthenticatedClients())
        {
            if (except != null && except.Contains(client))
            {
                continue;
            }
            try
            {
                client.Send(packet);
                delivered++;
            }
            catch (Exception exception)
            {
                failed++;
                Logger.Exception(client, exception);
            }
        }
        return new AdministrationBroadcastResult(delivered, failed);
    }

    private Client[] AuthenticatedClients() =>
        _clients.GetAll().Where(client => client.UserId.HasValue).ToArray();

    private static string NormalizeIdentity(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        string normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Value cannot be empty.", parameterName);
        }
        return normalized;
    }

    private static string NormalizeAnnouncement(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        string normalized = value.Trim();
        if (normalized.Any(character => character > 0x7F))
        {
            throw new ArgumentException(
                "This client announcement packet supports ASCII text only.", parameterName);
        }
        return normalized;
    }

    private static bool Matches(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
