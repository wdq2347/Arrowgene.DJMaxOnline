using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using Arrowgene.DJMaxOnline.Server.Korea400.Packets;
using Arrowgene.Logging;

namespace Arrowgene.DJMaxOnline.Server.Korea400;

/// <summary>
/// Detailed server-side result for the named /invite command. These are deliberately
/// separate from <see cref="QuickInviteResult"/>, whose numeric values are fixed by the
/// retail quick-invite acknowledgement packet and cannot describe the actual blocker.
/// </summary>
public enum NamedRoomInviteResult
{
    Sent,
    InviterNotInRoom,
    RoomInProgress,
    TargetNotFound,
    CannotInviteSelf,
    TargetAlreadyInRoom,
    TargetRefusesInvitations,
    RoomFull,
    StateChanged
}

/// <summary>Connected-player state shared by one local game-channel endpoint.</summary>
public sealed class LocalLobby
{
    private const int BattleItemCapacity = 4;
    private static readonly ServerLogger Logger =
        LogProvider.Logger<ServerLogger>(typeof(LocalLobby));

    private readonly object _lock = new();
    private readonly List<Client> _clients = [];
    private readonly Dictionary<ushort, LocalRoomState> _rooms = [];
    private readonly Dictionary<Client, LocalRoomMember> _memberships = [];
    private readonly Dictionary<Client, CancellationTokenSource> _accountClassFuzzers = [];
    private readonly Dictionary<Client, CancellationTokenSource> _messengerFuzzers = [];
    private readonly LocalPlayerStore? _players;
    private readonly SongCatalog _songs;
    private readonly IGameInfoProvider _gameInfo;
    private readonly CourseCatalog _courses;
    private bool _shutdownDraining;

    public LocalLobby(
        ChannelInfo channel,
        LocalPlayerStore? players,
        SongCatalog songs,
        IGameInfoProvider gameInfo,
        CourseCatalog? courses = null)
    {
        Channel = channel ?? throw new ArgumentNullException(nameof(channel));
        // Null with an account database: every client carries its own store and the
        // consumer drops player packets from anyone who has not authenticated. Only the
        // legacy single-player mode supplies one here.
        _players = players;
        _songs = songs ?? throw new ArgumentNullException(nameof(songs));
        _gameInfo = gameInfo ?? throw new ArgumentNullException(nameof(gameInfo));
        _courses = courses ?? CourseCatalog.Empty;
    }

    /// <summary>
    /// How much of an equipped loadout's HP the server hands out, as a percentage.
    ///
    /// The gauge bonus is entirely server-authored - the client just adds
    /// <c>net[894916 + 4*slot]</c> to its base gauge table - so this is the whole balance
    /// lever, and it needs no change to the client's ItemStock.csv. 100 = the catalog's
    /// values as written (Blade's 아리 + 캔디걸기어 = +50 over a base of ~100); 0 = gear is
    /// cosmetic and everyone starts on the same gauge; 50 = half the advantage.
    /// </summary>
    public int EquipmentHpPercent { get; init; } = 100;

    /// <summary>Percentage of the catalog's <c>max</c> paid as bonus MAX per song.</summary>
    public int EquipmentMaxPercent { get; init; } = 100;

    /// <summary>
    /// Where finished multiplayer matches are published for the local status API. Shared
    /// across channels so the feed is one ordered stream. Null leaves the capture off
    /// entirely, which is what the tests and the legacy single-player mode want.
    /// </summary>
    public MatchHistory? MatchHistory { get; init; }

    /// <summary>Percentage of the catalog's <c>exp</c> paid as bonus EXP per song.</summary>
    public int EquipmentExperiencePercent { get; init; } = 100;

    /// <summary>
    /// Extra MAX and EXP a Premium account earns, as a percentage: 100 = +100%, i.e.
    /// double. Applied to the whole payout - base award plus equipment and booster
    /// bonuses - and gated on the same bit the client uses for its "Premium" label
    /// (<see cref="AccountClassInfo.IsPremium"/>), so whoever shows as Premium earns it.
    /// </summary>
    public int PremiumRewardBonusPercent { get; init; } = 100;

    /// <summary>
    /// Chance, in percent, that picking the RANDOM disc turns the run into DJ Mission
    /// Match instead of just playing a random song. 0 disables missions entirely, 100
    /// makes every random pick a mission.
    /// </summary>
    public int MissionMatchChancePercent { get; init; } = 10;

    /// <summary>
    /// Account administration, for the in-game admin commands. Null when the server runs
    /// without an account database, in which case those commands report unavailable
    /// rather than throwing at a player.
    /// </summary>
    public ServerAdministrationService? Administration { get; init; }

    /// <summary>
    /// Combo per item-battle item DROP. Entirely server policy: the client never decides
    /// when an item drops, it only requests one when it hits a note this server marked
    /// with OnCrItemInf. Live state arrives every five seconds, so a drop lands on the
    /// first report after the combo is crossed.
    /// </summary>
    public int BattleItemComboInterval { get; init; } =
        BattleItemComboRewardTracker.DefaultComboInterval;

    /// <summary>
    /// Offer every course in the loaded client catalog without changing the player's
    /// persisted prerequisite progress. Turning this off restores normal progression.
    /// </summary>
    public bool UnlockAllCourses { get; init; }

    /// <summary>Exact-accuracy collection discs; empty disables them.</summary>
    public IReadOnlyList<AccuracyDiscRule> AccuracyDiscs { get; init; } = [];

    /// <summary>
    /// Millisecond adjustment applied to every judgment window, indexed by the room's
    /// match mode: 0 = freemode, 1 = RANKED, 2 = score battle, 3 = item battle,
    /// 4 = course. NEGATIVE is stricter - index 1 is the one that is meant to be tighter -
    /// and positive is more forgiving. Empty or short leaves a mode on the retail values.
    ///
    /// The client has no judgment table of its own for online play - it uses the 13
    /// windows the server ships in the chart's config block - so this is the whole
    /// timing-strictness lever and it needs no client-side change.
    /// </summary>
    public IReadOnlyList<int> JudgmentAdjustmentMsByMatchMode { get; init; } = [];

    /// <summary>
    /// Delivers the chart. A player holding a SIGHT booster gets their own copy with wider
    /// judgment windows; everybody else shares the room's blob, so with no boosters in the
    /// room this is exactly the single broadcast it has always been.
    /// </summary>
    private void SendGameInfo(
        Client[] recipients,
        GameInfoPayload roomPayload,
        uint discId,
        byte difficulty,
        short roomDescriptor,
        JudgmentWindows roomWindows)
    {
        Packet shared = OnGameInfoInfPacket.Build(roomPayload);
        foreach (Client recipient in recipients)
        {
            int boost = JudgmentBoostFor(recipient);
            if (boost == 0)
            {
                recipient.Send(shared);
                continue;
            }
            if (_gameInfo.TryLoad(
                    discId,
                    difficulty,
                    roomDescriptor,
                    out GameInfoPayload? boosted,
                    out _,
                    out string error,
                    roomWindows.Adjust(boost)))
            {
                recipient.Send(OnGameInfoInfPacket.Build(boosted!));
                Logger.Info(recipient,
                    $"Judgment widened by {boost}ms for this run (SIGHT booster).");
            }
            else
            {
                // Never drop the chart over a booster: fall back to the room's copy.
                Logger.Error(recipient,
                    $"Could not build a SIGHT-boosted chart ({error}); using the room's.");
                recipient.Send(shared);
            }
        }
    }

    /// <summary>
    /// Total <c>judgmentboost</c> of a player's armed SIGHT boosters, in milliseconds of
    /// extra window. Worn gear never carries this stat - only the section-5 consumables do.
    /// </summary>
    private int JudgmentBoostFor(Client client)
    {
        LocalPlayerStore store = client.PlayerStoreOr(_players);
        uint[] boosters = store.Read(
            profile => profile.Inventory.ActiveBoosters.ToArray());
        if (boosters.Length == 0)
        {
            return 0;
        }

        int total = 0;
        foreach (uint itemId in boosters)
        {
            if (store.Shop.TryGet((ushort)itemId, out ShopItemDefinition? item) &&
                item != null)
            {
                total += (int)item.JudgmentBoost;
            }
        }
        return total;
    }

    private int JudgmentAdjustmentFor(byte matchMode) =>
        matchMode < JudgmentAdjustmentMsByMatchMode.Count
            ? JudgmentAdjustmentMsByMatchMode[matchMode]
            : 0;

    private EquipmentBonusScale BonusScale => new(
        EquipmentHpPercent, EquipmentMaxPercent, EquipmentExperiencePercent);

    /// <summary>
    /// Everything a player is wearing plus every booster they have armed - the full set of
    /// items that pay out for the song about to be, or just, played.
    /// </summary>
    private EquipmentBonus BonusFor(Client client, byte[] loadout)
    {
        LocalPlayerStore store = Players(client);
        return EquipmentBonus.For(
            EquipmentBonus.EquippedItemIds(loadout)
                .Concat(store.Read(profile => profile.Inventory.ActiveBoosters.ToArray())),
            store.Shop,
            BonusScale);
    }

    public CourseCatalog Courses => _courses;

    public ChannelInfo Channel { get; }

    /// <summary>
    /// A read-only projection of this channel for the local status API.
    ///
    /// Deliberately narrow: room settings and nicknames only. No account id, no session
    /// token, no network endpoint - see <see cref="ChannelStatus"/> for why. Nothing here
    /// mutates state, and the lock is held only long enough to copy.
    /// </summary>
    public ChannelStatus StatusSnapshot()
    {
        List<RoomStatus> rooms = [];
        List<PlayerStatus> players = [];

        lock (_lock)
        {
            foreach (LocalRoomState room in _rooms.Values.OrderBy(entry => entry.Index))
            {
                LocalRoomMember? host = room.Members.FirstOrDefault(member => member.IsHost);
                rooms.Add(new RoomStatus(
                    Index: room.Index,
                    Title: RoomPacketFields.DecodeTitle(room.Settings.TitleField),
                    Occupants: room.OccupantCount,
                    Capacity: room.Settings.Capacity,
                    OpenSlots: room.OpenSlotCount,
                    // The CHANNEL's key mode, not room.Settings.KeyMode. The latter is the
                    // room descriptor's is-5-key flag (0/1), not a key count, so publishing
                    // it renders as "1K" - a mode that does not exist.
                    KeyMode: (byte)Channel.KeyMode,
                    MatchMode: room.Settings.MatchMode,
                    GameType: room.Settings.GameType,
                    LevelRestriction: room.Settings.LevelRestriction,
                    Locked: room.Settings.HasPassword,
                    Playing: room.Phase != RoomPlayPhase.Waiting,
                    // DiscId is the 0-based disc INDEX; DiscStock numbers from 1. The API
                    // publishes catalog ids throughout (see ScoreFeedQueries), so convert
                    // here too - publishing the raw index named the previous song.
                    SongId: host is { HasDisc: true } ? host.DiscId + 1u : null));
            }

            foreach (Client client in _clients)
            {
                LocalPlayerStore? store = client.PlayerStore;
                if (store == null)
                {
                    // Not authenticated yet, so it has no nickname to publish.
                    continue;
                }

                _memberships.TryGetValue(client, out LocalRoomMember? membership);
                (string nickname, int level) = store.Read(
                    profile => (profile.Nickname, (int)profile.Progress.Level));
                players.Add(new PlayerStatus(
                    Nickname: nickname,
                    Level: level,
                    RoomIndex: membership?.Room.Index,
                    Playing: membership is { Room.Phase: not RoomPlayPhase.Waiting },
                    // Catalog id, as above.
                    SongId: membership is { HasDisc: true } ? membership.DiscId + 1u : null,
                    KeyMode: (byte)Channel.KeyMode));
            }
        }

        return new ChannelStatus(
            Name: Channel.Name,
            KeyMode: (byte)Channel.KeyMode,
            Players: players.Count,
            Playing: players.Count(player => player.Playing),
            Rooms: rooms.Count,
            RoomList: rooms,
            PlayerList: players);
    }

    /// <summary>
    /// Authenticated players currently inside this channel, whether waiting in the lobby,
    /// sitting in a room, or playing. Pre-login/channel-select sockets are not population.
    /// </summary>
    public ushort UserCount
    {
        get
        {
            lock (_lock)
            {
                return checked((ushort)Math.Min(_clients.Count, ushort.MaxValue));
            }
        }
    }

    /// <summary>
    /// Prevents a draining server from starting another ordinary or ranked song. A course
    /// that had already completed at least one stage may continue until its terminal stage.
    /// </summary>
    public void BeginShutdownDrain()
    {
        lock (_lock)
        {
            _shutdownDraining = true;
        }
    }

    /// <summary>
    /// Whether this client belongs to a room whose current song is loading/playing, or to
    /// a course that is between stages. The whole room is protected when one member owns
    /// the course so multiplayer peers are never disconnected underneath that run.
    /// </summary>
    public bool IsShutdownProtected(Client client)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_lock)
        {
            return _memberships.TryGetValue(client, out LocalRoomMember? member) &&
                   (member.Room.Phase != RoomPlayPhase.Waiting ||
                    member.Room.Members.Any(value =>
                        IsCourseInProgress(value.Client)));
        }
    }

    public LobbyUserIdentity Identity(Client client) =>
        Players(client).Read(LobbyUserIdentity.CreateLocal);

    /// <summary>
    /// Pushes persisted identity changes through each native client cache: the
    /// full user record, lobby waiter row, and any occupied room rows.
    /// </summary>
    public void BroadcastLocalProfile(Client actingClient)
    {
        ArgumentNullException.ThrowIfNull(actingClient);
        LocalPlayerStore actingPlayers = Players(actingClient);
        Client[] waiting;
        List<(LocalRoomMember Member, Client[] Recipients)> roomUpdates = [];
        lock (_lock)
        {
            waiting = WaitingClients();
            foreach (LocalRoomState room in _rooms.Values)
            {
                Client[] recipients =
                    room.Members.Select(member => member.Client).ToArray();
                foreach (LocalRoomMember member in room.Members)
                {
                    roomUpdates.Add((member, recipients));
                }
            }
        }

        // NOTE: do NOT send OnUserInfoInf (0x43) — the Korean client has no size
        // registered for id 67, so it desyncs the stream. The waiter record (id 60,
        // 76 bytes) and room-member record (id 81, 135 bytes) are correctly sized and
        // carry the updated icon/profile for the live refresh.
        Send(
            waiting,
            OnWaiterInfoUpdateInfPacket.Build(
                actingPlayers.Read(LobbyWaiterInfo.CreateLocal)));
        foreach ((LocalRoomMember member, Client[] recipients) in roomUpdates)
        {
            Send(
                recipients,
                OnUpdateJoinerInfoInfPacket.Build(MemberInfo(member)));
        }
    }

    public bool UpdateLocalIcon(LocalPlayerStore players, Client actingClient, uint iconId)
    {
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(actingClient);
        // 0xFFFFFFFF is the unused wire sentinel and cannot represent an icon;
        // 0xFFFFFFFF also cannot be incremented into the one-based wire form.
        if (iconId >= uint.MaxValue - 1)
        {
            return false;
        }

        (uint UserId, uint AccountClass) updated = players.Update(profile =>
        {
            profile.IconId = iconId;
            return (profile.WireUserId, profile.AccountClass);
        });

        // The self-profile card repaints (client sub_441675) only when the update's
        // peer key @+7 equals net+793744 — the connection id the OnConnectAck handed
        // this client (its AssignedUserId), NOT profile.ProfileCode. Send that.
        ushort peerKey = RequireAssignedUserId(actingClient);

        Client[] clients;
        lock (_lock)
        {
            clients = _clients.ToArray();
        }
        Send(clients, OnUpdateUserIconInfPacket.Build(
            updated.UserId, peerKey, iconId, updated.AccountClass));
        BroadcastLocalProfile(actingClient);
        return true;
    }

    public void Join(Client client)
    {
        ArgumentNullException.ThrowIfNull(client);
        Client[] waitingPeers;
        Client[] roomPeers;
        RoomListEntry[] rooms;
        lock (_lock)
        {
            if (_clients.Contains(client))
            {
                return;
            }
            waitingPeers = WaitingClients();
            roomPeers = _clients.Where(peer => _memberships.ContainsKey(peer)).ToArray();
            _clients.Add(client);
            rooms = _rooms.Values
                .Select(RoomList)
                .OrderBy(room => room.RoomIndex)
                .ToArray();
        }

        // The session id is allocated by the login handler, before OnLogInAck tells the
        // client its own id. Anything that reaches the lobby without one (the CLI paths)
        // still needs a unique identity.
        if (Players(client).Read(profile => profile.SessionUserId) == 0)
        {
            AssignSessionId(client);
        }

        SynchronizeLobbyWaiters(client, waitingPeers, roomPeers);
        // Seed the newcomer's lobby grid with the rooms that already exist. The empty
        // "EMPTY STAGE" slots are drawn client-side from the room count in OnLogInAck,
        // so only real rooms are sent — without this a player entering the lobby cannot
        // see (or join) any room created before they arrived.
        foreach (RoomListEntry room in rooms)
        {
            client.Send(OnRoomInfoInfPacket.Build(room));
        }
        // The login bootstrap ships an EMPTY messenger snapshot, so without this the
        // friends panel starts blank every session no matter what was saved.
        SendMessengerBook(client);
        // ...and everyone who already has this player as a contact needs their list
        // rebuilt so the newcomer turns online for them.
        RefreshMessengerPresence(client);
        // Timed items only lapse while the player is away, so entering the lobby is the
        // natural point to reconcile them; silent because there is nothing to report when
        // nothing expired.
        SendExpiredItems(client, announce: false);
        SendMessageOfTheDay(client);
    }

    /// <summary>
    /// Greets the player and sends the configured message of the day as they enter the
    /// channel, one chat line per entry. Drawn in the System colour so it reads as server
    /// text rather than someone talking. Each line may use <c>{channel}</c> and
    /// <c>{player}</c>, which is also how the default greeting is built.
    /// </summary>
    private void SendMessageOfTheDay(Client client)
    {
        string nickname = Players(client).Read(profile => profile.Nickname);
        client.Send(OnChatInfPacket.BuildAscii(
            $"Welcome to {Channel.FullName}  {nickname}"));
        foreach (string line in MessageOfTheDay)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                client.Send(OnChatInfPacket.BuildAscii(line
                    .Replace("{channel}", Channel.FullName, StringComparison.OrdinalIgnoreCase)
                    .Replace("{player}", nickname, StringComparison.OrdinalIgnoreCase)));
            }
        }
    }

    /// <summary>Channel message of the day; empty disables it.</summary>
    public IReadOnlyList<string> MessageOfTheDay { get; set; } = [];

    public void Leave(Client client)
    {
        ArgumentNullException.ThrowIfNull(client);

        // A socket that never authenticated was never in the lobby: Join only runs after
        // LogInReq attaches a player. There is nothing to erase, and asking for its
        // player would throw - which used to escape the disconnect callback and leave the
        // connection registered forever, so the keep-alive sweeper kept "closing" a socket
        // that never went away.
        if (client.PlayerStore == null)
        {
            lock (_lock)
            {
                _clients.Remove(client);
            }
            return;
        }

        Client[] remaining;
        CancellationTokenSource? accountClassFuzzer;
        bool wasPresent;
        lock (_lock)
        {
            wasPresent = _clients.Remove(client);
            // The toggle is per session, and the client re-sends it when it wants it on.
            // Keeping a dead Client in here would also pin it for the lifetime of the set.
            _invitesRefused.Remove(client);
            _accountClassFuzzers.Remove(client, out accountClassFuzzer);
            _messengerFuzzers.Remove(client, out CancellationTokenSource? messengerFuzzer);
            messengerFuzzer?.Cancel();
            remaining = _clients.ToArray();
        }

        accountClassFuzzer?.Cancel();

        LeaveRoom(client, sendAcknowledgement: false);

        // The erase goes out even when this client was ALREADY out of the lobby list.
        // Leave runs twice on a channel switch - once for LogOutReq, once when the socket
        // drops - and bailing out on the second call meant that whichever path ran second
        // told nobody. Erasing a row that is already gone is a no-op on the client, so
        // sending it unconditionally is strictly safer than guessing which path ran.
        //
        // The waiter map is keyed by record+71 (sub_4333F0 inserts on it, sub_4334A0
        // erases on it), and that key is the STABLE per-account one, not the session id.
        ushort waiterKey = WaiterKey(client);
        Packet erase = OnWaiterInfoEraseInfPacket.Build(waiterKey);
        foreach (Client peer in remaining)
        {
            peer.Send(erase);
        }
        Logger.Info(client,
            $"Left the lobby (wasListed={wasPresent}, waiterKey={waiterKey}); " +
            $"erase sent to {remaining.Length} client(s): " +
            $"[{string.Join(", ", remaining.Select(DescribeIds))}].");

        // The waiter row is not the friends list: everyone holding this player as a
        // contact needs their book rebuilt or it keeps drawing them online.
        RefreshMessengerPresence(client);
    }

    public void RelayChat(Client sender, LobbyChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(request);
        if (request.EncodedText.Length == 0)
        {
            return;
        }

        // Flood control. The client renders CHATMSG1/2/3 from this packet but nothing
        // was ever sending it, so spamming had no consequence. A muted message is
        // dropped rather than relayed - the state byte only changes the sender's chat
        // box, it does not suppress anything for the people receiving it.
        ChatControlState? control = sender.ChatFlood.Observe(DateTimeOffset.UtcNow,
            out bool allowed);
        if (control is { } state)
        {
            sender.Send(OnChatControlInfPacket.Build(state));
            Logger.Info(sender, $"Chat flood control: {state}.");

            // Schedule the unmute. Without this the chat box stays shut until the player
            // sends another message - which they cannot see any reason to do, because as
            // far as they know they are still muted. The state is only recomputed when a
            // chat packet arrives, so the lifting has to be driven from here instead.
            if (state == ChatControlState.Disable)
            {
                Client muted = sender;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(ChatFloodControl.MuteDuration);
                    try
                    {
                        if (muted.ChatFlood.Expire(DateTimeOffset.UtcNow) is { } lifted)
                        {
                            muted.Send(OnChatControlInfPacket.Build(lifted));
                            Logger.Info(muted, $"Chat flood control: {lifted} (mute expired).");
                        }
                    }
                    catch (Exception exception)
                    {
                        // They may have disconnected while muted; that is not an error.
                        Logger.Debug(muted, $"Could not lift chat mute: {exception.GetType().Name}.");
                    }
                });
            }
        }
        if (!allowed)
        {
            return;
        }

        Client[] recipients;
        Client[] channelRecipients;
        ChatMessageType messageType;
        lock (_lock)
        {
            channelRecipients = _clients.ToArray();
            if (_memberships.TryGetValue(sender, out LocalRoomMember? member))
            {
                recipients = member.Room.Members.Select(value => value.Client).ToArray();
                messageType = ChatMessageType.Room;
            }
            else
            {
                recipients = _clients.Where(client => !_memberships.ContainsKey(client)).ToArray();
                messageType = ChatMessageType.Lobby;
            }
        }

        if (TryHandleChatCommand(
                sender, request, recipients, channelRecipients, messageType))
        {
            return;
        }

        ChatMessage chat = Players(sender).Read(profile =>
            ChatMessage.FromPlayer(
                profile.Nickname,
                profile.AccountClass,
                request.EncodedText,
                messageType));

        // Colour comes from the type byte alone (sub_42AE9C): 0/1 draw light blue, 4 draws
        // pink, 6/7/8 gold. Give the author their own line in a different colour so they
        // can pick it out, and send everyone else the ordinary lobby/room type.
        Packet response = OnChatInfPacket.Build(chat);
        Send(recipients.Where(client => client != sender), response);
        sender.Send(OnChatInfPacket.Build(chat with { Type = SelfChatType }));
    }

    /// <summary>
    /// The type the author's own copy of a chat line is sent as. Type 4 is the one
    /// remaining colour (pink) that is neither the ordinary lobby/room blue nor the gold
    /// the GM and whisper forms use, and unlike 0/1/6/7 the client does not re-parse a
    /// leading nickname out of it - it prints the line exactly as built.
    /// </summary>
    private const ChatMessageType SelfChatType = ChatMessageType.Alternate;

    public void RelayWhisper(Client sender, WhisperRequest request)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.TargetNickname) ||
            request.EncodedText.Length == 0)
        {
            return;
        }

        (string senderNickname, RosterUser? rosterTarget) = Players(sender).Read(profile => (
            profile.Nickname,
            profile.Roster.FirstOrDefault(user => string.Equals(
                user.Nickname, request.TargetNickname, StringComparison.OrdinalIgnoreCase))));

        bool targetIsSelf = request.TargetNickname.Equals(
            senderNickname, StringComparison.OrdinalIgnoreCase);
        Client[] targets;
        lock (_lock)
        {
            targets = _clients.Where(client =>
                    client != sender &&
                    string.Equals(
                        Players(client).Profile.Nickname,
                        request.TargetNickname,
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        if (!targetIsSelf && rosterTarget == null && targets.Length == 0)
        {
            SendSystemChat(sender,
                $"Whisper target not found: {request.TargetNickname}");
            return;
        }

        // The author's own copy. sub_4324D0 renders anything that is not direction 8
        // through WCHAT2 ("[Whisper]%s"), showing the line exactly as sent - so this
        // carries the ADDRESSEE's nickname.
        sender.Send(OnWChatInfPacket.Build(
            WhisperDirection.Sent,
            PrefixWhisper(request.TargetNickname, request.EncodedText)));

        // The addressee's copy. Direction 8 takes the WCHAT1 branch, which splits the
        // leading nickname off and runs it through the GM-name table - so this one has to
        // carry the AUTHOR's nickname, not the target's.
        byte[] delivered = PrefixWhisper(senderNickname, request.EncodedText);
        if (targets.Length != 0)
        {
            Send(targets, OnWChatInfPacket.Build(WhisperDirection.Received, delivered));
        }
        else if (rosterTarget != null && rosterTarget.Online)
        {
            // Answer for the stand-in, the same way messenger lines are answered, so the
            // received form can be seen at all with one account connected.
            sender.Send(OnWChatInfPacket.Build(
                WhisperDirection.Received,
                PrefixWhisper(rosterTarget.Nickname, request.EncodedText)));
        }
    }

    /// <summary>
    /// Relays a DJ메신저 conversation line. The client sends this on 0xFA, which it cannot
    /// itself receive, so the server retransmits it to the addressed user on 0xFB.
    /// </summary>
    public void RelayMessengerChat(Client sender, MessengerChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(message);
        if (message.EncodedText.Length == 0)
        {
            return;
        }

        // PROVEN by the client's own sender sub_48C176:
        //     sub_431FC0(net, text, net+794096, dword_55E898[tab])
        // which lands as +7 = the author's own id and +11 = the OPEN TAB's user id, i.e.
        // the recipient. So the addressee is +11 (TargetUserId) and +7 is the author.
        // Routing on +7 sends the line straight back to whoever wrote it.
        // Read the author's id from the SAME source the contact ids come from - the stored
        // profile. Taking it from Client.UserId (the assigned connection id) mixed two id
        // spaces and made the author spuriously equal the addressee, which routed the line
        // back to the sender: the "it just echoes" bug.
        uint senderUserId = WireId(sender);
        uint addressee = message.TargetUserId;

        RosterUser? rosterTarget = Players(sender).Read(profile =>
            profile.Roster.FirstOrDefault(user => user.UserId == addressee));

        // A roster stand-in has no socket, so nothing can deliver its side of the
        // conversation. Answer for it instead: this is the only way to see a line arrive
        // from an id that is not your own, which is the half of 0xFB that self-messaging
        // cannot exercise.
        if (rosterTarget != null)
        {
            if (rosterTarget.Online)
            {
                SendRosterReply(sender, rosterTarget, message);
            }
            return;
        }

        // Delivery goes to every socket holding the addressed user id, and NOT to the
        // author - their own window already shows the line (sub_48C176 appends it locally
        // before sending). Writing to YOURSELF is the one case where the author is also
        // the recipient, so the line legitimately comes back and appears twice.
        Client[] connected;
        lock (_lock)
        {
            connected = _clients.ToArray();
        }

        // Match against BOTH id spaces: Client.UserId is the assigned connection id, while
        // contact ids come from the stored profile.
        bool Holds(Client candidate) =>
            candidate.UserId == addressee ||
            WireId(candidate) == addressee;

        // SOMEONE ELSE always wins. Clients that never got their own player store share
        // one fallback profile, so several sockets can answer to the same id - and if the
        // sender is among them, delivering to "everyone holding the id" hands the line
        // straight back to the author. Only fall back to the sender when they are the sole
        // holder, which is a genuine note-to-self.
        Client[] targets = connected
            .Where(candidate => candidate != sender && Holds(candidate))
            .ToArray();
        bool toSelf = false;
        if (targets.Length == 0)
        {
            if (!Holds(sender))
            {
                SendSystemChat(sender, $"Messenger target is not online: user {addressee}");
                Logger.Info(sender,
                    $"Messenger line from {senderUserId} to {addressee} undeliverable; " +
                    $"connected ids [{string.Join(",", connected.Select(DescribeIds))}].");
                return;
            }
            toSelf = true;
            targets = [sender];
        }

        // A line may only pass between MUTUAL contacts. Presence already hides a one-sided
        // contact, but that is a display gate - the delivery itself has to refuse too, or
        // a stale tab (or a friend removed mid-session) still lands a message in a
        // conversation the other side has no record of, which crashes it.
        if (!toSelf)
        {
            string senderAccount = Players(sender).Read(profile => profile.AccountId);
            targets = targets.Where(target => Players(target).Read(profile =>
                profile.Messenger.Contacts.Any(entry => string.Equals(
                    entry.AccountId, senderAccount, StringComparison.OrdinalIgnoreCase))))
                .ToArray();
            if (targets.Length == 0)
            {
                SendSystemChat(sender,
                    "[SYSTEM] They have not added you back, so private messages " +
                    "cannot be delivered.");
                return;
            }
        }

        Logger.Info(sender,
            $"Messenger line from {senderUserId} to {addressee} -> " +
            $"{(toSelf ? "self" : string.Join(",", targets.Select(DescribeIds)))}.");

        // +7 already holds the author, which is exactly the tab the RECIPIENT needs:
        // sub_48BD17 matches +7 against dword_55E898 (the ten open tabs) and opens a new
        // one on no match. Forward it unchanged.
        Send(targets, OnMsgChatInfPacket.Build(message with
        {
            EncodedText = OnMsgChatInfPacket.Clamp(message.EncodedText)
        }));
    }

    /// <summary>
    /// Answers on a roster stand-in's behalf. The client formats its own outgoing lines as
    /// "&lt;nick&gt; &gt; &lt;text&gt;", so the reply is built the same way to look native, and it is
    /// sent with the stand-in's id at +7 so sub_48BD17 files it under that conversation.
    /// </summary>
    private void SendRosterReply(
        Client sender,
        RosterUser roster,
        MessengerChatMessage incoming)
    {
        // Echo back what was said, so it is obvious which line triggered which reply.
        string received = OnMsgChatInfPacket.TextEncoding
            .GetString(incoming.EncodedText);
        int arrow = received.IndexOf('>');
        string body = arrow >= 0 && arrow + 1 < received.Length
            ? received[(arrow + 1)..].Trim()
            : received;

        byte[] text = OnMsgChatInfPacket.Clamp(
            OnMsgChatInfPacket.TextEncoding.GetBytes($"{roster.Nickname} > {body}"));
        sender.Send(OnMsgChatInfPacket.Build(new MessengerChatMessage(
            SenderUserId: roster.UserId,
            TargetUserId: incoming.SenderUserId,
            EncodedText: text)));
    }

    private bool TryHandleChatCommand(
        Client sender,
        LobbyChatRequest request,
        Client[] recipients,
        Client[] channelRecipients,
        ChatMessageType scopeType)
    {
        // PERMISSIONS. One gate for every operator command. Previously each carried its
        // own check and most carried none, so ordinary players could fire /notice,
        // /alert and /bignews at the whole server.
        bool isAdmin = AccountClassInfo.IsAdmin(
            Players(sender).Read(profile => profile.AccountClass));

        if (TryReadCommandArgument(request, "/help", out _) ||
            TryReadCommandArgument(request, "/chathelp", out _))
        {
            SendChatHelp(sender, isAdmin);
            return true;
        }

        if (IsAdminCommand(request) && !isAdmin)
        {
            Logger.Info(sender, "Denied an operator command from a normal account.");
            SendSystemChat(sender, "That command requires administrator access.");
            return true;
        }

        if (TryReadCommandArgument(request, "/notice", out byte[] notice))
        {
            return SendSpecialChat(
                sender, recipients, ChatMessageType.Notice, notice, "/notice <message>");
        }

        if (TryReadCommandArgument(request, "/alert", out byte[] alert))
        {
            return SendSpecialChat(
                sender, recipients, ChatMessageType.Alert, alert, "/alert <message>");
        }

        if (TryReadCommandArgument(request, "/bignews", out byte[] bigNews))
        {
            SendBigNews(sender, channelRecipients, bigNews);
            return true;
        }

        if (TryReadCommandArgument(request, "/grant", out byte[] grant))
        {
            RunGrant(sender, System.Text.Encoding.ASCII.GetString(grant));
            return true;
        }

        foreach ((string verb, AccountLockState lockState) in InGameLockCommands)
        {
            if (!TryReadCommandArgument(request, verb, out byte[] lockArgs))
            {
                continue;
            }

            RunAccountLock(sender, verb, lockState,
                System.Text.Encoding.ASCII.GetString(lockArgs));
            return true;
        }

        if (TryReadCommandArgument(request, "/invite", out byte[] invite))
        {
            RunInvite(sender, invite);
            return true;
        }

        if (!TryReadCommandArgument(request, "/chatfx", out byte[] arguments))
        {
            return false;
        }

        int separator = Array.IndexOf(arguments, (byte)' ');
        if (separator <= 0 ||
            !byte.TryParse(
                Encoding.ASCII.GetString(arguments, 0, separator),
                out byte typeValue) ||
            typeValue > (byte)ChatMessageType.Styled8)
        {
            SendSystemChat(sender, "Usage: /chatfx <0-8> <message>");
            return true;
        }

        int messageStart = separator + 1;
        while (messageStart < arguments.Length && arguments[messageStart] == (byte)' ')
        {
            messageStart++;
        }
        if (messageStart == arguments.Length)
        {
            SendSystemChat(sender, "Usage: /chatfx <0-8> <message>");
            return true;
        }

        byte[] message = arguments[messageStart..];
        Packet response = OnChatInfPacket.Build(new ChatMessage(
            (ChatMessageType)typeValue, message, IsNullTerminated: false));
        Send(recipients, response);
        return true;
    }

    private static void SendBigNews(
        Client sender,
        Client[] recipients,
        byte[] arguments)
    {
        string value = Encoding.ASCII.GetString(arguments);
        int separator = value.IndexOf('|');
        string title;
        string body;
        if (separator < 0)
        {
            title = string.Empty;
            body = value.Trim();
        }
        else
        {
            title = value[..separator].Trim();
            body = value[(separator + 1)..].Trim();
        }

        if (body.Length == 0)
        {
            SendSystemChat(sender,
                "Usage: /bignews <message> or /bignews <title>|<message>");
            return;
        }
        if (Encoding.ASCII.GetByteCount(title) >= OnBigNewsInfPacket.TitleSize ||
            Encoding.ASCII.GetByteCount(body) >= OnBigNewsInfPacket.BodySize)
        {
            SendSystemChat(sender,
                "Big news is limited to 80 title bytes and 256 message bytes.");
            return;
        }

        Send(recipients, OnBigNewsInfPacket.Build(body, title));
    }

    private static bool SendSpecialChat(
        Client sender,
        Client[] recipients,
        ChatMessageType type,
        byte[] message,
        string usage)
    {
        if (message.Length == 0)
        {
            SendSystemChat(sender, $"Usage: {usage}");
            return true;
        }

        Packet response = OnChatInfPacket.Build(new ChatMessage(
            type, message, IsNullTerminated: false));
        Send(recipients, response);
        return true;
    }

    private static bool TryReadCommandArgument(
        LobbyChatRequest request,
        string command,
        out byte[] argument)
    {
        string text = request.AsciiText;
        if (text.Equals(command, StringComparison.OrdinalIgnoreCase))
        {
            argument = [];
            return true;
        }
        if (text.Length <= command.Length ||
            text[command.Length] != ' ' ||
            !text.StartsWith(command, StringComparison.OrdinalIgnoreCase))
        {
            argument = [];
            return false;
        }

        int start = command.Length + 1;
        while (start < request.EncodedText.Length &&
               request.EncodedText[start] == (byte)' ')
        {
            start++;
        }
        argument = request.EncodedText[start..];
        return true;
    }

    /// <summary>Commands only an operator may run. /help and /invite are not here.</summary>
    private static readonly string[] AdminCommands =
    [
        "/notice", "/alert", "/bignews", "/chatfx", "/grant",
        "/ban", "/suspend", "/unban"
    ];

    private static bool IsAdminCommand(LobbyChatRequest request) =>
        AdminCommands.Any(verb => TryReadCommandArgument(request, verb, out _));

    /// <summary>
    /// /help. An ordinary player is shown only what they can actually run - listing
    /// operator commands to everyone advertises them and invites the attempts.
    /// </summary>
    private static void SendChatHelp(Client client, bool isAdmin)
    {
        SendSystemChat(client,
            "/invite <nickname> - invite a player in this channel to your room");
        SendSystemChat(client, "/help - show this list");
        if (!isAdmin)
        {
            return;
        }

        SendSystemChat(client, "--- operator ---");
        SendSystemChat(client, "/notice <text> - cyan 30-second notice");
        SendSystemChat(client, "/alert <text> - yellow 30-second alert");
        SendSystemChat(client,
            "/bignews <message> or <title>|<message> - channel announcement");
        SendSystemChat(client, "/chatfx <0-8> <text> - chat display mode");
        SendSystemChat(client,
            "/grant <nickname> <max|cash|exp|level|item> <amount> [count]");
        SendSystemChat(client, "/ban <nickname> <reason> - lock an account and kick it");
        SendSystemChat(client, "/suspend <nickname> <reason> - lock as 'under review'");
        SendSystemChat(client, "/unban <nickname> - let them back in");
    }

    /// <summary>
    /// /invite &lt;nickname&gt;. The nickname is whatever the client typed, so it is read with
    /// the chat encoding rather than ASCII - a Korean nickname would otherwise never match.
    /// </summary>
    private void RunInvite(Client sender, byte[] arguments)
    {
        string nickname = OnMsgChatInfPacket.TextEncoding.GetString(arguments).Trim();
        if (nickname.Length == 0)
        {
            SendSystemChat(sender, "Usage: /invite <nickname>");
            return;
        }

        SendSystemChat(sender, InviteByName(sender, nickname) switch
        {
            NamedRoomInviteResult.Sent => $"Invited '{nickname}' to your room.",
            NamedRoomInviteResult.InviterNotInRoom =>
                "You must be in a room before using /invite.",
            NamedRoomInviteResult.RoomInProgress =>
                "You cannot invite players while your room is playing.",
            NamedRoomInviteResult.TargetNotFound =>
                $"No player named '{nickname}' is in this channel.",
            NamedRoomInviteResult.CannotInviteSelf =>
                "You cannot invite yourself.",
            NamedRoomInviteResult.TargetAlreadyInRoom =>
                $"'{nickname}' is already in a room.",
            NamedRoomInviteResult.TargetRefusesInvitations =>
                $"'{nickname}' is refusing invitations.",
            NamedRoomInviteResult.RoomFull => "Your room is full.",
            _ => $"Could not invite '{nickname}' because the room state changed.",
        });
    }

    // Fake rooms for single-client testing of the lobby grid (0x39 add / 0x3A remove).
    // They exist only on the wire — no LocalRoomState is created — so they cannot be
    // joined; they verify that the 48-byte record renders (title, counts, state) and that
    // removal works. Indices start high so they never collide with real rooms.
    // Must stay below the advertised room count (OnRoomInfoInfPacket.MaxRoomIndex): the
    // client indexes per-room tables by 4*roomIndex, so a higher index corrupts the grid
    // instead of just adding a row. Sits high enough to avoid the real rooms, which are
    // allocated from 0 upward.
    private const ushort TestRoomFirstIndex = 160;
    private const int TestRoomCount = 24;

    private const int DefaultAccountClassFuzzDelayMs = 500;
    private const int MinimumAccountClassFuzzDelayMs = 1;
    private const int MaximumAccountClassFuzzDelayMs = 10_000;
    private const ulong MaximumAccountClassFuzzRange = 65_536;

    private void RunAccountClassFuzz(Client sender, string[] parts)
    {
        string mode = parts.Length > 2 ? parts[2].ToLowerInvariant() : "bits";
        if (mode == "stop")
        {
            StopAccountClassFuzz(sender, announce: true);
            return;
        }

        IEnumerable<uint> values;
        ulong count;
        int delayArgument;
        switch (mode)
        {
            case "bits":
            case "bit":
                values = EnumerateAccountClassBits();
                count = 33; // zero plus each of the 32 individual bits
                delayArgument = 3;
                break;
            case "byte":
            case "raw":
                values = EnumerateUIntRange(0, byte.MaxValue);
                count = byte.MaxValue + 1UL;
                delayArgument = 3;
                break;
            case "known":
            case "all":
                int knownBitCount = BitOperations.PopCount(AccountClassInfo.KnownMask);
                count = 1UL << knownBitCount;
                values = EnumerateKnownAccountClassCombinations();
                delayArgument = 3;
                break;
            case "range":
                if (!TryParseUInt(parts, 3, out uint start) ||
                    !TryParseUInt(parts, 4, out uint end) ||
                    end < start)
                {
                    SendSystemChat(sender,
                        "Usage: /packettest class fuzz range <start> <end> [delayMs]");
                    return;
                }
                count = (ulong)end - start + 1;
                if (count > MaximumAccountClassFuzzRange)
                {
                    SendSystemChat(sender,
                        $"A fuzz range is limited to {MaximumAccountClassFuzzRange:N0} " +
                        "values per run; split the uint range into chunks.");
                    return;
                }
                values = EnumerateUIntRange(start, end);
                delayArgument = 5;
                break;
            default:
                SendAccountClassFuzzHelp(sender);
                return;
        }

        int delayMs = DefaultAccountClassFuzzDelayMs;
        if (parts.Length > delayArgument)
        {
            if (!TryParseUInt(parts, delayArgument, out uint parsedDelay) ||
                parsedDelay < MinimumAccountClassFuzzDelayMs ||
                parsedDelay > MaximumAccountClassFuzzDelayMs)
            {
                SendSystemChat(sender,
                    $"Fuzz delay must be {MinimumAccountClassFuzzDelayMs}-" +
                    $"{MaximumAccountClassFuzzDelayMs} ms.");
                return;
            }
            delayMs = (int)parsedDelay;
        }

        CancellationTokenSource cancellation = new();
        CancellationTokenSource? previous;
        lock (_lock)
        {
            _accountClassFuzzers.Remove(sender, out previous);
            _accountClassFuzzers[sender] = cancellation;
        }
        previous?.Cancel();

        SendSystemChat(sender,
            $"Account-class fuzz '{mode}' started: {count:N0} values, {delayMs} ms each. " +
            "Use /packettest class fuzz stop to cancel.");
        Logger.Info(
            $"Account-class fuzz '{mode}' started for {sender.Identity}: " +
            $"{count} values at {delayMs} ms.");
        _ = Task.Run(() => RunAccountClassFuzzAsync(
            sender, mode, values, count, delayMs, cancellation));
    }

    private async Task RunAccountClassFuzzAsync(
        Client sender,
        string mode,
        IEnumerable<uint> values,
        ulong count,
        int delayMs,
        CancellationTokenSource cancellation)
    {
        ulong step = 0;
        bool completed = false;
        try
        {
            foreach (uint value in values)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                step++;
                SendAccountClassProbe(sender, value);
                Logger.Info(
                    $"Account-class fuzz '{mode}' {sender.Identity}: " +
                    $"{step}/{count} = {value} (0x{value:X8}).");

                // Correlate every one-bit probe with the visual result. Larger
                // sweeps report periodically so chat itself does not become the test.
                if (count <= 64 || step == 1 || step == count || step % 64 == 0)
                {
                    SendSystemChat(sender,
                        $"class fuzz {step:N0}/{count:N0}: {value} (0x{value:X8})");
                }

                if (step != count)
                {
                    await Task.Delay(delayMs, cancellation.Token).ConfigureAwait(false);
                }
            }
            completed = true;
        }
        catch (OperationCanceledException)
        {
            // Expected when replaced, explicitly stopped, or the client leaves.
        }
        catch (Exception ex)
        {
            Logger.Exception(sender, ex);
        }
        finally
        {
            bool ownsCurrentSession;
            lock (_lock)
            {
                ownsCurrentSession =
                    _accountClassFuzzers.TryGetValue(sender, out CancellationTokenSource? current) &&
                    ReferenceEquals(current, cancellation);
                if (ownsCurrentSession)
                {
                    _accountClassFuzzers.Remove(sender);
                }
            }

            if (ownsCurrentSession)
            {
                uint persisted = Players(sender).Read(profile => profile.AccountClass);
                SendAccountClassProbe(sender, persisted);
                SendSystemChat(sender,
                    completed
                        ? $"Account-class fuzz complete; restored 0x{persisted:X8}."
                        : $"Account-class fuzz stopped; restored 0x{persisted:X8}.");
            }
            cancellation.Dispose();
        }
    }

    private void StopAccountClassFuzz(Client sender, bool announce)
    {
        CancellationTokenSource? cancellation;
        lock (_lock)
        {
            _accountClassFuzzers.Remove(sender, out cancellation);
        }
        if (cancellation == null)
        {
            if (announce)
            {
                SendSystemChat(sender, "No account-class fuzz is running.");
            }
            return;
        }

        cancellation.Cancel();
        uint persisted = Players(sender).Read(profile => profile.AccountClass);
        SendAccountClassProbe(sender, persisted);
        if (announce)
        {
            SendSystemChat(sender,
                $"Account-class fuzz stopped; restored 0x{persisted:X8}.");
        }
    }

    private void SendAccountClassProbe(Client sender, uint value)
    {
        uint userId = WireId(sender);
        sender.Send(OnUpdateUserAccountClassInfPacket.Build(userId, value));
    }

    private static IEnumerable<uint> EnumerateAccountClassBits()
    {
        yield return 0;
        for (int bit = 0; bit < 32; bit++)
        {
            yield return 1u << bit;
        }
    }

    private static IEnumerable<uint> EnumerateKnownAccountClassCombinations()
    {
        int[] bits = Enumerable.Range(0, 32)
            .Where(bit => (AccountClassInfo.KnownMask & (1u << bit)) != 0)
            .ToArray();
        uint combinations = 1u << bits.Length;
        for (uint combination = 0; combination < combinations; combination++)
        {
            uint value = 0;
            for (int index = 0; index < bits.Length; index++)
            {
                if ((combination & (1u << index)) != 0)
                {
                    value |= 1u << bits[index];
                }
            }
            yield return value;
        }
    }

    private static IEnumerable<uint> EnumerateUIntRange(uint start, uint end)
    {
        for (uint value = start;; value++)
        {
            yield return value;
            if (value == end)
            {
                yield break;
            }
        }
    }

    private static void SendAccountClassFuzzHelp(Client sender)
    {
        SendSystemChat(sender,
            "/packettest class fuzz bits [delayMs] - zero and every individual uint bit");
        SendSystemChat(sender,
            "/packettest class fuzz byte [delayMs] - every raw value from 0 through 255");
        SendSystemChat(sender,
            "/packettest class fuzz known [delayMs] - all 2,048 known-flag combinations");
        SendSystemChat(sender,
            "/packettest class fuzz range <start> <end> [delayMs] | fuzz stop");
    }

    private void ApplyAccountClass(Client sender, uint value)
    {
        StopAccountClassFuzz(sender, announce: false);
        (uint UserId, uint AccountClass) updated = Players(sender).Update(profile =>
        {
            profile.AccountClass = value;
            return (profile.WireUserId, profile.AccountClass);
        });

        // 0x2E is the dedicated account-class update. Waiter/room rows are refreshed
        // afterwards so both the local card and other lobby views see the same mask.
        Client[] clients;
        lock (_lock)
        {
            clients = _clients.ToArray();
        }
        Send(clients, OnUpdateUserAccountClassInfPacket.Build(
            updated.UserId, updated.AccountClass));
        BroadcastLocalProfile(sender);

        uint unknownBits = value & ~AccountClassInfo.KnownMask;
        SendSystemChat(sender,
            $"accountClass = {value} (0x{value:X}) = {AccountClassInfo.Format(value)}; " +
            $"premium={(AccountClassInfo.IsPremium(value) ? "yes" : "no")}, " +
            $"staff={(AccountClassInfo.IsPrivileged(value) ? "yes" : "no")}; " +
            $"icon elements: {AccountClassInfo.DescribeBadges(value)}" +
            (unknownBits != 0 ? $"; UNMAPPED 0x{unknownBits:X}" : string.Empty) +
            ". Re-enter the lobby to refresh the profile card.");
    }

    // TextStock.ini EXTRATYPENAME1..5, resolved by the client through sub_42A044.
    private static string ModeName(byte mode) => mode switch
    {
        0 => "Free",
        1 => "Ranking",
        2 => "Score Battle",
        3 => "Item Battle",
        4 => "Course",
        _ => "?"
    };

    private RoomListEntry TestRoom(
        ushort index,
        string title,
        byte memberCount,
        byte matchMode = OnRoomInfoInfPacket.ScoreBattleMode)
    {
        byte[] titleField = new byte[CreateRoomReqPacket.TitleSize];
        // Leave at least one trailing zero: the client reads the title as a C string.
        byte[] encoded = Encoding.ASCII.GetBytes(title);
        encoded.AsSpan(0, Math.Min(encoded.Length, titleField.Length - 1))
            .CopyTo(titleField);
        return new RoomListEntry(
            RoomIndex: index,
            TitleField: titleField,
            TitlePadding: 0,
            Capacity: LocalRoomProtocol.MaximumSlots,
            MemberCount: memberCount,
            Unlocked: 1,
            Category: OnRoomInfoInfPacket.IsBattleMode(matchMode)
                ? OnRoomInfoInfPacket.NormalRoomCategory
                : (byte)0,
            LevelRestriction: 0,
            MatchMode: matchMode,
            EffectorFlag: LocalRoomProtocol.EffectsAllowed,
            Premium: 0,
            State: RoomListState.Waiting,
            DiscId: 0,
            Reserved46: 0,
            Difficulty: 0);
    }

    /// <summary>
    /// Sweeps one messenger field so the value that flips the UI can be found by watching
    /// rather than guessed. Every field here is one the server owns and the client stores
    /// at a position already proven from its handlers; only the MEANING of the value is
    /// unknown, which is exactly what a sweep resolves.
    /// </summary>
    private void StartMessengerFuzz(Client sender, string[] parts)
    {
        string field = parts.Length > 2 ? parts[2].ToLowerInvariant() : "help";
        if (field == "stop")
        {
            StopMessengerFuzz(sender, announce: true);
            return;
        }

        if (field is not ("slot" or "status" or "unknown52" or "gender"))
        {
            SendSystemChat(sender,
                "/packettest friend fuzz slot|status|unknown52|gender [max] [ms]");
            SendSystemChat(sender,
                "slot = contact record+4; status = OnMsgNotifyInf value (contact+6);");
            SendSystemChat(sender,
                "unknown52 = user record+52, the only field still unidentified;");
            SendSystemChat(sender,
                "gender = user record+62 (1 male, 0 female) - already known, for checking.");
            SendSystemChat(sender, "/packettest friend fuzz stop cancels.");
            return;
        }

        ushort max = TryParseUShort(parts, 3, 16);
        int delayMs = Math.Clamp((int)TryParseUShort(parts, 4, 1500), 200, 10000);

        CancellationTokenSource cancellation = new();
        CancellationTokenSource? previous;
        lock (_lock)
        {
            _messengerFuzzers.Remove(sender, out previous);
            _messengerFuzzers[sender] = cancellation;
        }
        previous?.Cancel();

        SendSystemChat(sender,
            $"Messenger fuzz '{field}' started: 0..{max} at {delayMs} ms. " +
            "Watch the friends panel; /packettest friend fuzz stop cancels.");
        _ = Task.Run(() => RunMessengerFuzzAsync(sender, field, max, delayMs, cancellation));
    }

    private async Task RunMessengerFuzzAsync(
        Client sender,
        string field,
        ushort max,
        int delayMs,
        CancellationTokenSource cancellation)
    {
        bool completed = false;
        try
        {
            for (ushort value = 0; value <= max; value++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                ApplyMessengerField(sender, field, value);
                SendMessengerBook(sender);
                SendSystemChat(sender, $"friend fuzz {field} = {value}");
                Logger.Info($"Messenger fuzz {field}={value} for {sender.Identity}.");

                if (value != max)
                {
                    await Task.Delay(delayMs, cancellation.Token).ConfigureAwait(false);
                }
            }
            completed = true;
        }
        catch (OperationCanceledException)
        {
            // Expected when replaced, stopped, or the client leaves.
        }
        catch (Exception ex)
        {
            Logger.Exception(sender, ex);
        }
        finally
        {
            bool owns;
            lock (_lock)
            {
                owns = _messengerFuzzers.TryGetValue(
                           sender, out CancellationTokenSource? current) &&
                       ReferenceEquals(current, cancellation);
                if (owns)
                {
                    _messengerFuzzers.Remove(sender);
                }
            }
            if (owns)
            {
                SendSystemChat(sender, completed
                    ? $"Messenger fuzz '{field}' complete; last value left applied."
                    : $"Messenger fuzz '{field}' stopped.");
            }
            cancellation.Dispose();
        }
    }

    private void ApplyMessengerField(Client sender, string field, ushort value)
    {
        if (field == "status")
        {
            _messengerStatus = value;
            return;
        }

        if (field == "slot")
        {
            // Contact record +4. sub_4378C0 pins userId at +0 and sub_437950 pins status
            // at +6, leaving this word the only unidentified part of the record - and the
            // most likely reason a row that exists server-side still does not draw.
            Players(sender).Update(profile =>
            {
                foreach (MessengerContactEntry contact in profile.Messenger.Contacts)
                {
                    contact.GroupIndex = value;
                }
                return true;
            });
            return;
        }

        Players(sender).Update(profile =>
        {
            switch (field)
            {
                // record+52 - the last unidentified field in the 67-byte user record.
                case "unknown52": profile.ProfileFlags = value; break;
                case "gender": profile.Gender = (byte)value; break;
            }
            return true;
        });
        BroadcastLocalProfile(sender);
        RefreshLocalIdentity(sender);
    }

    /// <summary>
    /// Re-sends the 67-byte user record (OnUserIdInfoAck, 0x22) for this account.
    ///
    /// BroadcastLocalProfile only refreshes the waiter row and the room-member row; the
    /// messenger reads this record instead, and the client caches it from whenever it last
    /// asked. Without this, changing State or any of the opaque property fields is
    /// invisible - which is why sweeping them appeared to do nothing.
    /// </summary>
    private void RefreshLocalIdentity(Client client)
    {
        uint userId = WireId(client);
        SendIdentityAck(client, [userId]);
    }

    private void StopMessengerFuzz(Client sender, bool announce)
    {
        CancellationTokenSource? cancellation;
        lock (_lock)
        {
            _messengerFuzzers.Remove(sender, out cancellation);
        }
        cancellation?.Cancel();
        if (announce && cancellation == null)
        {
            SendSystemChat(sender, "No messenger fuzz is running.");
        }
    }

    private string MessengerSummary(Client client) =>
        Players(client).Read(profile =>
        {
            string contacts = profile.Messenger.Contacts.Count == 0
                ? "none"
                : string.Join(", ", profile.Messenger.Contacts
                    .Select(contact => $"{contact.UserId}(g{contact.GroupIndex})"));
            string blocked = profile.Messenger.BlockedUserIds.Count == 0
                ? "none"
                : string.Join(", ", profile.Messenger.BlockedUserIds);
            string groups = string.Join(", ", profile.Messenger.GroupNames
                .Select((name, index) => $"{index}:'{name}'"));
            return $"Contacts: {contacts} | Blocked: {blocked} | Groups: {groups} | " +
                   $"presenceStatus={_messengerStatus} userState={profile.State}";
        });

    /// <summary>
    /// Runs the expiry sweep and announces whatever went. Both notifications also carry a
    /// fresh inventory block, so the client's box and loadout stay correct without a
    /// separate refresh.
    /// </summary>
    public void SendExpiredItems(Client client, bool announce)
    {
        ArgumentNullException.ThrowIfNull(client);
        (IReadOnlyList<TimedInventoryItem> box, IReadOnlyList<TimedInventoryItem> mount) =
            Players(client).ExpireItems(DateTimeOffset.UtcNow);

        if (box.Count == 0 && mount.Count == 0)
        {
            if (announce)
            {
                SendSystemChat(client, "No timed items have expired.");
            }
            return;
        }

        // The client loops exactly eight records per notification, so a large sweep is
        // announced in batches rather than truncated.
        foreach (TimedInventoryItem[] batch in Batches(mount))
        {
            client.Send(OnExpiredMountItemInfPacket.Build(
                batch, Players(client).Read(profile => profile.Inventory.MountLoadout())));
        }
        foreach (TimedInventoryItem[] batch in Batches(box))
        {
            client.Send(OnExpiredShopItemInfPacket.Build(
                batch, Players(client).Read(profile => profile.Inventory.ItemBoxItems())));
        }

        Logger.Info(client,
            $"Expired {box.Count} item-box and {mount.Count} mounted item(s).");
        if (announce)
        {
            SendSystemChat(client,
                $"Expired {box.Count} item-box and {mount.Count} mounted item(s).");
        }
    }

    private static IEnumerable<TimedInventoryItem[]> Batches(
        IReadOnlyList<TimedInventoryItem> items)
    {
        for (int offset = 0; offset < items.Count;
             offset += ExpiredItemProtocol.RecordCount)
        {
            yield return items
                .Skip(offset)
                .Take(ExpiredItemProtocol.RecordCount)
                .ToArray();
        }
    }

    /// <summary>Re-sends all four inventory blocks the client caches.</summary>
    private void SendInventoryRefresh(Client client)
    {
        (uint userId, IReadOnlyList<TimedInventoryItem> box) =
            Players(client).Read(profile => (
                profile.WireUserId, profile.Inventory.ItemBoxItems()));

        // The mount block is 8 fixed slots; equipped entries first, the rest empty.
        TimedInventoryItem[] mount = new TimedInventoryItem[
            OnInventoryInfoInfPacket.MountItemCount];
        IReadOnlyList<TimedInventorySlot> equipped =
            Players(client).Read(profile => profile.Inventory.MountItems.ToArray());
        for (int i = 0; i < mount.Length; i++)
        {
            mount[i] = i < equipped.Count
                ? new TimedInventoryItem(equipped[i].ItemId, equipped[i].Expiration)
                : new TimedInventoryItem(0xFFFFFFFF, 0xFFFFFFFF);
        }

        client.Send(OnUpdateUserInventoryShopItemInfPacket.BuildItemBox(userId, box));
        client.Send(OnUpdateUserInventoryMountItemInfPacket.Build(userId, mount));
    }

    /// <summary>
    /// Replaces the Korean client's live 48-slot PRIZE/COLLECTION cache. OnLogInAck
    /// populates the same memory at login; without this incremental 0x2A update, newly
    /// earned accuracy and course discs remain invisible until the next login.
    /// </summary>
    private void SendCollectionRefresh(Client client)
    {
        (uint userId, IReadOnlyList<CollectionEntry> collection) =
            Players(client).Read(profile => (
                profile.WireUserId, profile.Collection.ToArray()));
        client.Send(OnUpdateUserInventoryDefaultItemInfPacket.BuildKorean(
            userId, collection));
    }

    /// <summary>
    /// Pushes the persisted friends list, blocked users and group names, then a presence
    /// notification for every contact that is actually online. The client renders its
    /// messenger purely from these three sections, so they are sent together.
    /// </summary>
    /// <summary>
    /// Tells the person who was just added that they have to add back. Presence is gated
    /// on a MUTUAL contact, so until they do, the adder shows offline to them and private
    /// messages cannot be exchanged - without this the requirement is invisible.
    /// </summary>
    public void AnnounceFriendRequest(Client adder, uint addedUserId)
    {
        ArgumentNullException.ThrowIfNull(adder);
        string adderNickname = Players(adder).Read(profile => profile.Nickname);
        foreach (Client peer in ConnectedPeers())
        {
            if (peer == adder || WireId(peer) != addedUserId)
            {
                continue;
            }
            SendSystemChat(peer,
                $"[SYSTEM] {adderNickname} has added you as a friend, " +
                "you must add them back to use private messages!");
            return;
        }
    }

    /// <summary>
    /// Re-points every saved contact at its owner's CURRENT session id. Contacts are
    /// stored against the stable account id because session ids are randomised per login,
    /// so a list saved by id alone would point at nobody - or at whoever happened to
    /// inherit that number - next session.
    /// </summary>
    private void RefreshContactIds(Client client)
    {
        Dictionary<string, uint> live = new(StringComparer.OrdinalIgnoreCase);
        Client[] connected;
        lock (_lock)
        {
            connected = _clients.ToArray();
        }
        foreach (Client peer in connected)
        {
            (string accountId, uint wireId) = Players(peer).Read(
                profile => (profile.AccountId, profile.WireUserId));
            if (!string.IsNullOrEmpty(accountId))
            {
                live[accountId] = wireId;
            }
        }

        Players(client).Update(profile =>
        {
            foreach (MessengerContactEntry contact in profile.Messenger.Contacts)
            {
                if (!string.IsNullOrEmpty(contact.AccountId) &&
                    live.TryGetValue(contact.AccountId, out uint current))
                {
                    contact.UserId = current;
                }
            }
            return 0;
        });
    }

    /// <summary>
    /// Both id spaces for every connected client - the server-assigned connection id and
    /// the stored profile id - since a contact entry may hold either.
    /// </summary>
    private Client[] ConnectedPeers()
    {
        lock (_lock)
        {
            return _clients.ToArray();
        }
    }

    private HashSet<uint> ConnectedUserIds()
    {
        Client[] connected;
        lock (_lock)
        {
            connected = _clients.ToArray();
        }

        HashSet<uint> ids = [];
        foreach (Client peer in connected)
        {
            if (peer.UserId is uint assigned)
            {
                ids.Add(assigned);
            }
            ids.Add(WireId(peer));
        }
        return ids;
    }

    /// <summary>
    /// Re-sends the messenger book to everyone EXCEPT the given client, so their friend
    /// lists pick up a presence change. Someone logging out has no way to tell their
    /// contacts themselves, so the lists stayed showing them online until relogin.
    /// </summary>
    public void RefreshMessengerPresence(Client changed)
    {
        Client[] peers;
        lock (_lock)
        {
            peers = _clients.Where(peer => peer != changed).ToArray();
        }
        foreach (Client peer in peers)
        {
            SendMessengerBook(peer);
        }
    }

    public void SendMessengerBook(Client client)
    {
        ArgumentNullException.ThrowIfNull(client);
        RefreshContactIds(client);
        (MessengerContact[] contacts, MessengerContact[] blocked, string[] groups,
            HashSet<uint> presentIds) =
            Players(client).Read(profile => (
                profile.Messenger.ContactSlots(),
                profile.Messenger.BlockedSlots(),
                profile.Messenger.GroupSlots(),
                // The real player is always present; roster stand-ins are present when
                // their entry says so, which is what lets an offline contact be tested.
                new HashSet<uint>(profile.Roster
                    .Where(user => user.Online)
                    .Select(user => user.UserId))
                { profile.WireUserId }));

        // Everyone actually connected is present too. Without this the set held only the
        // caller and their roster stand-ins, so a real second player showed offline the
        // whole time they were logged in - and, since nothing ever marked them present,
        // leaving the channel could not change anything either.
        //
        // MUTUAL ONLY: a one-sided contact must stay offline. The client will open a
        // conversation with anyone it sees as online, and the other side has no record of
        // the tab, which crashes it. Presence is the gate that keeps that unreachable.
        string callerAccount = Players(client).Read(profile => profile.AccountId);
        foreach (Client peer in ConnectedPeers())
        {
            (uint peerWireId, bool mutual) = Players(peer).Read(profile => (
                profile.WireUserId,
                profile.Messenger.Contacts.Any(entry => string.Equals(
                    entry.AccountId, callerAccount, StringComparison.OrdinalIgnoreCase))));
            if (mutual || peer == client)
            {
                presentIds.Add(peerWireId);
            }
        }

        // The status has to be stamped into the list itself: sub_437AA0 memcpy's all 480
        // bytes of OnMsgRegUserInf straight over the contact array, so a list that says
        // zero leaves every row offline until some later notify overwrites it - and the
        // panel is built from the list.
        for (int i = 0; i < contacts.Length; i++)
        {
            if (contacts[i].UserId == 0)
            {
                continue;
            }
            // Stamp BOTH states. Only setting the online value left a contact who logged
            // out carrying whatever status they last had, so they stayed online forever.
            contacts[i] = contacts[i] with
            {
                Status = presentIds.Contains(contacts[i].UserId)
                    ? _messengerStatus
                    : OfflineMessengerStatus
            };
        }

        // Every row is built from the 67-byte user record, NOT from the contact list:
        // sub_48AB44 and sub_48BE29 skip any entry whose sub_433B80(userId) lookup returns
        // null, so a client that has not been told who these ids are draws an empty
        // messenger even though the list arrived intact.
        uint[] known = contacts.Concat(blocked)
            .Select(entry => entry.UserId)
            .Where(userId => userId != 0)
            .Distinct()
            .ToArray();
        if (_pushMessengerRecords && known.Length != 0)
        {
            SendIdentityAck(client, known);
        }

        client.Send(OnMsgGroupInfPacket.Build(groups));
        client.Send(OnMsgRegUserInfPacket.Build(contacts));
        client.Send(OnMsgBlkUserInfPacket.Build(blocked));

        foreach (MessengerContact contact in contacts)
        {
            if (contact.UserId != 0 && presentIds.Contains(contact.UserId))
            {
                client.Send(OnMsgNotifyInfPacket.Build(
                    new MessengerPresence(contact.UserId, _messengerStatus)));
            }
        }
    }

    /// <summary>
    /// Status value sent in OnMsgNotifyInf (0xF1) and in the contact list, which the
    /// client keeps at contact record +6. Only <see cref="MessengerPresence.Online"/>
    /// renders as present; the knob (/packettest friend status N) stays for probing.
    /// </summary>
    private ushort _messengerStatus = MessengerPresence.Online;

    /// <summary>
    /// What a contact who is NOT connected carries at record +6. Only the exact online
    /// word renders as present, so anything else reads as offline - but it has to be
    /// written explicitly, or a contact keeps the online value it was last sent.
    /// </summary>
    private const ushort OfflineMessengerStatus = 0;

    /// <summary>
    /// Whether the messenger book is preceded by the 67-byte user records its rows are
    /// built from. See <see cref="SendMessengerBook"/>; /packettest friend records 0|1.
    /// </summary>
    private bool _pushMessengerRecords = true;

    private static QuickInviteResult ParseQuickInviteResult(string[] parts)
    {
        if (parts.Length < 2)
        {
            return QuickInviteResult.NoUsersAvailable;
        }
        return parts[1].ToLowerInvariant() switch
        {
            "sent" => QuickInviteResult.Sent,
            "unable" => QuickInviteResult.UnableToInvite,
            "max" => QuickInviteResult.MaximumInvitationsExceeded,
            _ => QuickInviteResult.NoUsersAvailable
        };
    }

    private static bool TryParseUInt(
        IReadOnlyList<string> parts,
        int index,
        out uint value)
    {
        value = 0;
        if (index >= parts.Count)
        {
            return false;
        }
        string text = parts[index];
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(
                text.AsSpan(2),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out value)
            : uint.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value);
    }

    private static ushort TryParseUShort(
        IReadOnlyList<string> parts,
        int index,
        ushort fallback) =>
        TryParseUInt(parts, index, out uint parsed) && parsed <= ushort.MaxValue
            ? (ushort)parsed
            : fallback;

    private static bool TryParseByte(
        IReadOnlyList<string> parts,
        int index,
        out byte value)
    {
        value = 0;
        if (!TryParseUInt(parts, index, out uint parsed) ||
            parsed > byte.MaxValue)
        {
            return false;
        }

        value = (byte)parsed;
        return true;
    }

    private static byte[] PrefixWhisper(string nickname, byte[] message)
    {
        byte[] prefix = Encoding.ASCII.GetBytes($"{nickname} ");
        byte[] result = new byte[prefix.Length + message.Length];
        prefix.CopyTo(result, 0);
        message.CopyTo(result, prefix.Length);
        return result;
    }

    private static void SendSystemChat(Client client, string text)
    {
        client.Send(OnChatInfPacket.BuildAscii(text));
    }

    /// <summary>
    /// Colour of the chart announcement. Types 2 and 5 also throw a 30-second on-screen
    /// banner (sub_42B83D), which would cover the loading screen, so the styled
    /// history-only types are used instead. Swap this if a different shade reads better.
    /// </summary>
    public ChatMessageType ChartAnnouncementType { get; set; } = ChatMessageType.Styled7;

    /// <summary>
    /// Announces the chart everyone is about to play. The client shows the title on the
    /// loading screen but nothing else, so BPM, length and the chart's own difficulty and
    /// level only exist here.
    /// </summary>
    private void AnnounceChart(
        IReadOnlyCollection<Client> recipients,
        SongDefinition song,
        byte difficulty)
    {
        if (recipients.Count == 0)
        {
            return;
        }

        SongChartDefinition? chart = song.FindChart(
            Channel.KeyMode, (SongDifficulty)difficulty);
        string chartLabel = chart == null
            ? string.Empty
            : $" [{chart.DifficultyCode} Lv.{chart.DiscLevel}]";
        string length = song.PlayTimeSeconds > 0
            ? $" | {song.PlayTimeSeconds / 60}:{song.PlayTimeSeconds % 60:00}"
            : string.Empty;
        string bpm = song.Bpm > 0 ? $" | BPM {song.Bpm:0.##}" : string.Empty;

        // No decoration in front of the title. The music note the server used to prefix
        // drew as an empty box in the client's chat font, and DiscStock no longer carries
        // one either.
        //
        // DisplayTitle, not Title: DiscStock stores spaces as '_' and chat is drawn
        // verbatim, so the raw field reads "Y_Have_To_Follow_Me" in the chat window.
        Send(recipients, OnChatInfPacket.BuildAscii(
            $"{song.DisplayTitle}{chartLabel}{bpm}{length}", ChartAnnouncementType));
    }

    public void CreateRoom(Client client, RoomCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        // Do NOT put the channel's key mode in this byte. OnRoomDescInf byte 37 lands on
        // net+794269, and sub_4285A0 ("== 0") is not the 5-key test it looks like: making
        // it non-zero on a 7-key channel switched the client into the online-battle UI.
        // Whatever that field selects, 0 is the mode this server serves; leave it alone.
        LeaveRoom(client, sendAcknowledgement: false);

        LocalRoomState room;
        LocalRoomMember member;
        Client[] waiting;
        // A brand-new room has no sides yet, so it starts on the ladder unless it is item
        // battle (which needs the flag for its item slots). ChangeTeam re-evaluates this
        // and pushes 0x5B when someone picks a side. See GameTypeFor.
        request = request with
        {
            GameType = GameTypeFor(request.MatchMode, hasTeams: false),
            // The create dialog sends the difficulty lock as the LAST byte of the packet,
            // while every room packet the client reads carries it in the LevelRestriction
            // slot (wire 36 of OnRoomDescInf, the same field the 방옵션변경 dialog changes
            // through settings[1]). Copy it across here so joiners are told the lock
            // instead of the zero the client left in that byte - which is why a joiner's
            // room always showed FREE.
            LevelRestriction = request.DifficultyRestriction
        };

        lock (_lock)
        {
            ushort index = AllocateRoomIndex();
            room = new LocalRoomState(index, request);
            member = new LocalRoomMember(
                room,
                client,
                slot: 0,
                connectionId: RequireAssignedUserId(client),
                team: LocalRoomProtocol.InitialTeam(slot: 0, isHost: true),
                isHost: true,
                battleItemComboInterval: BattleItemComboInterval);
            room.Members.Add(member);
            _rooms.Add(index, room);
            _memberships.Add(client, member);
            waiting = WaitingClients(client);
        }

        // The Course scene snapshots the already-received 0x82 list during its
        // synchronous initialization. Re-send the server-owned availability in
        // direct response to the Course-mode CreateRoomReq, before either room
        // acknowledgement can launch that scene.
        if (request.MatchMode == 4)
        {
            client.Send(OnCourseListInfPacket.Build(AvailableCourseIds(client)));
        }

        client.Send(OnRoomDescInfPacket.Build(RoomDescriptor.FromCreate(room.Settings)));
        client.Send(OnCreateRoomAckPacket.Build(new CreateRoomResponse(
            CreateRoomResult.Success, room.Index, room.Settings)));
        client.Send(OnUpdateJoinerInfoInfPacket.Build(MemberInfo(member)));
        client.Send(OnPostJoinRoomInfPacket.Build());
        // State the host's own team explicitly; the room scene reads teams from 0x59 as
        // well as from the joiner record.
        BroadcastRoomTeams(room);
        AnnounceRoomEntry(client, waiting, room);
    }

    /// <summary>
    /// Refuses a join WITH A REASON. The result byte is what sub_441523 switches on to
    /// pick the message the player sees, so a generic code left them staring at a room
    /// they could not enter with no explanation.
    /// </summary>
    private static void RejectJoin(
        Client client,
        ushort roomIndex,
        JoinRoomResult reason = JoinRoomResult.RoomGone)
    {
        Logger.Info(client, $"Rejected join of room {roomIndex}: {reason}.");
        client.Send(OnJoinRoomAckPacket.Build(new JoinRoomResponse(
            roomIndex, reason, Slot: 0)));
    }

    public bool JoinRoom(Client client, JoinRoomRequest request)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);
        LeaveRoom(client, sendAcknowledgement: false);

        LocalRoomState room;
        LocalRoomMember member;
        LocalRoomMember[] existing;
        LocalRoomMember[] occupants;
        byte[] closedSlots;
        Client[] waiting;
        // Read the account class BEFORE taking the lobby lock: the player store has a
        // lock of its own, and reaching into it from inside this one invites a deadlock.
        bool joinerIsPremium = Players(client).Read(
            profile => AccountClassInfo.IsPremium(profile.AccountClass));
        lock (_lock)
        {
            // A refusal must still be acknowledged. The client's join handler
            // (sub_434030) only clears its pending-join state when an OnJoinRoomAck
            // arrives, so returning silently leaves it waiting forever.
            // Capacity is the OPEN slot count, not the created one: a host who closes
            // slots is reducing how many players the room takes.
            // Each refusal carries its own reason so the client shows the right message.
            if (!_rooms.TryGetValue(request.RoomIndex, out room!))
            {
                // The password re-prompt (sub_441469) re-sends the join itself, so if the
                // retry names a different index this line shows it rather than the player
                // just seeing "the stage no longer exists".
                Logger.Info(client,
                    $"Join refused: no room {request.RoomIndex}; live rooms " +
                    $"[{string.Join(", ", _rooms.Keys)}].");
                RejectJoin(client, request.RoomIndex, JoinRoomResult.RoomGone);
                return false;
            }
            if (room.Phase != RoomPlayPhase.Waiting)
            {
                RejectJoin(client, request.RoomIndex, JoinRoomResult.GameInProgress);
                return false;
            }
            if (room.OccupantCount >= room.OpenSlotCount)
            {
                RejectJoin(client, request.RoomIndex, JoinRoomResult.RoomFull);
                return false;
            }
            // A premium room is the create dialog's room-type toggle (wire 42), which is
            // also what draws the gold skin in the lobby list. It was decorative only:
            // the room advertised itself as premium and then admitted anyone.
            // Checked before the password so a player who cannot enter is told why.
            if (room.Settings.PublicFlag != 0 && !joinerIsPremium)
            {
                Logger.Info(client,
                    $"Join of premium room {request.RoomIndex} refused: " +
                    "account is not premium.");
                RejectJoin(client, request.RoomIndex, JoinRoomResult.PremiumOnly);
                return false;
            }
            if (!room.Settings.PasswordMatches(request.PasswordField))
            {
                // Routed through sub_441469, which re-prompts rather than just reporting.
                Logger.Info(client,
                    $"Join of room {request.RoomIndex} refused: password mismatch " +
                    $"(credential {request.PasswordField.Length} bytes).");
                RejectJoin(client, request.RoomIndex, JoinRoomResult.WrongPassword);
                return false;
            }

            HashSet<byte> taken = [.. room.OccupiedSlots()];
            int slot = Enumerable.Range(0, room.Settings.Capacity)
                .FirstOrDefault(index =>
                    room.SlotsEnabled[index] && !taken.Contains((byte)index),
                    -1);
            if (slot < 0)
            {
                // Every open slot is taken - the same thing the player sees as "full".
                RejectJoin(client, request.RoomIndex, JoinRoomResult.RoomFull);
                return false;
            }

            existing = room.Members.ToArray();
            // Take over as host when the room has none — otherwise a player who walks into
            // an unhosted room can never start a song (StartGame requires a host), and
            // LeaveRoom's promotion only runs when a host actually leaves.
            bool becomeHost = !room.Members.Any(value => value.IsHost);
            member = new LocalRoomMember(
                room,
                client,
                checked((byte)slot),
                RequireAssignedUserId(client),
                // A room only has sides once its host picked one, so a joiner lands on no
                // side unless team play is already open - in which case it is seated
                // opposite the host rather than left as the only sideless player.
                team: TeamForJoiner(room, checked((byte)slot)),
                isHost: becomeHost,
                battleItemComboInterval: BattleItemComboInterval);
            room.Members.Add(member);
            _memberships.Add(client, member);
            occupants = room.Members.OrderBy(value => value.Slot).ToArray();
            closedSlots = room.ClosedSlots().ToArray();
            waiting = WaitingClients(client);
            Logger.Info(client,
                $"Joined room {room.Index} in slot {slot} of {room.OpenSlotCount} open " +
                $"(closed: [{string.Join(",", closedSlots)}], " +
                $"taken: [{string.Join(",", taken.OrderBy(value => value))}]).");
        }

        client.Send(OnJoinRoomAckPacket.Build(new JoinRoomResponse(
            room.Index, JoinRoomResult.Success, member.Slot)));
        client.Send(OnRoomDescInfPacket.Build(RoomDescriptor.FromCreate(room.Settings)));
        foreach (LocalRoomMember occupant in occupants)
        {
            client.Send(OnUpdateJoinerInfoInfPacket.Build(MemberInfo(occupant)));
        }
        client.Send(OnPostJoinRoomInfPacket.Build());
        // Nothing else in the join burst carries the slot locks, so a player joining a
        // room whose host already closed slots draws them all as open.
        //
        // This MUST come after OnPostJoinRoomInf. 0x54's net handler sub_434350 always
        // updates net+895060, but the redraw only happens when it forwards to the scene
        // observer (net+895272), and the room scene only registers itself in its
        // constructor - so anything sent before the scene exists updates the array
        // silently and never appears.
        foreach (byte closed in closedSlots)
        {
            client.Send(OnSlotControlAckPacket.Build(
                new SlotControlResponse(closed, Enabled: false)));
        }

        // The room scene has nowhere to show a difficulty lock: the create/options dialog
        // draws its FREE/EASY/../SC row from dword_857668, a CLIENT-SIDE global holding
        // whatever that player last picked, and nothing in the room state is fed from the
        // server. The lobby row badge (record+47) is the only place it appears. So say it
        // in chat, or a joiner has no way of knowing before the server refuses a chart.
        if (room.Settings.LockedDifficulty is { } joinedLock)
        {
            SendSystemChat(client,
                $"This room only plays {DifficultyName(joinedLock)} charts.");
        }

        Packet joined = OnUpdateJoinerInfoInfPacket.Build(MemberInfo(member));
        Send(existing.Select(value => value.Client), joined);
        // The joiner records above carry each member's team byte, but the room scene also
        // tracks teams from OnTeamControlInf (0x59) - so a side that only listens to that
        // one never learned the teams on a join, and one client showed team colours while
        // the other never updated. Restate every occupant's team to the WHOLE room, the
        // newcomer and the existing members alike, so both sides always converge.
        BroadcastRoomTeams(room);
        // Everyone's gear, to everyone - the joiner needs the occupants' and the occupants
        // need the joiner's, and both have to be in place before any play scene is built.
        BroadcastRoomLoadouts(room);
        AnnounceRoomEntry(client, waiting, room);
        return true;
    }

    /// <summary>
    /// Publishes every occupant's equipped loadout to every member of the room, bots
    /// included.
    ///
    /// This has to happen on JOIN, not only at StartGame. A peer's gear is drawn by
    /// <c>Player::CreatePanel</c> (sub_4226B0), which reads
    /// <c>net + 894532 + 64*slot</c> ONCE while the play scene builds its panels - and
    /// that array has no other writer than OnUseMountItemInf. The local player's own
    /// branch reads net+893395 instead, which OnLogInAck filled at login, which is exactly
    /// why your own gear appeared and everyone else's did not: theirs only ever arrived in
    /// the same breath as OnStartInf. Pushing it while the room is still idle puts the data
    /// in place long before any panel is constructed.
    /// </summary>
    private void BroadcastRoomLoadouts(LocalRoomState room)
    {
        LocalRoomMember[] members;
        RoomBot[] bots;
        lock (_lock)
        {
            members = room.Members.ToArray();
            bots = [.. room.Bots];
        }

        Client[] recipients = members.Select(value => value.Client).ToArray();
        if (recipients.Length == 0)
        {
            return;
        }
        foreach (LocalRoomMember member in members)
        {
            MountItemState loadout = LoadoutFor(member);
            Send(recipients, OnUseMountItemInfPacket.Build(member.Slot, loadout));
            Logger.Info(member.Client,
                $"Published slot {member.Slot} loadout to {recipients.Length} client(s): " +
                $"[{DescribeLoadout(loadout.Snapshot)}] +{loadout.HpBonus} HP.");
        }
        foreach (RoomBot bot in bots)
        {
            // A stand-in wears nothing, so it gets the base gauge and no bonus.
            Send(recipients, OnUseMountItemInfPacket.Build(
                bot.Slot, new MountItemState(0, bot.MountSnapshot)));
        }
    }

    /// <summary>
    /// The loadout to publish for a member: what the client last volunteered through
    /// UseMountItemInf, or - because it only ever volunteers that on an equip CHANGE -
    /// what the profile says they actually have on.
    ///
    /// The profile wins whenever the volunteered snapshot holds NOTHING the client could
    /// draw, not merely when it is all-zero. An all-0xFF (or otherwise empty) snapshot from
    /// the client is not "no snapshot" by the old test, so it silently replaced a perfectly
    /// good profile loadout with an empty one for the rest of the session.
    /// </summary>
    private MountItemState LoadoutFor(LocalRoomMember member)
    {
        byte[] snapshot = member.MountSnapshot.ToArray();
        byte[] mount = EquippedSlotCount(snapshot) > 0
            ? snapshot
            : Players(member.Client).Read(profile => profile.Inventory.MountLoadout());
        // The 64-byte snapshot is only HALF of it. wire+4 is this loadout's HP BONUS,
        // added to the base gauge by sub_428A0C - see MountItemState.HpBonus. Armed
        // boosters count towards it too: an HP booster is only ever felt here.
        return new MountItemState(BonusFor(member.Client, mount).Hp, mount);
    }

    /// <summary>
    /// The item ids in a 64-byte loadout, as the client sees them: it reads the LOW u16 of
    /// each 8-byte slot and resolves it against ItemStock (wItem + base). Logged so a
    /// default-looking peer panel can be told apart from an empty loadout without guessing.
    /// </summary>
    private static string DescribeLoadout(byte[] loadout) =>
        string.Join(" ", Enumerable.Range(0, loadout.Length / 8)
            .Select(index => BinaryPrimitives.ReadUInt32LittleEndian(
                loadout.AsSpan(index * 8, 4)))
            .Select(id => id is 0 or uint.MaxValue ? "----" : $"{(ushort)id:X4}"));

    private static int EquippedSlotCount(byte[] loadout) =>
        Enumerable.Range(0, loadout.Length / 8)
            .Count(index =>
            {
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(
                    loadout.AsSpan(index * 8, 4));
                return id != uint.MaxValue && id != 0;
            });

    /// <summary>
    /// Sends every occupant's current team to every member of the room, bots included.
    /// Cheap and idempotent - the client just stores each into the matching joiner record
    /// (sub_434DA0) - so it is used after any membership or mode change rather than trying
    /// to work out which side is missing what.
    /// </summary>
    private void BroadcastRoomTeams(LocalRoomState room)
    {
        RoomTeamUpdate[] updates;
        Client[] recipients;
        lock (_lock)
        {
            updates = room.Members
                .Select(value => new RoomTeamUpdate(value.Slot, value.Team))
                .Concat(room.Bots.Select(value => new RoomTeamUpdate(value.Slot, value.Team)))
                .ToArray();
            recipients = room.Members.Select(value => value.Client).ToArray();
        }

        foreach (RoomTeamUpdate update in updates)
        {
            Send(recipients, OnTeamControlInfPacket.Build(update));
        }
    }

    public void LeaveRoom(Client client, bool sendAcknowledgement = true)
    {
        ArgumentNullException.ThrowIfNull(client);
        LocalRoomState? room;
        LocalRoomMember? leaving;
        Client[] waiting;
        Client[] waitingPeers;
        Client[] roomPeers;
        Client[] remainingMembers;
        RoomListEntry[] lobbyGrid;
        bool erased;
        lock (_lock)
        {
            if (!_memberships.Remove(client, out leaving))
            {
                return;
            }

            room = leaving.Room;
            room.Members.Remove(leaving);
            erased = room.Members.Count == 0;
            if (erased)
            {
                _rooms.Remove(room.Index);
            }
            else if (leaving.IsHost)
            {
                LocalRoomMember promoted = room.Members.MinBy(value => value.Slot)!;
                promoted.IsHost = true;
            }
            waiting = WaitingClients();
            waitingPeers = waiting.Where(peer => peer != client).ToArray();
            roomPeers = _clients.Where(peer => _memberships.ContainsKey(peer)).ToArray();
            remainingMembers = room.Members.Select(value => value.Client).ToArray();
            lobbyGrid = _rooms.Values
                .Where(value => value.Index != room.Index || !erased)
                .Select(RoomList)
                .OrderBy(value => value.RoomIndex)
                .ToArray();
        }

        if (sendAcknowledgement)
        {
            client.Send(OnLeaveRoomAckPacket.Build());
            SynchronizeLobbyWaiters(client, waitingPeers, roomPeers);
            // The lobby grid is only ever seeded on entering the CHANNEL, so a player
            // coming back out of a room had an empty grid and could not see - or rejoin -
            // any room that already existed, including the one they just left.
            foreach (RoomListEntry entry in lobbyGrid)
            {
                client.Send(OnRoomInfoInfPacket.Build(entry));
            }
            // The chat panel is a lobby-scene surface. Replay the greeting and configured
            // MOTD after every real room exit, including being kicked, just as on login.
            SendMessageOfTheDay(client);
        }

        // 0x39 adds/refreshes the room in everyone's lobby grid; 0x3A removes it. China
        // used 0x3A for the full record, which is why creating a room used to leave the
        // grid empty.
        Packet roomUpdate = erased
            ? OnRoomInfoUpdateInfPacket.BuildRemove(room.Index)
            : OnRoomInfoInfPacket.Build(RoomList(room));
        Send(waiting, roomUpdate);
        if (!erased)
        {
            // sub_4348B0 removes the member keyed by connection id only when its 0x51
            // state is in the leaving range. Removing it solely on the server left a
            // permanent ghost in every other occupant's room scene.
            Send(remainingMembers, OnUpdateJoinerInfoInfPacket.Build(
                MemberInfo(leaving) with { State = RoomMemberState.Leaving }));
        }
        if (!erased && leaving.IsHost)
        {
            foreach (LocalRoomMember occupant in room.Members)
            {
                Send(remainingMembers, OnUpdateJoinerInfoInfPacket.Build(MemberInfo(occupant)));
            }
        }
        if (!erased)
        {
            // Whoever is left keeps their side; restate it so nobody is left showing the
            // departed player's colours.
            BroadcastRoomTeams(room);
        }
    }

    /// <summary>
    /// <summary>
    /// Identity for a profile-view request (0x1D), which asks by USER ID and can name any
    /// player the asker can see - not just themselves.
    ///
    /// Looks through: the asker's own profile, every other connected client, and the
    /// asker's roster stand-ins (a bot sitting in a room slot is clickable like anyone
    /// else, so it needs an answer too).
    /// </summary>
    /// <summary>Profile whose stats fill the panel, or null when only a name is known.</summary>
    public LocalPlayerProfile? ResolveUserProfile(Client asker, uint userId)
    {
        ArgumentNullException.ThrowIfNull(asker);
        if (userId == Players(asker).Read(profile => profile.WireUserId) ||
            userId == Players(asker).Read(profile => profile.WaiterKey))
        {
            return Players(asker).Profile;
        }
        foreach (Client peer in ConnectedPeers())
        {
            if (peer == asker)
            {
                continue;
            }
            LocalPlayerStore store = Players(peer);
            (uint wire, uint key) = store.Read(
                profile => (profile.WireUserId, (uint)profile.WaiterKey));
            if (wire == userId || key == userId)
            {
                return store.Profile;
            }
        }
        return null;
    }

    public bool TryResolveUserInfo(
        Client asker,
        uint userId,
        out string nickname,
        out string accountId)
    {
        ArgumentNullException.ThrowIfNull(asker);

        (uint ownId, string ownNickname, string ownAccountId, RosterUser? roster) =
            Players(asker).Read(profile => (
                profile.WireUserId,
                profile.Nickname,
                profile.AccountId,
                profile.Roster.FirstOrDefault(user => user.UserId == userId)));

        if (userId == ownId ||
            userId == Players(asker).Read(profile => profile.WaiterKey))
        {
            nickname = ownNickname;
            accountId = ownAccountId;
            return true;
        }

        // CONNECTED PLAYERS FIRST. Roster stand-ins get ids derived from their owner's
        // account key, so one can collide with a real player's - and checking roster
        // first then handed back the stand-in's NAME while ResolveUserProfile (which only
        // looks at real players) supplied the stats. That mismatch is what showed one
        // player's numbers under a completely different name.
        Client[] peers;
        lock (_lock)
        {
            peers = _clients.Where(peer => peer != asker).ToArray();
        }
        foreach (Client peer in peers)
        {
            // Accept EITHER id. Joiner records and messenger lists carry the session id,
            // but a lobby waiter row carries the stable WaiterKey - so a click from the
            // user list arrives as that instead, and matching only one of them left half
            // the ways of opening a profile answering "invalid user info".
            (uint peerId, uint peerKey, string peerNickname, string peerAccountId) =
                Players(peer).Read(profile => (
                    profile.WireUserId,
                    (uint)profile.WaiterKey,
                    profile.Nickname,
                    profile.AccountId));
            if (peerId == userId || peerKey == userId)
            {
                nickname = peerNickname;
                accountId = peerAccountId;
                return true;
            }
        }

        // Only when nobody real owns the id does a roster stand-in answer for it.
        if (roster != null)
        {
            nickname = roster.Nickname;
            accountId = roster.AccountId;
            return true;
        }

        nickname = string.Empty;
        accountId = string.Empty;
        return false;
    }

    /// <summary>
    /// Resolves a nickname to a user id across everyone the asker can see - themselves,
    /// every other connected client, and their roster stand-ins. The messenger's own
    /// resolver only knew the caller's profile and roster, so adding a second logged-in
    /// player by name always answered UserNotFound.
    ///
    /// Matching is case-insensitive: the client sends whatever the player typed.
    /// </summary>
    public bool TryResolveUserId(Client asker, string nickname, out uint userId)
    {
        ArgumentNullException.ThrowIfNull(asker);
        userId = 0;
        if (string.IsNullOrEmpty(nickname))
        {
            return false;
        }

        (uint ownId, string ownNickname, RosterUser? roster) = Players(asker).Read(
            profile => (
                profile.WireUserId,
                profile.Nickname,
                profile.Roster.FirstOrDefault(user => string.Equals(
                    user.Nickname, nickname, StringComparison.OrdinalIgnoreCase))));

        if (string.Equals(ownNickname, nickname, StringComparison.OrdinalIgnoreCase))
        {
            userId = ownId;
            return true;
        }
        // Connected players win over roster stand-ins, whose ids can collide with theirs.
        Client[] peers;
        lock (_lock)
        {
            peers = _clients.Where(peer => peer != asker).ToArray();
        }
        foreach (Client peer in peers)
        {
            (uint peerId, string peerNickname) = Players(peer).Read(
                profile => (profile.WireUserId, profile.Nickname));
            if (string.Equals(peerNickname, nickname, StringComparison.OrdinalIgnoreCase))
            {
                userId = peerId;
                return true;
            }
        }
        if (roster != null)
        {
            userId = roster.UserId;
            return true;
        }
        return false;
    }

    /// Places a roster stand-in into the caller's current room as a render-only occupant,
    /// so the multiplayer room UI can be exercised with a single real account. The bot
    /// fills the next free enabled slot and its joiner record is broadcast to every real
    /// member, exactly like a real join, but nothing is ever sent TO it.
    /// </summary>
    public void AddRoomBot(Client host, string nickname)
    {
        ArgumentNullException.ThrowIfNull(host);
        RosterUser? roster = Players(host).Read(profile => profile.Roster.FirstOrDefault(
            user => string.Equals(user.Nickname, nickname, StringComparison.OrdinalIgnoreCase)));
        if (roster == null)
        {
            SendSystemChat(host, $"No roster user named '{nickname}'. Add one with " +
                "/packettest roster add <nickname>.");
            return;
        }

        RoomBot bot;
        LocalRoomMember[] members;
        Client[] waiting;
        RoomListEntry grid;
        lock (_lock)
        {
            if (!_memberships.TryGetValue(host, out LocalRoomMember? me))
            {
                SendSystemChat(host, "You are not in a room. Create or join one first.");
                return;
            }
            LocalRoomState room = me.Room;
            if (room.OccupantCount >= room.Settings.Capacity)
            {
                SendSystemChat(host, "The room is full.");
                return;
            }
            if (room.Bots.Any(existing => existing.Roster.UserId == roster.UserId))
            {
                SendSystemChat(host, $"'{roster.Nickname}' is already in this room.");
                return;
            }

            HashSet<byte> taken = [.. room.OccupiedSlots()];
            int slot = Enumerable.Range(0, room.Settings.Capacity)
                .FirstOrDefault(index =>
                    room.SlotsEnabled[index] && !taken.Contains((byte)index), -1);
            if (slot < 0)
            {
                SendSystemChat(host, "No open slot for a bot.");
                return;
            }

            bot = new RoomBot(
                roster,
                (byte)slot,
                // Real assigned ids start at 0x014D and climb; keep bots well clear so a
                // bot's connection id can never collide with a real member's.
                connectionId: checked((ushort)(0xF000 + slot)),
                team: TeamForJoiner(room, checked((byte)slot)));
            room.Bots.Add(bot);
            members = room.Members.ToArray();
            waiting = WaitingClients();
            grid = RoomList(room);
        }

        Packet joiner = OnUpdateJoinerInfoInfPacket.Build(
            RoomMemberInfo.CreateBot(bot.Roster, bot.Slot, bot.ConnectionId, bot.Team));
        Send(members.Select(member => member.Client), joiner);
        // Refresh the lobby grid headcount for everyone outside the room.
        Send(waiting, OnRoomInfoInfPacket.Build(grid));
        SendSystemChat(host,
            $"'{bot.Roster.Nickname}' joined slot {bot.Slot} as a bot (render-only).");
    }

    /// <summary>
    /// Kicks a room occupant identified by user id (what 0x1D carries when the host clicks
    /// another player). Only bots can be removed on this local server; returns true if one
    /// matched so the caller knows the click was a kick rather than an info request.
    /// </summary>
    /// <summary>
    /// Removes whoever occupies a slot, for the host's kick button.
    ///
    /// The client sends the SAME packet for kick and for the slot lock - 0x53 with the
    /// slot - and tells them apart purely by whether the slot is occupied. There is no
    /// separate kick id, which is why kicking appeared to do nothing for so long.
    /// </summary>
    private void KickRoomSlot(Client host, byte slot)
    {
        Client? kickedClient = null;
        RoomBot? kickedBot = null;
        LocalRoomState? room = null;
        lock (_lock)
        {
            if (!_memberships.TryGetValue(host, out LocalRoomMember? me) || !me.IsHost)
            {
                return;
            }
            room = me.Room;
            // The host cannot kick themselves; the client offers it, so refuse politely.
            LocalRoomMember? target = room.Members.FirstOrDefault(
                value => value.Slot == slot && value.Client != host);
            kickedClient = target?.Client;
            kickedBot = room.Bots.FirstOrDefault(value => value.Slot == slot);
        }

        if (kickedClient != null)
        {
            string name = Players(kickedClient).Read(profile => profile.Nickname);
            Logger.Info(host, $"Kicked {name} from slot {slot}.");
            SendSystemChat(kickedClient, "[SYSTEM] You were removed from the room.");
            // Same path a voluntary exit takes, so the room, grid and every peer's roster
            // are cleaned up exactly once.
            LeaveRoom(kickedClient, sendAcknowledgement: true);
            return;
        }

        if (kickedBot != null && room != null)
        {
            KickRoomMember(host, kickedBot.Roster.UserId);
        }
    }

    public bool KickRoomMember(Client host, uint targetUserId)
    {
        ArgumentNullException.ThrowIfNull(host);
        RoomBot? kicked = null;
        LocalRoomMember[] members = [];
        Client[] waiting = [];
        RoomListEntry? grid = null;
        lock (_lock)
        {
            if (_memberships.TryGetValue(host, out LocalRoomMember? me) && me.IsHost)
            {
                kicked = me.Room.Bots.FirstOrDefault(
                    bot => bot.Roster.UserId == targetUserId);
                if (kicked != null)
                {
                    me.Room.Bots.Remove(kicked);
                    members = me.Room.Members.ToArray();
                    waiting = WaitingClients();
                    grid = RoomList(me.Room);
                }
            }
        }
        if (kicked == null)
        {
            return false;
        }

        Packet leave = OnUpdateJoinerInfoInfPacket.Build(RoomMemberInfo.CreateBot(
            kicked.Roster, kicked.Slot, kicked.ConnectionId, kicked.Team)
            with { State = RoomMemberState.Leaving });
        Send(members.Select(member => member.Client), leave);
        Send(waiting, OnRoomInfoInfPacket.Build(grid!));
        SendSystemChat(host, $"Kicked bot '{kicked.Roster.Nickname}'.");
        return true;
    }

    /// <summary>Sets how every bot in the caller's room resolves its end-of-song result.</summary>
    private void SetRoomBotOutcome(Client host, BotOutcome outcome)
    {
        int count;
        lock (_lock)
        {
            if (!_memberships.TryGetValue(host, out LocalRoomMember? me))
            {
                SendSystemChat(host, "You are not in a room.");
                return;
            }
            foreach (RoomBot bot in me.Room.Bots)
            {
                bot.Outcome = outcome;
            }
            count = me.Room.Bots.Count;
        }
        SendSystemChat(host,
            $"{count} bot(s) will {outcome.ToString().ToLowerInvariant()} the next song.");
    }

    /// <summary>Removes every bot from the caller's room and tells the members they left.</summary>
    public void ClearRoomBots(Client host)
    {
        ArgumentNullException.ThrowIfNull(host);
        RoomBot[] removed;
        LocalRoomMember[] members;
        Client[] waiting;
        RoomListEntry grid;
        lock (_lock)
        {
            if (!_memberships.TryGetValue(host, out LocalRoomMember? me))
            {
                SendSystemChat(host, "You are not in a room.");
                return;
            }
            removed = [.. me.Room.Bots];
            me.Room.Bots.Clear();
            members = me.Room.Members.ToArray();
            waiting = WaitingClients();
            grid = RoomList(me.Room);
        }

        foreach (RoomBot bot in removed)
        {
            // State 142 removes a member keyed by the connection value at +59 (sub_4348B0).
            Packet leave = OnUpdateJoinerInfoInfPacket.Build(RoomMemberInfo.CreateBot(
                bot.Roster, bot.Slot, bot.ConnectionId, bot.Team)
                with { State = RoomMemberState.Leaving });
            Send(members.Select(member => member.Client), leave);
        }
        Send(waiting, OnRoomInfoInfPacket.Build(grid));
        SendSystemChat(host, $"Removed {removed.Length} bot(s) from the room.");
    }

    public void ToggleReady(Client client)
    {
        LocalRoomMember member;
        Client[] recipients;
        lock (_lock)
        {
            if (!_memberships.TryGetValue(client, out member!))
            {
                return;
            }
            // The owner starts the match and never participates in the ready toggle.
            // Accepting a forged ReadyReq from it would make the server's host state
            // diverge from the client's fixed 0x8D owner state.
            if (member.IsHost)
            {
                return;
            }
            member.IsReady = !member.IsReady;
            recipients = member.Room.Members.Select(value => value.Client).ToArray();
        }

        Send(recipients, OnReadyInfPacket.Build(new RoomReadyUpdate(
            member.IsReady, member.ConnectionId, member.Client.UserId ?? 0)));
    }

    /// <summary>
    /// The side a new occupant takes. Nothing on the wire carries a room-level team mode,
    /// so a room counts as a team room exactly when its host has picked a side; until then
    /// everyone sits on <see cref="LocalRoomProtocol.SingleTeam"/>.
    /// </summary>
    private static byte TeamForJoiner(LocalRoomState room, byte slot)
    {
        LocalRoomMember? host = room.Members.FirstOrDefault(value => value.IsHost);
        return host is null || host.Team == LocalRoomProtocol.SingleTeam
            ? LocalRoomProtocol.SingleTeam
            : LocalRoomProtocol.TeamForSlot(host.Team, host.Slot, slot);
    }

    /// <summary>
    /// The GAME TYPE byte (descriptor 38 -> net+794270), which drives TWO things:
    /// <code>
    ///   sub_4285BF() == (net+794270 == 1)
    ///   sub_44F48D @0x45076d: jz -> the PLACEMENT result (1st..6th), else WIN/LOSE
    ///   Player::CreatePanel (sub_4226B0): == 1 enables the item slots
    /// </code>
    /// So the WIN/LOSE screen belongs to a room with SIDES, and a room without them gets
    /// the 1st..6th ladder. Item battle is the one forced case: it needs the flag for its
    /// item slots, so it always uses the win/lose screen.
    /// </summary>
    private static byte GameTypeFor(byte matchMode, bool hasTeams) =>
        matchMode == LocalRoomProtocol.ItemBattleMatchMode || hasTeams ? (byte)1 : (byte)0;

    /// <summary>
    /// Re-evaluates the game type after a team change and announces it if it moved.
    /// 0x5B (sub_452825) is the ONLY live writer of net+794270 - the descriptor sets it
    /// once at join - so without this a room that switches to sides keeps whichever
    /// result screen it was created with.
    /// </summary>
    private void SyncGameType(LocalRoomState room)
    {
        byte desired;
        Client[] recipients;
        lock (_lock)
        {
            bool hasTeams = room.Members.Select(value => value.Team)
                .Concat(room.Bots.Select(value => value.Team))
                .Any(team => team != LocalRoomProtocol.SingleTeam);
            desired = GameTypeFor(room.Settings.MatchMode, hasTeams);
            if (desired == room.Settings.GameType)
            {
                return;
            }
            room.Settings = room.Settings with { GameType = desired };
            recipients = room.Members.Select(value => value.Client).ToArray();
        }

        Send(recipients, OnGameTypeInfPacket.Build(desired));
    }

    /// <summary>
    /// Players who ticked 초대거부 ("refuse invitations"). The client sends 0xA6 when the
    /// box is toggled; retail only acknowledges it, so the opt-out has to live here or an
    /// invite would reach someone who asked not to receive one.
    /// </summary>
    private readonly HashSet<Client> _invitesRefused = [];

    public void SetInvitesRefused(Client client, bool refused)
    {
        ArgumentNullException.ThrowIfNull(client);
        lock (_lock)
        {
            if (refused)
            {
                _invitesRefused.Add(client);
            }
            else
            {
                _invitesRefused.Remove(client);
            }
        }
    }

    /// <summary>
    /// Invites someone to the caller's room. The client offers "빠른초대" with no target,
    /// so the server picks: anyone in the channel who is NOT already in a room and has not
    /// refused invitations.
    ///
    /// This used to be hardcoded to <see cref="QuickInviteResult.NoUsersAvailable"/> from
    /// when the server had a single account, so the button could never do anything.
    /// </summary>
    public QuickInviteResult QuickInvite(Client host)
    {
        ArgumentNullException.ThrowIfNull(host);
        Client? target;
        lock (_lock)
        {
            // Validate the caller FIRST. Previously target selection ran first, so using
            // quick invite outside a room incorrectly reported "no users available" and
            // never identified the real problem.
            if (!_memberships.TryGetValue(host, out LocalRoomMember? member) ||
                member.Room.Phase != RoomPlayPhase.Waiting)
            {
                return QuickInviteResult.UnableToInvite;
            }
            if (member.Room.OccupantCount >= member.Room.OpenSlotCount)
            {
                return QuickInviteResult.MaximumInvitationsExceeded;
            }

            target = _clients.FirstOrDefault(candidate =>
                candidate != host &&
                !_memberships.ContainsKey(candidate) &&
                !_invitesRefused.Contains(candidate));
        }

        return target == null
            ? QuickInviteResult.NoUsersAvailable
            : SendRoomInvite(host, target);
    }

    /// <summary>
    /// Invites a NAMED player to the caller's room (/invite). Identical delivery to
    /// 빠른초대 - the client has no "invite this person" request of its own, so the target
    /// is chosen here and the invitation is pushed the same way.
    /// </summary>
    /// <summary>
    /// Admin grant: hand a player money, cash, experience, levels or an item.
    ///
    ///   /grant &lt;nickname&gt; max|cash|exp &lt;amount&gt;
    ///   /grant &lt;nickname&gt; level &lt;levels&gt;
    ///   /grant &lt;nickname&gt; item &lt;catalogId&gt; [count]
    ///
    /// The RECIPIENT gets a targeted 30-second alert naming the GM; the GM gets the
    /// outcome as ordinary system chat. Nothing is broadcast to the channel - a grant is
    /// between the two of them.
    ///
    /// Only players in THIS channel can be granted to. The award has to be written into a
    /// live session and pushed to that client, so an offline player would need a
    /// database-side tool instead of a chat command.
    /// </summary>
    /// <summary>The in-game equivalents of the console's ban commands.</summary>
    private static readonly (string Verb, AccountLockState State)[] InGameLockCommands =
    [
        ("/ban", AccountLockState.Locked),
        ("/suspend", AccountLockState.UnderReview),
        ("/unban", AccountLockState.None)
    ];

    /// <summary>
    /// /ban &lt;nickname&gt; &lt;reason&gt;, /suspend &lt;nickname&gt; &lt;reason&gt;,
    /// /unban &lt;nickname&gt;.
    ///
    /// The reason is stored against the account and echoed back to the ISSUING admin
    /// only. The server-wide alert names the player and nothing else.
    /// </summary>
    private void RunAccountLock(
        Client sender, string verb, AccountLockState state, string arguments)
    {
        string[] parts = arguments.Split(
            ' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            SendSystemChat(sender, $"Usage: {verb} <nickname>" +
                (state == AccountLockState.None ? "" : " <reason>"));
            return;
        }

        string nickname = parts[0];
        string reason = parts.Length > 1 ? parts[1] : string.Empty;
        if (state != AccountLockState.None && reason.Length == 0)
        {
            // A ban with no reason is unmanageable later, so require one up front.
            SendSystemChat(sender, $"Give a reason: {verb} <nickname> <reason>");
            return;
        }

        if (Administration == null)
        {
            SendSystemChat(sender, "Account administration is not available.");
            return;
        }

        try
        {
            LocalPlayerProfile target = Administration.SetAccountLock(
                nickname, state, reason);
            SendSystemChat(sender,
                $"{target.Nickname} is now {AccountLockReasons.Describe(state)}" +
                (reason.Length == 0 ? "." : $" ({reason})."));
        }
        catch (KeyNotFoundException)
        {
            SendSystemChat(sender, $"No account matches '{nickname}'.");
        }
        catch (ArgumentException error)
        {
            SendSystemChat(sender, error.Message);
        }
    }

    private void RunGrant(Client sender, string arguments)
    {
        string[] parts = arguments.Split(
            ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
        {
            SendSystemChat(sender,
                "Usage: /grant <nickname> <max|cash|exp|level|item> <amount> [count]");
            return;
        }

        string nickname = parts[0];
        string what = parts[1].ToLowerInvariant();
        if (!uint.TryParse(parts[2], out uint amount) || amount == 0)
        {
            SendSystemChat(sender, $"'{parts[2]}' is not a positive whole number.");
            return;
        }

        Client? target;
        lock (_lock)
        {
            target = _clients.FirstOrDefault(peer =>
                peer.PlayerStore != null &&
                string.Equals(
                    peer.PlayerStore.Read(profile => profile.Nickname),
                    nickname,
                    StringComparison.OrdinalIgnoreCase));
        }

        if (target == null)
        {
            SendSystemChat(sender, $"No player named '{nickname}' is in this channel.");
            return;
        }

        string gmName = Players(sender).Read(profile => profile.Nickname);
        string awarded;

        switch (what)
        {
            case "max":
            case "money":
            {
                uint total = Players(target).Update(profile =>
                    profile.Progress.Money = AddSaturating(profile.Progress.Money, amount));
                awarded = $"{amount:N0} MAX";
                SendSystemChat(sender, $"{nickname} now holds {total:N0} MAX.");
                PushProgress(target);
                break;
            }
            case "cash":
            {
                uint total = Players(target).Update(profile =>
                    profile.Progress.Cash = AddSaturating(profile.Progress.Cash, amount));
                awarded = $"{amount:N0} cash";
                // There is no cash equivalent of OnUpdateUserPropertyMoneyInf - that
                // packet carries MAX only - so the counter on their screen keeps the
                // old figure until they relog. The award itself is already saved.
                SendSystemChat(sender,
                    $"{nickname} now holds {total:N0} cash " +
                    "(their counter updates on relog).");
                PushProgress(target);
                break;
            }
            case "exp":
            case "experience":
            {
                uint level = Players(target).Update(profile =>
                {
                    profile.Progress.Experience =
                        AddSaturating(profile.Progress.Experience, amount);
                    ApplyLevelUps(profile.Progress);
                    return profile.Progress.Level;
                });
                awarded = $"{amount:N0} EXP";
                // Levels are stored zero-based; the client draws level + 1.
                SendSystemChat(sender, $"{nickname} is now level {level + 1}.");
                PushProgress(target);
                break;
            }
            case "level":
            case "levels":
            {
                uint level = Players(target).Update(profile =>
                {
                    uint wanted = profile.Progress.Level + amount;
                    profile.Progress.Level =
                        wanted > ExperienceCurve.MaxLevel ? ExperienceCurve.MaxLevel : wanted;
                    // The bar restarts at the new level, so it cannot show progress
                    // towards a threshold that no longer applies.
                    profile.Progress.Experience = 0;
                    return profile.Progress.Level;
                });
                awarded = amount == 1 ? "a level" : $"{amount:N0} levels";
                SendSystemChat(sender, $"{nickname} is now level {level + 1}.");
                PushProgress(target);
                break;
            }
            case "item":
            {
                ushort count = parts.Length > 3 && ushort.TryParse(parts[3], out ushort parsed)
                    ? parsed
                    : (ushort)1;
                if (count == 0)
                {
                    SendSystemChat(sender, "Item count must be at least 1.");
                    return;
                }
                if (amount > ushort.MaxValue)
                {
                    SendSystemChat(sender, $"'{amount}' is not a catalog id.");
                    return;
                }

                IReadOnlyList<TimedInventoryItem> granted = Players(target).GrantItems(
                    [((ushort)amount, count)], DateTimeOffset.UtcNow);
                if (granted.Count == 0)
                {
                    // GrantItems skips unknown ids and a full box, so distinguish them -
                    // "nothing happened" is the least useful thing to tell a GM.
                    SendSystemChat(sender,
                        Players(target).Shop.TryGet((ushort)amount, out _)
                            ? $"{nickname}'s item box has no room for 0x{amount:X}."
                            : $"0x{amount:X} is not an item in ItemStock.");
                    return;
                }

                string itemName =
                    Players(target).Shop.TryGet((ushort)amount, out ShopItemDefinition? def)
                    ? def!.Name
                    : $"0x{amount:X}";
                awarded = count == 1 ? itemName : $"{itemName} x{count}";
                SendSystemChat(sender, $"Granted {awarded} to {nickname}.");
                SendInventoryRefresh(target);
                break;
            }
            default:
                SendSystemChat(sender,
                    $"'{what}' is not grantable. Use max, cash, exp, level or item.");
                return;
        }

        // The recipient's own notice, and only theirs: a 30-second on-screen alert naming
        // the GM. ChatMessageType.Alert is the native yellow banner (sub_42B83D).
        target.Send(OnChatInfPacket.BuildAscii(
            $"{ChatMessage.AdminDisplayName} {gmName} has granted you {awarded}.",
            ChatMessageType.Alert));

        Logger.Info(sender, $"GRANT: {gmName} gave {nickname} {awarded}.");
        if (what is not "item")
        {
            SendSystemChat(sender, $"Granted {awarded} to {nickname}.");
        }
    }

    /// <summary>
    /// Pushes money, level/EXP and the win-loss record to a player's own client.
    ///
    /// These property packets are the ONLY things that refresh those counters - the same
    /// reason the end-of-song award sends them. Changing the profile without them leaves
    /// the granted MAX or level invisible until the player relogs, which looks exactly
    /// like the grant having failed.
    /// </summary>
    private void PushProgress(Client client)
    {
        (uint userId, uint money, uint experience, uint level, uint wins, uint losses,
            uint draws) = Players(client).Read(profile => (
                profile.WireUserId,
                profile.Progress.Money,
                profile.Progress.Experience,
                profile.Progress.Level,
                profile.Progress.Wins,
                profile.Progress.Losses,
                profile.Progress.Draws));

        client.Send(OnUpdateUserPropertyMoneyInfPacket.Build(userId, money));
        client.Send(OnUpdateUserPropertyLevelInfPacket.Build(userId, experience, level));
        client.Send(OnUpdateUserPropertyRecordInfPacket.Build(userId, wins, losses, draws));
        BroadcastLocalProfile(client);
    }

    /// <summary>
    /// Advances a player against the client's own curve, so its EXP bar agrees with the
    /// level being shown. Same loop the end-of-song award uses.
    /// </summary>
    private static void ApplyLevelUps(LocalPlayerProgress progress)
    {
        ExperienceCurve.ApplyLevelUps(progress);
    }

    public NamedRoomInviteResult InviteByName(Client host, string nickname)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(nickname);

        Client? target;
        lock (_lock)
        {
            // Membership must be evaluated before nickname lookup. Otherwise an invalid
            // or offline nickname masks the fact that the caller is not in any room.
            if (!_memberships.TryGetValue(host, out LocalRoomMember? member))
            {
                return NamedRoomInviteResult.InviterNotInRoom;
            }
            if (member.Room.Phase != RoomPlayPhase.Waiting)
            {
                return NamedRoomInviteResult.RoomInProgress;
            }

            target = _clients.FirstOrDefault(peer =>
                string.Equals(
                    Players(peer).Read(profile => profile.Nickname),
                    nickname,
                    StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                return NamedRoomInviteResult.TargetNotFound;
            }
            if (target == host)
            {
                return NamedRoomInviteResult.CannotInviteSelf;
            }
            if (_memberships.ContainsKey(target))
            {
                return NamedRoomInviteResult.TargetAlreadyInRoom;
            }
            if (_invitesRefused.Contains(target))
            {
                return NamedRoomInviteResult.TargetRefusesInvitations;
            }
            if (member.Room.OccupantCount >= member.Room.OpenSlotCount)
            {
                return NamedRoomInviteResult.RoomFull;
            }
        }

        // SendRoomInvite revalidates both clients under the lock. If either one moved
        // between the checks above and delivery, report that race instead of claiming the
        // original state was still true.
        return SendRoomInvite(host, target) switch
        {
            QuickInviteResult.Sent => NamedRoomInviteResult.Sent,
            QuickInviteResult.MaximumInvitationsExceeded => NamedRoomInviteResult.RoomFull,
            _ => NamedRoomInviteResult.StateChanged
        };
    }

    private QuickInviteResult SendRoomInvite(Client host, Client target)
    {
        ushort roomIndex;
        RoomListEntry grid;
        lock (_lock)
        {
            if (!_memberships.TryGetValue(host, out LocalRoomMember? member))
            {
                // Not in a room, so there is nothing to invite anyone to.
                return QuickInviteResult.UnableToInvite;
            }
            if (member.Room.Phase != RoomPlayPhase.Waiting)
            {
                return QuickInviteResult.UnableToInvite;
            }
            if (member.Room.OccupantCount >= member.Room.OpenSlotCount)
            {
                return QuickInviteResult.MaximumInvitationsExceeded;
            }
            if (_memberships.ContainsKey(target) || _invitesRefused.Contains(target))
            {
                return QuickInviteResult.UnableToInvite;
            }
            roomIndex = member.Room.Index;
            grid = RoomList(member.Room);
        }

        string inviter = Players(host).Read(profile => profile.Nickname);
        // The invitee resolves the room out of its OWN grid, so make sure the row is
        // there before the invitation arrives - otherwise they get "the stage no longer
        // exists" instead of an invite. The retail popup intentionally renders this
        // normal room title; the separate inviter field is retained for its join request.
        target.Send(OnRoomInfoInfPacket.Build(grid));
        target.Send(OnInviteReqPacket.Build(inviter, roomIndex));
        Logger.Info(host,
            $"Invited {Players(target).Read(p => p.Nickname)} to room {roomIndex}.");
        return QuickInviteResult.Sent;
    }

    public void ChangeTeam(Client client, TeamControlRequest request)
    {
        LocalRoomMember member;
        Client[] recipients;
        lock (_lock)
        {
            if (!_memberships.TryGetValue(client, out member!))
            {
                return;
            }
            if (!LocalRoomProtocol.IsValidTeam(request.Team))
            {
                // Never fail this silently: a rejected team change sends nothing at all,
                // so the player just sees their click do nothing while everyone else keeps
                // the old colour - which is indistinguishable from a lost packet. SINGLE
                // (0xFF) used to land here and was dropped, which is why pressing it never
                // updated the player who pressed it.
                Logger.Error(client,
                    $"Rejected team 0x{request.Team:X2}; the panel sends 0/1/2 for A/B/C " +
                    $"and 0x{LocalRoomProtocol.SingleTeam:X2} for SINGLE.");
                return;
            }
            member.Team = request.Team;
            // The HOST opening team play is what turns the room into a team room: nothing
            // on the wire carries a room-level team mode, so until the other slots are
            // actually given a side they have nothing to switch away from and the panel is
            // meaningless for them. Seat every other occupant opposite the host (and put
            // them all back on no side when the host returns to SINGLE); they stay free to
            // pick a different side afterwards.
            if (member.IsHost)
            {
                foreach (LocalRoomMember occupant in member.Room.Members)
                {
                    if (occupant == member)
                    {
                        continue;
                    }
                    occupant.Team = member.Team == LocalRoomProtocol.SingleTeam
                        ? LocalRoomProtocol.SingleTeam
                        : LocalRoomProtocol.TeamForSlot(
                            member.Team, member.Slot, occupant.Slot);
                }
                foreach (RoomBot bot in member.Room.Bots)
                {
                    bot.Team = member.Team == LocalRoomProtocol.SingleTeam
                        ? LocalRoomProtocol.SingleTeam
                        : LocalRoomProtocol.TeamForSlot(
                            member.Team, member.Slot, bot.Slot);
                }
            }
            recipients = member.Room.Members.Select(value => value.Client).ToArray();
        }

        // The 팀선택 panel sends 0/1/2 for A/B/C and 0xFF for SINGLE.
        // Restate the WHOLE room's teams, not just the one that changed: the sides are
        // rendered from each joiner record, and a client that missed an earlier update
        // would otherwise stay wrong forever.
        Logger.Info(client,
            $"Slot {member.Slot} changed to team {member.Team}." +
            (member.IsHost ? " Reseated the other slots to match." : string.Empty));
        Send(recipients, OnTeamControlInfPacket.Build(new RoomTeamUpdate(
            member.Slot, member.Team)));
        BroadcastRoomTeams(member.Room);
        // Sides decide the result screen: with them it is WIN/LOSE, without them the
        // 1st..6th ladder. Nothing else pushes net+794270 after the room is created.
        SyncGameType(member.Room);
    }

    public void ChangeRoom(Client client, RoomChangeRequest request)
    {
        LocalRoomState room;
        Client[] waiting;
        Client[] recipients;
        RoomListEntry grid;
        RoomTeamUpdate[] teamUpdates;
        lock (_lock)
        {
            if (!_memberships.TryGetValue(client, out LocalRoomMember? member) ||
                !member.IsHost)
            {
                return;
            }
            room = member.Room;
            // 0x9C carries title, level restriction, password, match mode and the EFFECTOR
            // flag - and NOT the game type, so that field is left exactly as the room was
            // created with. Overwriting it from settings[1] (which is the level
            // restriction) is what pushed the level byte into net+794270. There is no team
            // field here either; sides are per player, over 0x58/0x59.
            // The password field is 11 bytes on the wire and 15 in the room's own record,
            // so copy rather than assign - and copy it at all, which is what was missing:
            // typing a password into the room options did nothing.
            byte[] password = new byte[CreateRoomReqPacket.PasswordTextSize];
            request.Password.CopyTo(password, 0);

            room.Settings = room.Settings with
            {
                TitleField = request.TitleField.ToArray(),
                // settings[1] is the dialog's FREE/EASY/../SC row. Keep both fields in
                // step: LevelRestriction is the byte the room packets put on the wire,
                // DifficultyRestriction is what the server enforces against.
                LevelRestriction = request.LevelRestriction,
                DifficultyRestriction = request.LevelRestriction,
                PasswordField = password,
                MatchMode = request.MatchMode <= OnRoomInfoInfPacket.MaxMatchMode
                    ? request.MatchMode
                    : room.Settings.MatchMode,
                EffectorFlag = request.EffectorFlag
            };
            teamUpdates = [];
            waiting = WaitingClients();
            recipients = room.Members.Select(value => value.Client).ToArray();
            grid = RoomList(room);
        }

        // 0x9D is both the request acknowledgement and the room-scene settings update.
        // Every occupant needs it, not just the host that submitted 0x9C.
        Send(recipients, OnRoomChangeInfoAckPacket.Build(request));
        // Deliberately NO OnGameTypeInf here. 0x9C cannot change the game type, so there is
        // nothing new to announce - and sending it with the value parsed out of settings[1]
        // pushed the level-restriction byte into net+794270, which forced the owner (the
        // only member at the time) back to singles while later joiners never got it.
        if (teamUpdates.Length != 0)
        {
            BroadcastRoomTeams(room);
        }
        Send(waiting, OnRoomInfoInfPacket.Build(grid));
    }

    /// <summary>
    /// The difficulty as the create dialog labels it, so a rejection message names the
    /// button the host actually pressed.
    /// </summary>
    private static string DifficultyName(SongDifficulty difficulty) => difficulty switch
    {
        SongDifficulty.Easy => "EASY",
        SongDifficulty.Normal => "NORMAL",
        SongDifficulty.Hard => "HARD",
        SongDifficulty.Maximum => "MX",
        SongDifficulty.Special => "SC",
        _ => difficulty.ToString()
    };

    public void ChangeDisc(Client client, ChangeDiscRequest request)
    {
        // The client sends a 0-based disc index; the catalog is 1-based (catalog id =
        // discId + 1), the same mapping StartGame and _gameInfo.TryLoad use. Validate
        // and name with it so the "Selected song" log isn't one slot off.
        // ONE PAST THE LAST DISC IS THE RANDOM SLOT. Selecting it only ARMS a roll. The
        // actual song is deliberately chosen in StartGame after readiness checks pass, so
        // browsing to RANDOM neither reveals nor changes the song early. The sentinel is
        // derived (SongCatalog.RandomDiscIndex), never fixed, so adding charts moves it.
        bool randomRequested = request.DiscId == _songs.RandomDiscIndex;
        if (randomRequested)
        {
            Client[] peers;
            lock (_lock)
            {
                if (!_memberships.TryGetValue(client, out LocalRoomMember? roller) ||
                    !roller.IsHost)
                {
                    return;
                }
                roller.RandomDiscPending = true;
                roller.Difficulty = request.Difficulty;
                // A previous /mission on applies to the old concrete song. /mission arm is
                // kept, because it intentionally targets whichever song is chosen next.
                roller.IsMissionMatch = false;
                client.SelectedCourseId = null;
                client.ResetCourseProgress();
                peers = roller.Room.Members
                    .Where(member => member.Client != client)
                    .Select(member => member.Client)
                    .ToArray();
            }

            // The sender has already changed its own selector locally. Mirror the synthetic
            // RANDOM entry to the other occupants now, but do not resolve it yet; StartGame
            // replaces it with the real rolled disc only after a valid host Start request.
            Send(peers, OnChangeDiscInfPacket.Build(request.DiscId, request.Difficulty));
            Logger.Info(client,
                $"RANDOM armed for Start ({(int)Channel.KeyMode}-key, " +
                $"{(SongDifficulty)request.Difficulty}); echoed to {peers.Length} peer(s).");
            return;
        }

        if (!_songs.TryGet(request.DiscId + 1, out SongDefinition? selectedSong))
        {
            // Disc index is 0-based (catalog id = discId + 1), so validate every value —
            // including 0, the legitimate first chart — against the catalog.
            Logger.Error(client,
                $"Rejected unknown disc index {request.DiscId} (catalog id " +
                $"{request.DiscId + 1}); the active catalog contains {_songs.Count} " +
                $"songs and its random slot is {_songs.RandomDiscIndex}.");
            return;
        }

        // A room created with a difficulty lock plays that difficulty and nothing else.
        // Checked before anything is committed, and on the SERVER rather than trusted to
        // the client: the host is the one picking, and the point of the lock is to protect
        // the other occupants from a chart they did not agree to.
        SongDifficulty? lockedDifficulty;
        lock (_lock)
        {
            lockedDifficulty =
                _memberships.TryGetValue(client, out LocalRoomMember? picker) && picker.IsHost
                    ? picker.Room.Settings.LockedDifficulty
                    : null;
        }

        if (lockedDifficulty is { } required && request.Difficulty != (byte)required)
        {
            SendSystemChat(client,
                $"This room is locked to {DifficultyName(required)} charts.");
            Logger.Info(client,
                $"Rejected {(SongDifficulty)request.Difficulty} selection of " +
                $"{selectedSong.Tag}: the room is locked to {required}.");
            return;
        }

        Client[] recipients;
        Client[] waiting;
        RoomListEntry grid;
        lock (_lock)
        {
            if (!_memberships.TryGetValue(client, out LocalRoomMember? member) ||
                !member.IsHost)
            {
                return;
            }

            member.DiscId = request.DiscId;
            member.HasDisc = true;
            member.Difficulty = request.Difficulty;
            member.RandomDiscPending = false;
            // An armed /mission lands on this selection and is spent doing so. Re-check the
            // mode in case the room was changed to item battle after it was armed.
            bool itemBattle = member.Room.Settings.MatchMode ==
                LocalRoomProtocol.ItemBattleMatchMode;
            member.IsMissionMatch = !itemBattle && member.MissionPending;
            member.MissionPending = false;
            if (member.IsMissionMatch)
            {
                Logger.Info(client,
                    $"DJ MISSION will run on {selectedSong.Tag} (disc {request.DiscId}).");
            }
            // Picking a disc by hand means the player is in normal song select, not the
            // Course Club, so a stale selection must not turn the next start into a
            // course start.
            client.SelectedCourseId = null;
            client.ResetCourseProgress();
            recipients = member.Room.Members.Select(value => value.Client).ToArray();
            waiting = WaitingClients();
            grid = RoomList(member.Room);
        }
        // Echo the difficulty back with the disc - it is the field the room UI and every
        // other client read the chart's difficulty from. Zeroing it pinned every room to EZ.
        Send(recipients, OnChangeDiscInfPacket.Build(request.DiscId, request.Difficulty));
        Send(waiting, OnRoomInfoInfPacket.Build(grid));
        SongKeyMode keyMode = Channel.KeyMode;
        int availableCharts = selectedSong.Charts.Count(chart =>
            chart.KeyMode == keyMode && chart.IsAvailable);
        Logger.Info(client,
            $"Selected song {selectedSong.Id} ({selectedSong.Tag}) in " +
            $"{(int)keyMode}-key; {availableCharts} chart definitions available.");
    }

    public void ToggleSlot(Client client, SlotControlRequest request)
    {
        // 0x53 is only the empty-slot lock/unlock toggle. Its control byte is not an action
        // selector (it varies per request); kicking an occupant is a separate id, 0x1D.
        bool enabled = false;
        byte openSlots = 0;
        string slotMap = string.Empty;
        Client[] occupants = [];
        Client[] waiting = [];
        RoomListEntry grid = default!;
        byte? kickSlot = null;
        lock (_lock)
        {
            if (!_memberships.TryGetValue(client, out LocalRoomMember? member) ||
                !member.IsHost || request.Slot >= member.Room.SlotsEnabled.Length)
            {
                return;
            }
            LocalRoomState room = member.Room;
            // 0x53 IS ALSO THE KICK. The same packet does both: on an EMPTY slot it locks
            // or unlocks it, on an OCCUPIED one it removes whoever is standing there.
            // Ignoring the occupied case is why kicking never did anything - the client
            // sends no other packet for it. (Captured: `53 00 AD 01` while kicking slot 1.)
            if (room.OccupiedSlots().Contains(request.Slot))
            {
                kickSlot = request.Slot;
            }
            else
            {
            // Closing a slot lowers the room's capacity, so it must never drop below the
            // people already standing in it.
            if (room.SlotsEnabled[request.Slot] &&
                room.OpenSlotCount - 1 < room.OccupantCount)
            {
                Logger.Error(client,
                    $"Refused to close slot {request.Slot}: the room holds " +
                    $"{room.OccupantCount} of {room.OpenSlotCount} open slots.");
                return;
            }
            enabled = room.SlotsEnabled[request.Slot] = !room.SlotsEnabled[request.Slot];
            occupants = room.Members.Select(value => value.Client).ToArray();
            waiting = WaitingClients();
            grid = RoomList(room);
            slotMap = string.Join(" ", Enumerable
                .Range(0, room.Settings.Capacity)
                .Select(index =>
                {
                    string who = room.OccupiedSlots().Contains((byte)index)
                        ? "*"
                        : room.SlotsEnabled[index] ? "." : "X";
                    return $"{index}{who}";
                }));
            openSlots = room.OpenSlotCount;
            }
        }

        if (kickSlot is byte target)
        {
            KickRoomSlot(client, target);
            return;
        }

        // The client's slot index and the server's member slot must be the same number.
        // If a joiner ever lands on a slot logged as X here, they are off by one.
        Logger.Info(client,
            $"Slot {request.Slot} {(enabled ? "opened" : "CLOSED")}; " +
            $"capacity now {openSlots}; slots [{slotMap}] (* occupied, X closed).");
        // 0x54 is the lock packet for EVERY occupant, not just the host who asked.
        // Its net handler sub_434350 writes `net+895060 + 4*slot = raw[4] != 0` - the
        // per-slot array the room scene draws the X over (sub_45284E, whose debug strings
        // are literally "X-ON:%d" / "X-OFF:%d"). Sending peers 0x55 instead did nothing to
        // that array: sub_4528AF only makes the receiver send a 0x56 back, so everyone
        // except the host kept seeing the closed slots as open.
        Send(occupants, OnSlotControlAckPacket.Build(new SlotControlResponse(
            request.Slot, enabled)));
        // The lobby row's capacity changed with it.
        Send(waiting, OnRoomInfoInfPacket.Build(grid));
    }

    /// <summary>
    /// Records a player's modifier selection and echoes it to the WHOLE room, the sender
    /// included.
    ///
    /// The echo is what makes modifiers take effect. sub_42919A, the OnUseEffectorInf
    /// handler, does two things: it memcpy's the 24-byte config into that slot's player
    /// record at +2168 - which is what gameplay reads - and, only when the slot is the
    /// local player, mirrors the selected indices into dword_68EDC0 (the UI global). The
    /// local player's +2168 is written by nothing else, so withholding the echo leaves it
    /// zeroed and the chosen modifier never applies. Echoing the client's own selection
    /// straight back is idempotent: both writes land on the values it just sent.
    ///
    /// What must NOT happen is sending a stale config: the client only reports its
    /// selection here, from sub_436690 in its OnStartInf handler, so anything sent during
    /// the start burst predates it and would overwrite the real choice with zeros.
    /// </summary>
    public void SetEffector(Client client, EffectorSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        byte slot;
        Client[] recipients;
        lock (_lock)
        {
            if (!_memberships.TryGetValue(client, out LocalRoomMember? member))
            {
                return;
            }
            member.EffectorConfiguration = selection.Configuration.ToArray();
            slot = member.Slot;
            // 4 slots x (selected index, param, param) as words.
            Logger.Info(client,
                $"UseEffectorInf slot {slot}: {DescribeEffectors(selection.Configuration)}");
            recipients = member.Room.Members.Select(value => value.Client).ToArray();
        }

        Send(recipients, OnUseEffectorInfPacket.Build(slot, selection.Configuration));
    }

    public void SetMountSnapshot(Client client, byte[] snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        GameplayProtocol.RequireLength(
            snapshot, GameplayProtocol.MountSnapshotSize, nameof(snapshot));
        byte slot;
        Client[] recipients;
        lock (_lock)
        {
            if (!_memberships.TryGetValue(client, out LocalRoomMember? member))
            {
                return;
            }
            member.MountSnapshot = snapshot.ToArray();
            slot = member.Slot;
            recipients = member.Room.Members.Select(value => value.Client).ToArray();
        }

        byte[] mount = snapshot.ToArray();
        Send(recipients, OnUseMountItemInfPacket.Build(
            slot, new MountItemState(BonusFor(client, mount).Hp, mount)));
        Logger.Info(client,
            $"Client volunteered slot {slot} loadout: [{DescribeLoadout(mount)}].");
    }

    /// <summary>
    /// Relays a member's compact effector-set selection (0xC5) to the other real
    /// clients in the room as OnUseEffectorSetInf (0xC6), appending the sender's slot.
    /// </summary>
    public void BroadcastEffectorSet(Client client, EffectorSetSelection selection)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(selection);
        byte playerSlot;
        Client[] peers;
        lock (_lock)
        {
            if (!_memberships.TryGetValue(client, out LocalRoomMember? member))
            {
                return;
            }

            playerSlot = member.Slot;
            peers = member.Room.Members
                .Where(value => value.Client != client)
                .Select(value => value.Client)
                .ToArray();
        }

        // The sender already applied the choice locally; only remote human clients need
        // the mirror. Bots have no socket and are stored separately from Room.Members.
        if (peers.Length == 0)
        {
            return;
        }
        Send(peers, OnUseEffectorSetInfPacket.Build(selection, playerSlot));
    }

    public void StartGame(Client client, StartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Remember how this stage was started. Video mode auto-plays the chart, so the run
        // must not be scored or paid; the flag is set per start so a normal run after one
        // clears it again.
        client.VideoMode = request.VideoMode;
        if (request.VideoMode)
        {
            Logger.Info(client,
                "StartReq requested video mode (auto-play); this run will not be scored " +
                "or rewarded.");
        }
        LocalRoomMember[] members;
        Client[] waiting;
        RoomListEntry grid;
        uint discId;
        bool hasDisc;
        byte difficulty;
        short roomDescriptor;
        bool missionMatch;
        byte matchMode;
        StartResult? rejection = null;
        bool shutdownRejected = false;
        (CourseDefinition Course, int StageIndex)? courseStart = null;
        SongDefinition? randomSelection = null;
        lock (_lock)
        {
            if (!_memberships.TryGetValue(client, out LocalRoomMember? starter) ||
                !starter.IsHost)
            {
                // Not the host of a room; there is nothing to answer.
                return;
            }

            // Can't start right now: a previous song's phase never reset, or a
            // non-host member isn't ready. Reply with a failure rather than returning
            // silently — a silent return leaves the host client waiting forever for a
            // start response, which is the classic post-song soft-lock.
            if (starter.Room.Phase != RoomPlayPhase.Waiting)
            {
                rejection = StartResult.GameInfoUnavailable;
            }
            else if (starter.Room.Members.Any(member =>
                         !member.IsHost && !member.IsReady))
            {
                rejection = StartResult.NotAllPlayersReady;
            }
            // A battle needs someone to battle. The readiness test above cannot catch a
            // one-player room: it only looks at NON-host members, and in a solo room the
            // host is the only member, so the collection is empty and Any() is false.
            // Ranked is deliberately excluded - it is a solo mode.
            else if (RequiresOpponent(starter.Room.Settings.MatchMode) &&
                     starter.Room.Members.Count + starter.Room.Bots.Count < 2)
            {
                rejection = StartResult.NotEnoughPlayers;
            }
            else if (RequiresOpponent(starter.Room.Settings.MatchMode) &&
                     TeamRejection(starter.Room) is { } teamProblem)
            {
                rejection = teamProblem;
            }
            if (_shutdownDraining && !IsCourseInProgress(client))
            {
                rejection = StartResult.GameInfoUnavailable;
                shutdownRejected = true;
            }

            discId = starter.DiscId;
            hasDisc = starter.HasDisc;
            difficulty = starter.Difficulty;
            matchMode = starter.Room.Settings.MatchMode;
            // RANDOM is a pending room choice, not an immediate ChangeDisc. Only consume it
            // for a start that has passed the phase/readiness/shutdown checks. This is the
            // moment the host asked to play, so choose the real song now and let every
            // downstream path use its concrete disc id.
            if (rejection == null &&
                client.SelectedCourseId == null &&
                starter.RandomDiscPending)
            {
                uint? previousCatalogId = starter.HasDisc ? starter.DiscId + 1 : null;
                if (_songs.TryPickRandomPlayable(
                        Channel.KeyMode,
                        (SongDifficulty)difficulty,
                        previousCatalogId,
                        Random.Shared,
                        out SongDefinition rolledSong))
                {
                    randomSelection = rolledSong;
                    discId = rolledSong.Id - 1;
                    hasDisc = true;
                    starter.DiscId = discId;
                    starter.HasDisc = true;
                    starter.RandomDiscPending = false;

                    bool itemBattle = matchMode == LocalRoomProtocol.ItemBattleMatchMode;
                    bool randomMission = MissionMatchChancePercent > 0 &&
                        Random.Shared.Next(100) < MissionMatchChancePercent;
                    starter.IsMissionMatch = !itemBattle &&
                        (starter.MissionPending || randomMission);
                    starter.MissionPending = false;
                }
                else
                {
                    Logger.Error(client,
                        $"RANDOM has no playable {(int)Channel.KeyMode}-key " +
                        $"{(SongDifficulty)difficulty} charts in {_songs.SourcePath}.");
                    rejection = StartResult.GameInfoUnavailable;
                }
            }
            // Re-checked here as well as at the roll, because the room can be switched to
            // item battle after a proc was already armed and landed on a song.
            missionMatch = starter.IsMissionMatch &&
                matchMode != LocalRoomProtocol.ItemBattleMatchMode;
            // A Course Club start never sends ChangeDiscReq: the course script names the
            // songs, so the client has no disc to announce. Resolve the current stage from
            // the course the client last selected (ChangeCourseReq 0x85) instead.
            //
            // The course wins over whatever disc the member is carrying, rather than only
            // filling in when there is none. After stage one the member holds that stage's
            // song, so a "no disc" test would pin every later stage to the first one.
            // ChangeDiscReq is what ends a course, by clearing the selection outright.
            if (client.SelectedCourseId is ushort courseId &&
                _courses.TryGet(courseId, out CourseDefinition? course) &&
                course != null)
            {
                int stageIndex = Math.Clamp(client.CourseStage, 0, course.Stages.Count - 1);
                CourseStage stage = course.Stages[stageIndex];
                discId = stage.DiscId;
                difficulty = stage.Difficulty;
                hasDisc = true;
                courseStart = (course, stageIndex);
                // A course start is never a mission, whatever the member was carrying.
                missionMatch = false;
                // Record the stage's song on the member exactly as ChangeDiscReq would.
                // Everything downstream reads the member, not these locals - including the
                // re-validation after the game-info load, which compares starter.DiscId
                // against the disc being started and silently abandons the start if they
                // disagree. Leaving the member on its default disc made every course start
                // load a chart and then send nothing at all.
                starter.DiscId = stage.DiscId;
                starter.Difficulty = stage.Difficulty;
                starter.HasDisc = true;
                starter.RandomDiscPending = false;
            }
            // The client folds the room index (sent in OnCreateRoomAck) into its
            // chart-descramble key, so the game-info blob must be keyed with it too.
            roomDescriptor = unchecked((short)starter.Room.Index);
            members = starter.Room.Members.OrderBy(member => member.Slot).ToArray();
        }

        if (rejection is StartResult failure)
        {
            if (shutdownRejected)
            {
                Logger.Info(client,
                    "Rejected a new song start because graceful shutdown is draining.");
            }
            client.Send(OnStartInfPacket.Build(failure));
            return;
        }

        if (randomSelection != null)
        {
            Client[] roomClients = members.Select(member => member.Client).ToArray();
            Send(roomClients, OnChangeDiscInfPacket.Build(discId, difficulty));
            Logger.Info(client,
                $"RANDOM selected {randomSelection.Id} ({randomSelection.Tag}) on Start " +
                $"for {(int)Channel.KeyMode}-key {(SongDifficulty)difficulty}.");
        }

        if (!hasDisc)
        {
            Logger.Error(client, client.SelectedCourseId is ushort pending
                ? $"Cannot start: course {pending} is not in {_courses.SourcePath}" +
                  $" ({_courses.Count} courses loaded)."
                : "Cannot start: the host has not selected a disc.");
            client.Send(OnStartInfPacket.Build(StartResult.GameInfoUnavailable));
            return;
        }

        if (courseStart is var (startedCourse, startedStage))
        {
            Logger.Info(client,
                $"Course \"{startedCourse.Name}\" (id {startedCourse.Id}) " +
                $"stage {startedStage + 1}/{startedCourse.Stages.Count}: " +
                $"disc {discId} difficulty {difficulty}.");
            // The entry fee is for the course, not each song, so it is charged once when
            // the run begins. Refusing here rather than letting an unaffordable course
            // start keeps the MAX cost the course screen advertises meaningful.
            if (startedStage == 0 && !TryChargeCourse(client, startedCourse))
            {
                client.Send(OnStartInfPacket.Build(StartResult.GameInfoUnavailable));
                return;
            }
        }

        // The client sends a 0-based disc index; the DiscStock catalog is 1-based,
        // so the song (and the chart served by _gameInfo.TryLoad) is catalog id
        // discId + 1. Resolve the name with the SAME mapping the provider uses,
        // otherwise the log names the song one slot before the one actually served.
        uint catalogId = discId + 1;
        if (!_songs.TryGet(catalogId, out SongDefinition? selectedSong))
        {
            Logger.Error(client,
                $"Cannot start: disc index {discId} (catalog id {catalogId}) is not " +
                $"present in {_songs.SourcePath}.");
            client.Send(OnStartInfPacket.Build(StartResult.GameInfoUnavailable));
            return;
        }

        // Timing strictness is server-authored: the client has no judgment table of its
        // own for online play, it uses the 13 windows shipped in the chart's config block.
        // The room's mode sets the baseline - a ranking room is meant to be stricter.
        JudgmentWindows roomWindows =
            JudgmentWindows.Retail.Adjust(JudgmentAdjustmentFor(matchMode));
        if (!_gameInfo.TryLoad(
                discId,
                difficulty,
                roomDescriptor,
                out GameInfoPayload? gameInfo,
                out bool usedFallback,
                out string error,
                roomWindows))
        {
            Logger.Error(client, error);
            client.Send(OnStartInfPacket.Build(StartResult.GameInfoUnavailable));
            return;
        }

        // The Course Club learns which stage it is on only from this packet, so stamp it
        // before anything is sent; a freemode start leaves both fields zero.
        if (courseStart is var (stageCourse, stageNumber))
        {
            gameInfo = gameInfo!.WithCourseStage(stageCourse.Id, (ushort)stageNumber);
        }
        // The host picked the RANDOM disc slot, which is the client's own entry into DJ
        // Mission Match, so tell it to run the mission. This is the ONLY thing that may
        // set payload+0 - see GameInfoPayload.GameModeOffset.
        if (missionMatch)
        {
            gameInfo = gameInfo!.WithGameMode(GameInfoPayload.MissionMatchMode);
            Logger.Info(client, "Starting DJ Mission Match (random disc selected).");
        }

        lock (_lock)
        {
            // Re-checked because loading the chart happens outside the lock and the player
            // may have changed disc or left in the meantime. Never fail this silently: the
            // client waits indefinitely for a start response, so a bare return here is the
            // post-song soft-lock, and it hides the reason as well.
            if (!_memberships.TryGetValue(client, out LocalRoomMember? starter) ||
                !starter.IsHost || starter.Room.Phase != RoomPlayPhase.Waiting ||
                starter.DiscId != discId ||
                starter.RandomDiscPending ||
                (_shutdownDraining && !IsCourseInProgress(client)))
            {
                Logger.Error(client,
                    $"Abandoned the start of disc {discId}: the room state changed while " +
                    "the chart was loading " +
                    (starter == null
                        ? "(no longer in a room)."
                        : $"(host={starter.IsHost}, phase={starter.Room.Phase}, " +
                          $"disc={starter.DiscId})."));
                client.Send(OnStartInfPacket.Build(StartResult.GameInfoUnavailable));
                return;
            }

            starter.Room.Phase = RoomPlayPhase.Loading;
            starter.Room.CheckSequence = 0;
            starter.Room.BattleItemSignalsEarned = 0;
            starter.Room.PendingBattleItemSignals = 0;
            foreach (LocalRoomMember member in starter.Room.Members)
            {
                member.IsLoaded = false;
                member.LastResult = null;
                member.Award = StageAward.None;
                member.CourseTotals = null;
                member.BattleItems.Clear();
                member.BattleItemComboRewards.Reset();
                // Only a course stage continues a combo; an ordinary song always starts at
                // zero, so a stale carry can never leak into freemode.
                if (courseStart == null)
                {
                    member.CarriedCombo = 0;
                }
            }
            members = starter.Room.Members.OrderBy(member => member.Slot).ToArray();
            waiting = WaitingClients();
            grid = RoomList(starter.Room);
        }

        Client[] recipients = members.Select(member => member.Client).ToArray();
        // The lobby row's state byte is the only indication that this room is now
        // in-progress. Refresh it as soon as loading starts.
        Send(waiting, OnRoomInfoInfPacket.Build(grid));
        // DO NOT write payload+0 here. It reaches net+794279, which is the GAME MODE, not
        // a result-screen switch: setting it to 1 put the client into DJ MISSION MATCH
        // (survival), which is why the result branch at sub_44F48D 0x4516ef then chose the
        // "mission clear" component. The 1st..6th layout is NOT selected by this byte.
        //
        // Judgment windows are the ONE per-recipient part of a chart, because a SIGHT
        // booster widens them for that player alone. The scramble key depends on the disc,
        // difficulty, session label and room descriptor - never on the player - so each
        // client can be handed the same chart carrying its own timing. Everyone gets the
        // identical broadcast unless somebody actually has a boost, so the ordinary path
        // stays a single encode.
        // DO NOT switch the room's match mode here. net+794271 mode 1 is RANKED, not a
        // mission - forcing it would drop the room into ranked play. DJ Mission happens
        // WITHIN score/item battle and its real trigger is still unidentified; all the
        // game-info mode byte above buys is the asset root and the "mission clear" result
        // component.
        SendGameInfo(recipients, gameInfo!, discId, difficulty, roomDescriptor, roomWindows);
        AnnounceChart(recipients, selectedSong, difficulty);
        Send(recipients, OnJoinEventInfPacket.Build());
        foreach (LocalRoomMember member in members)
        {
            // OnUseEffectorInf mirrors one player's modifiers to the OTHERS; it must never
            // reach its own owner. sub_42919A copies the config into that slot's player
            // record and then, when the slot is the local player (sub_426530), overwrites
            // dword_68EDC0 - the live modifier selection the room UI and gameplay read.
            // The server cannot know the current selection here either: the client only
            // reports it in UseEffectorInf, which sub_436690 sends from its OnStartInf
            // handler, i.e. strictly after this packet. Sending it to the owner therefore
            // replaced their chosen modifiers with a stale (first song: all-zero) config
            // moments before play began.
            Client[] peers = recipients.Where(value => value != member.Client).ToArray();
            if (peers.Length > 0)
            {
                Send(peers, OnUseEffectorInfPacket.Build(
                    member.Slot, member.EffectorConfiguration));
            }
            // The player's EQUIPMENT in game. Restated here as well as on join because an
            // equip change between joining and starting has to reach the peers.
            MountItemState loadout = LoadoutFor(member);
            Send(recipients, OnUseMountItemInfPacket.Build(member.Slot, loadout));
            // Says which source was used and whether it actually held anything, so a blank
            // panel can be told apart from an empty profile in one run.
            Logger.Info(member.Client,
                $"Slot {member.Slot} equipment: " +
                $"{(EquippedSlotCount(member.MountSnapshot) > 0 ? "client" : "profile")} " +
                $"source, [{DescribeLoadout(loadout.Snapshot)}], " +
                $"+{loadout.HpBonus} HP.");
            // The combo carries from chart to chart in a course: sub_422250 seeds the live
            // combo from net+894964[slot], which this packet is the only writer of, so a
            // zero here is what made the on-screen combo restart every stage. Feed it the
            // combo the player was ON when the previous stage ended.
            Send(recipients, OnStartParameterInfPacket.Build(
                member.CarriedCombo == 0
                    ? StartParameter.Default(member.Slot)
                    : StartParameter.Continuing(member.Slot, member.CarriedCombo)));
        }
        // Bots occupy slots too, so item battle needs their effector/mount/start
        // parameters sent to the real members - otherwise the bot's panel is blank and the
        // battle can never resolve its slot. A bot has no owner to exclude, so every packet
        // goes to all recipients.
        RoomBot[] bots;
        lock (_lock)
        {
            bots = members.Length != 0 && _memberships.TryGetValue(client, out LocalRoomMember? m)
                ? [.. m.Room.Bots]
                : [];
        }
        foreach (RoomBot bot in bots)
        {
            Send(recipients, OnUseEffectorInfPacket.Build(
                bot.Slot, bot.EffectorConfiguration));
            Send(recipients, OnUseMountItemInfPacket.Build(
                bot.Slot, new MountItemState(0, bot.MountSnapshot)));
            Send(recipients, OnStartParameterInfPacket.Build(
                StartParameter.Default(bot.Slot)));
        }
        // OnStartInf carries a 16-bit word at +4 that the client latches into net+894424
        // before clearing its per-stage counters (sub_435000). It is the only
        // server-controlled value in the start reply besides the GO byte, so echo the
        // video-mode flag there. Sent only to the player who asked for it: the word is
        // per-stage state on the receiving client, not a room-wide property.
        short startWord = client.VideoMode ? (short)1 : (short)0;
        if (startWord != 0)
        {
            foreach (Client recipient in recipients)
            {
                recipient.Send(OnStartInfPacket.Build(
                    StartResult.Success,
                    eventId: recipient == client ? startWord : (short)0));
            }
            Logger.Info(client,
                $"Sent OnStartInf GO with start word {startWord} (video mode).");
        }
        else
        {
            Send(recipients, OnStartInfPacket.Build(StartResult.Success));
        }
        Logger.Info(client,
            $"Started disc index {discId} -> song {catalogId} ({selectedSong.Tag}) " +
            $"with game-info field {gameInfo!.ChartType}" +
            (usedFallback ? " using the configured fallback chart." : "."));
    }

    public void CompleteLoad(Client client)
    {
        LocalRoomMember member;
        Client[] peers;
        Client[] recipients;
        bool beginPlay;
        lock (_lock)
        {
            if (!_memberships.TryGetValue(client, out member!) ||
                member.Room.Phase != RoomPlayPhase.Loading)
            {
                return;
            }

            member.IsLoaded = true;
            peers = member.Room.Members
                .Where(value => value != member)
                .Select(value => value.Client)
                .ToArray();
            beginPlay = member.Room.Members.All(value => value.IsLoaded);
            if (beginPlay)
            {
                member.Room.Phase = RoomPlayPhase.Playing;
            }
            recipients = member.Room.Members.Select(value => value.Client).ToArray();
        }

        Send(peers, OnLoadCompleteInfPacket.Build(member.Slot));
        if (beginPlay)
        {
            Send(recipients, OnPlayStartInfPacket.Build());
        }
    }

    public void RelayPlayState(Client client, PlayState state)
    {
        // The client masks its own combo, but OnPlayStateInf stores a remote player's
        // combo verbatim. Strip the sender's mask once; do not apply a recipient key.
        PlayState relayedState = client.CipherSeed is { } seed &&
                                 seed.Length >= LogInReqPacket.SeedSize
            ? state.DecodeMaxComboForRelay(StageResultInfPacket.DeriveJudgmentKey(seed))
            : state;
        byte slot;
        Client[] peers;
        byte[] botSlots;
        Client[] allMembers;
        bool createBattleItem;
        ushort estimatedBattleItemCombo = 0;
        int battleItemRewardNumber = 0;
        lock (_lock)
        {
            if (!_memberships.TryGetValue(client, out LocalRoomMember? member) ||
                member.Room.Phase != RoomPlayPhase.Playing)
            {
                return;
            }

            slot = member.Slot;
            peers = member.Room.Members
                .Where(value => value != member)
                .Select(value => value.Client)
                .ToArray();
            // A render-only bot cannot send its own live state, so its progress bar would
            // sit frozen. Mirror this player's state onto every bot slot, so in a solo-vs-
            // bots room the opponent bars track the player's own play. All real members
            // (here, just the player) receive it.
            botSlots = [.. member.Room.Bots.Select(bot => bot.Slot)];
            allMembers = member.Room.Members.Select(value => value.Client).ToArray();
            createBattleItem = false;
            if (member.Room.Settings.MatchMode == LocalRoomProtocol.ItemBattleMatchMode &&
                client.CipherSeed is { } sessionSeed &&
                sessionSeed.Length >= LogInReqPacket.SeedSize)
            {
                ushort loginKey = StageResultInfPacket.DeriveJudgmentKey(sessionSeed);
                ushort reportedMaxCombo = state.DecodeMaxCombo(loginKey);
                bool hadObservedBreak = member.BattleItemComboRewards.HasObservedBreak;
                int playerRewards = member.BattleItemComboRewards.Observe(
                    reportedMaxCombo, state.BaseScore);
                estimatedBattleItemCombo =
                    member.BattleItemComboRewards.EstimatedCurrentStreakCombo;
                if (!hadObservedBreak && member.BattleItemComboRewards.HasObservedBreak)
                {
                    Logger.Info(client,
                        $"Item battle observed a combo break after song maximum " +
                        $"{reportedMaxCombo}; rearmed the {BattleItemComboInterval}-combo item threshold.");
                }

                // 0xB3 is room-wide: every receiving client marks an item note for every
                // active player. Use the highest reward count in the room, so two players
                // earning the same tier cannot duplicate the synchronized drop.
                int roomRewards = Math.Max(
                    playerRewards,
                    member.Room.Members.Max(value =>
                        value.BattleItemComboRewards.RewardsEarned));
                int scheduledSignals = member.Room.BattleItemSignalsEarned +
                                       member.Room.PendingBattleItemSignals;
                member.Room.PendingBattleItemSignals +=
                    Math.Max(0, roomRewards - scheduledSignals);

                // PlayStateInf is sent only once every five seconds. A dense chart can
                // cross more than one tier between reports; queue the extra signals and
                // drain one per report so repeated 0xB3 packets do not mark the same note.
                if (member.Room.PendingBattleItemSignals > 0)
                {
                    --member.Room.PendingBattleItemSignals;
                    createBattleItem = true;
                    ++member.Room.BattleItemSignalsEarned;
                    battleItemRewardNumber = member.Room.BattleItemSignalsEarned;
                }
            }
        }
        if (peers.Length != 0)
        {
            Send(peers, OnPlayStateInfPacket.Build(slot, relayedState));
        }
        foreach (byte botSlot in botSlots)
        {
            Send(allMembers, OnPlayStateInfPacket.Build(botSlot, relayedState));
        }
        if (createBattleItem)
        {
            // Each receiving client marks the same suitable future chart note with flag
            // 0x20 for every active player (sub_40CD00/sub_426340). Hitting that note is
            // what generates GetItemReq; the chart itself must not be rewritten.
            Send(allMembers, OnCrItemInfPacket.Build());
            Logger.Info(
                $"Item battle room emitted CR-item signal for {BattleItemComboInterval}-combo reward " +
                $"#{battleItemRewardNumber} (estimated current streak " +
                $"{estimatedBattleItemCombo}) to " +
                $"{allMembers.Length} player(s).");
        }
    }

    public void SkipPlay(Client client)
    {
        Client[] recipients;
        Client[] waiting;
        RoomListEntry grid;
        lock (_lock)
        {
            if (!_memberships.TryGetValue(client, out LocalRoomMember? member) ||
                member.Room.Phase != RoomPlayPhase.Playing)
            {
                return;
            }

            // A skip ends the song with no result screen: the client returns straight
            // to select and immediately issues the next ChangeDisc/StartReq (verified
            // in the retail captures). Drop the room back to Waiting here, or
            // StartGame's phase guard silently rejects that StartReq and the client
            // soft-locks with no response.
            member.Room.Phase = RoomPlayPhase.Waiting;
            member.Room.BattleItemSignalsEarned = 0;
            member.Room.PendingBattleItemSignals = 0;
            foreach (LocalRoomMember value in member.Room.Members)
            {
                value.IsLoaded = false;
                value.IsReady = false;
                value.LastResult = null;
                value.Award = StageAward.None;
                value.CourseTotals = null;
                value.BattleItems.Clear();
                value.BattleItemComboRewards.Reset();
            }

            recipients = member.Room.Members.Select(value => value.Client).ToArray();
            waiting = WaitingClients();
            grid = RoomList(member.Room);
        }
        Send(recipients, OnPlaySkipInfPacket.Build());
        Send(waiting, OnRoomInfoInfPacket.Build(grid));
    }

    /// <summary>
    /// Decodes and stores the player's own end-of-song result. The judgment XOR key is
    /// derived from that client's 30-byte login seed. Each room member can have a
    /// different seed, so this must never be stored as room-wide state.
    /// </summary>
    public StageResult? RecordStageResult(Client client, Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ushort judgmentKey = StageResultInfPacket.RecoverJudgmentKey(packet);
        if (client.CipherSeed is { } sessionSeed &&
            sessionSeed.Length >= LogInReqPacket.SeedSize)
        {
            ushort loginKey = StageResultInfPacket.DeriveJudgmentKey(sessionSeed);
            if (loginKey != judgmentKey)
            {
                Logger.Error(client,
                    $"Stage result key 0x{judgmentKey:X4} differs from stored " +
                    $"login key 0x{loginKey:X4}; using the packet-proven key.");
            }
        }
        else
        {
            Logger.Info(client,
                $"Recovered stage result key 0x{judgmentKey:X4} from unused " +
                "judgment bucket 0 (login seed unavailable).");
        }
        lock (_lock)
        {
            if (!_memberships.TryGetValue(client, out LocalRoomMember? member) ||
                member.Room.Phase == RoomPlayPhase.Waiting)
            {
                return null;
            }

            if (member.LastResult != null)
            {
                return member.LastResult;
            }
        }

        StageResult result = StageResultInfPacket.Parse(packet, judgmentKey);
        LocalRoomMember awarded;
        Client[] stillPlaying;
        lock (_lock)
        {
            if (!_memberships.TryGetValue(client, out LocalRoomMember? member) ||
                member.Room.Phase == RoomPlayPhase.Waiting)
            {
                return null;
            }
            member.LastResult = result;
            awarded = member;
            // Only worth announcing while somebody is still on the chart; if this was the
            // last result, FinishPlay broadcasts the complete set immediately after.
            stillPlaying = result.Failed &&
                member.Room.Members.Any(value => value != member && value.LastResult == null)
                ? member.Room.Members
                    .Where(value => value != member)
                    .Select(value => value.Client)
                    .ToArray()
                : [];
        }

        // A player who fails mid-song has to look finished to everyone else RIGHT THEN, the
        // same way a mid-song leaver does. Two things rule out the obvious signals:
        //
        //  * the leaver's 0x51 leaving state runs sub_4347A0, which marks the slot dead AND
        //    erases the joiner from the client's room map - a failed player would vanish
        //    from the room for good;
        //  * a record with struct+46 == 0 (Failed) sets Player+32 = 2, and sub_41F68F
        //    @0x41f802 jumps clean over the icon/nickname/level/gender block whenever
        //    Player+32 == 2, so the whole info window blanks out. That is the client's own
        //    behaviour and no packet can put the data back.
        //
        // sub_428950 runs Player::SetGameOver (sub_424480 -> Panel::OnGameOver, the GAME
        // OVER overlay and the UserIcon_Die swap) for BOTH struct+46 == 0 and struct+46 == 1
        // while the panel exists, but only the 0 case marks the slot dead. Sending Cleared
        // therefore gives the game-over visual with the player's info still on screen.
        // The real clear/fail state follows in FinishPlay's authoritative record, by which
        // point the result scene has taken over the panel.
        if (stillPlaying.Length > 0)
        {
            Send(stillPlaying, OnStageResultExInfPacket.Build(
                awarded.Slot, result, resultStateOverride: StageResultState.Cleared));
            Logger.Info(client,
                $"Slot {awarded.Slot} failed mid-song; notified " +
                $"{stillPlaying.Length} player(s) still on the chart.");
        }

        awarded.Award = PersistStageResult(client, awarded, result);
        return result;
    }

    /// <summary>
    /// Finalizes the song for a room. Returns false when there was nothing to finalize -
    /// the caller then still owes the client an acknowledgement, because PlayOverReq is a
    /// request the client blocks on rather than a notification.
    /// </summary>
    public bool FinishPlay(Client client)
    {
        LocalRoomMember[] members;
        RoomBot[] bots;
        Client[] waiting;
        RoomListEntry grid;
        bool battleMode;
        lock (_lock)
        {
            if (!_memberships.TryGetValue(client, out LocalRoomMember? member) ||
                member.Room.Phase == RoomPlayPhase.Waiting)
            {
                return false;
            }

            // Do not discard the later players' records by finalizing on the first
            // 0x6F. Retail broadcasts the complete occupied-slot result set together.
            // Bots are excluded from this gate: they never send a 0x6F, so waiting on them
            // would wedge the song forever.
            if (member.Room.Members.Any(value => value.LastResult == null))
            {
                return false;
            }

            member.Room.Phase = RoomPlayPhase.Waiting;
            member.Room.BattleItemSignalsEarned = 0;
            member.Room.PendingBattleItemSignals = 0;
            members = member.Room.Members.ToArray();
            bots = [.. member.Room.Bots];
            waiting = WaitingClients();
            grid = RoomList(member.Room);
            // Ranked counts too, even though IsBattleMode (the room-list category) does
            // not include it - see the comment on the override below.
            battleMode = OnRoomInfoInfPacket.IsBattleMode(
                member.Room.Settings.MatchMode) ||
                member.Room.Settings.MatchMode == LocalRoomProtocol.RankedMatchMode;
            foreach (LocalRoomMember value in members)
            {
                // A mission lasts one song.
                value.IsMissionMatch = false;
                value.IsLoaded = false;
                value.IsReady = false;
                value.BattleItems.Clear();
                value.BattleItemComboRewards.Reset();
            }
        }

        Client[] recipients = members.Select(value => value.Client).ToArray();
        foreach (LocalRoomMember member in members)
        {
            AdvanceCourse(member);
        }

        List<(byte Slot, StageResult Result, StageAward Award, StageTotals? Totals,
              bool CarriesCombo)> resultRows = [];
        foreach (LocalRoomMember member in members)
        {
            resultRows.Add((
                member.Slot,
                CourseResultState(member),
                member.Award,
                member.CourseTotals,
                // Only a course carries the combo between charts, so only a course may
                // report one larger than the stage's own notes.
                member.Client.SelectedCourseId is not null));
        }

        // Bots never send a 0x6F. Give every occupied bot slot a synthesized row before
        // ranking so humans and bots participate in the same placement order.
        StageResult reference = members.FirstOrDefault(m => m.IsHost)?.LastResult
            ?? members[0].LastResult!;
        foreach (RoomBot bot in bots)
        {
            resultRows.Add((
                bot.Slot,
                SynthesizeBotResult(reference, bot),
                StageAward.None,
                null,
                false));
        }

        // A room has sides only when its occupants actually hold one. With sides, the
        // result is a team verdict - everyone on the winning side wins together; without
        // them it is the 1st/2nd/3rd ladder. Sending the ladder for a team battle made
        // team-mates place against each other.
        Dictionary<byte, byte> teams = members
            .Select(value => (value.Slot, value.Team))
            .Concat(bots.Select(value => (value.Slot, value.Team)))
            .ToDictionary(entry => entry.Slot, entry => entry.Team);
        bool teamBattle = teams.Values.Any(
            team => team != LocalRoomProtocol.SingleTeam);

        IReadOnlyDictionary<byte, byte>? placements = battleMode
            ? teamBattle
                ? StagePlacementPolicy.AssignTeams(resultRows.Select(row =>
                    (row.Slot, teams[row.Slot], row.Result)))
                : StagePlacementPolicy.Assign(
                    resultRows.Select(row => (row.Slot, row.Result)))
            : null;

        // Retail's end-of-song sequence is one 0x70 record for every occupied slot,
        // followed by the completion signals. Every recipient needs the complete set,
        // including the record for its own slot.
        foreach ((byte slot, StageResult result, StageAward award, StageTotals? totals,
                  bool carriesCombo) in resultRows.OrderBy(row => row.Slot))
        {
            Send(recipients, OnStageResultExInfPacket.Build(
                slot,
                result,
                award,
                totals,
                placement: placements?[slot] ?? OnStageResultExInfPacket.UnrankedPlacement,
                carriesCombo: carriesCombo,
                // struct+46 MUST stay the clear/fail state. It also feeds the 순위 frame
                // (0x4509fd: frame = struct+46 + 1), but it is NOT free to use as a rank:
                // sub_428950 tests `a2[46] == 0` for FAILED, so sending placement 0 for the
                // winner marked them failed and the result scene did not render at all.
                // TESTED - do not put a placement here again. The ladder needs the outer
                // branch at 0x45089b (player record +0x16C) to select the placement path;
                // that value is copied out of the PLAY record by sub_44F299 and nothing in
                // the result packet reaches it.
                // A PLAYER WHO STILL HAS HP MUST NOT BE ENDED THROUGH THE DEATH PATH.
                // sub_428950 runs Player::SetGameOver for struct+46 == 1 (cleared) as well
                // as 0 (failed), and Panel::OnGameOver ends with an UNCONDITIONAL swap to
                // UserIcon_Die - so answering the game-over request with an ordinary
                // "cleared" flashed the death graphic at someone who had just passed.
                // 2 and 3 match neither arm, so:
                //   failed (gauge empty) -> the death path, which is correct for them
                //   course clear         -> 3, which the course result scene needs anyway
                //   any other clear      -> 2, ending them without the death panel
                resultStateOverride: ResultStateFor(battleMode, result)));
        }
        // The retail trace sends 0xC0 to the player immediately after their
        // stage result. Its network handler treats it as a completion signal;
        // the 19-byte body remains opaque, so replay the captured bytes.
        Send(recipients, OnMissionStandItemInfPacket.BuildCaptured());
        Send(recipients, OnPlayOverInfPacket.Build());
        Send(waiting, OnRoomInfoInfPacket.Build(grid));
        foreach (LocalRoomMember member in members.Where(value => !value.IsHost))
        {
            Send(recipients, OnReadyInfPacket.Build(new RoomReadyUpdate(
                false, member.ConnectionId, member.Client.UserId ?? 0)));
        }

        RecordMatch(
            members,
            bots,
            [.. resultRows.Select(row => (row.Slot, row.Result, row.Award, row.Totals))],
            placements,
            teams,
            teamBattle);
        return true;
    }

    /// <summary>
    /// Publishes the finished match to <see cref="MatchHistory"/> for the local status API.
    ///
    /// Telemetry only, and last: it runs after every packet has gone out and swallows its
    /// own failures, because nothing here is allowed to wedge a song. Solo runs are skipped
    /// - the score feed already carries those, and a match with one line has no verdict.
    /// </summary>
    private void RecordMatch(
        IReadOnlyList<LocalRoomMember> members,
        IReadOnlyList<RoomBot> bots,
        IReadOnlyList<(byte Slot, StageResult Result, StageAward Award, StageTotals? Totals)>
            resultRows,
        IReadOnlyDictionary<byte, byte>? placements,
        IReadOnlyDictionary<byte, byte> teams,
        bool teamBattle)
    {
        if (MatchHistory == null || resultRows.Count < 2 || members.Count == 0)
        {
            return;
        }

        try
        {
            LocalRoomState room = members[0].Room;
            Dictionary<byte, string> names = [];
            foreach (LocalRoomMember member in members)
            {
                // Read through the client's own store, never PlayerStoreOr: a member who
                // dropped mid-song still has a row here and must not throw.
                names[member.Slot] =
                    member.Client.PlayerStore?.Read(profile => profile.Nickname) ??
                    string.Empty;
            }

            HashSet<byte> botSlots = [.. bots.Select(bot => bot.Slot)];
            foreach (RoomBot bot in bots)
            {
                names[bot.Slot] = bot.Roster.Nickname;
            }

            LocalRoomMember? host = members.FirstOrDefault(member => member.IsHost);
            ushort? courseId = host?.Client.SelectedCourseId;

            MatchHistory.Record(id => new MatchStatus(
                MatchId: id,
                FinishedUtc: DateTime.UtcNow.ToString("O"),
                Channel: Channel.Name,
                RoomIndex: room.Index,
                RoomTitle: RoomPacketFields.DecodeTitle(room.Settings.TitleField),
                // As above: the channel decides the key mode, and the room setting is a
                // flag rather than a count.
                KeyMode: (byte)Channel.KeyMode,
                MatchMode: room.Settings.MatchMode,
                GameType: room.Settings.GameType,
                TeamBattle: teamBattle,
                Ranked: placements != null,
                // Both are 0-based indexes internally; the API publishes catalog ids.
                SongId: host is { HasDisc: true } ? host.DiscId + 1u : null,
                CourseId: courseId is { } course ? (ushort)(course + 1) : null,
                Players: resultRows
                    .OrderBy(row => placements?.GetValueOrDefault(row.Slot) ?? row.Slot)
                    .ThenBy(row => row.Slot)
                    .Select(row => new MatchPlayerStatus(
                        Nickname: names.GetValueOrDefault(row.Slot) ?? string.Empty,
                        Slot: row.Slot,
                        Team: teams.GetValueOrDefault(row.Slot),
                        Placement: placements != null && placements.TryGetValue(
                            row.Slot, out byte place)
                            ? place
                            : MatchPlayerStatus.Unranked,
                        Winner: placements != null &&
                                placements.GetValueOrDefault(row.Slot, (byte)1) == 0,
                        Failed: row.Result.Failed,
                        Score: row.Totals?.Score ?? row.Result.Score,
                        Accuracy: row.Totals?.Accuracy ?? row.Result.Accuracy,
                        MaxCombo: row.Totals?.MaxCombo ?? row.Result.MaxCombo,
                        Breaks: row.Totals?.Breaks ?? row.Result.Breaks,
                        FullCombo: row.Result.FullCombo,
                        IsBot: botSlots.Contains(row.Slot)))
                    .ToArray()));
        }
        catch (Exception ex)
        {
            Logger.Error($"Match history could not record the finished room: {ex.Message}");
        }
    }

    /// <summary>
    /// Item battle's in-game item loop. MatchMode 3 turns on three client requests, each
    /// gated behind a pending flag that ONLY its reply clears - leave one unanswered and
    /// that request never fires again for the rest of the song:
    ///   0xB4 GetItemReq      (sub_4362A0, flag net+894520, cleared by 0xB6) - hit an item
    ///                        note holding nothing
    ///   0xB7 ItemLevelUpReq  (sub_436400, flag net+894524, cleared by 0xB9) - hit an item
    ///                        note while already holding one below max level
    ///   0xBA UseItemReq      (sub_436510, flag net+894528, cleared by 0xBC) - fire the
    ///                        held item at a target slot (keys 42..47 -> players 0..5,
    ///                        sub_407050, which only runs when MatchMode == 3)
    /// Returns false when the request is not an in-game battle one, so the caller can fall
    /// back to the lobby/inventory meaning of the same id.
    /// </summary>
    public bool TryHandleBattleGetItem(Client client)
    {
        ArgumentNullException.ThrowIfNull(client);
        LocalRoomMember member;
        Client[] recipients;
        BattleItemState item;
        string itemName;
        lock (_lock)
        {
            if (!TryGetBattlePlayerLocked(client, out LocalRoomMember? found))
            {
                return false;
            }

            member = found!;
            if (BattleItemProbe is var (forcedItemId, forcedLevel))
            {
                item = new BattleItemState(forcedItemId, forcedLevel);
            }
            else
            {
                BattleItemDefinition drop = BattleItemCatalog.RandomDrop();
                item = new BattleItemState(drop.PickupWireId, 0);
            }
            if (member.BattleItems.Count == BattleItemCapacity)
            {
                // sub_426BC0/sub_426C20 discard the oldest entry before appending when
                // the client's four-entry queue is full. Mirror that rotation exactly.
                member.BattleItems.RemoveAt(0);
            }
            member.BattleItems.Add(item);
            itemName = BattleItemCatalog.TryGet(
                item.PickupId, out BattleItemDefinition? definition)
                ? definition!.Name
                : "unknown probe";
            recipients = member.Room.Members.Select(value => value.Client).ToArray();
        }

        Send(recipients, OnGetItemAckPacket.Build(
            member.Slot, item.PickupId, item.Level));
        Logger.Info(client,
            $"Item battle: slot {member.Slot} picked up item {item.PickupId} " +
            $"({itemName}) " +
            $"at level {item.Level} ({member.BattleItems.Count}/{BattleItemCapacity}).");
        return true;
    }

    public bool TryHandleBattleLevelUp(Client client)
    {
        ArgumentNullException.ThrowIfNull(client);
        LocalRoomMember member;
        Client[] recipients;
        byte newLevel;
        short pickupId;
        lock (_lock)
        {
            if (!TryGetBattlePlayerLocked(client, out LocalRoomMember? found))
            {
                return false;
            }

            member = found!;
            if (member.BattleItems.Count == 0 || member.BattleItems[0].Level >= 2)
            {
                client.Send(ItemFailurePackets.BuildItemLevelUpFail());
                return true;
            }

            BattleItemState current = member.BattleItems[0];
            pickupId = current.PickupId;
            newLevel = (byte)(current.Level + 1);
            member.BattleItems[0] = current with { Level = newLevel };
            recipients = member.Room.Members.Select(value => value.Client).ToArray();
        }

        Send(recipients, OnItemLevelUpAckPacket.Build(
            member.Slot, queueIndex: 0, level: newLevel));
        Logger.Info(client,
            $"Item battle: slot {member.Slot} levelled item {pickupId} " +
            $"to {newLevel}.");
        return true;
    }

    public bool TryHandleBattleUseItem(Client client, byte targetSlot)
    {
        ArgumentNullException.ThrowIfNull(client);
        LocalRoomMember member;
        Client[] recipients;
        BattleItemState item;
        BattleItemUseEffect effect;
        string itemName;
        lock (_lock)
        {
            if (!TryGetBattlePlayerLocked(client, out LocalRoomMember? found))
            {
                return false;
            }

            member = found!;
            if (member.BattleItems.Count == 0 ||
                !member.Room.OccupiedSlots().Contains(targetSlot))
            {
                client.Send(ItemFailurePackets.BuildUseItemFail());
                return true;
            }

            item = member.BattleItems[0];
            if (!BattleItemCatalog.TryCreateUseEffect(
                    item.PickupId,
                    member.Slot,
                    targetSlot,
                    item.Level,
                    out BattleItemUseEffect? mappedEffect))
            {
                // The probe command can deliberately award unknown ids. Do not send an
                // effect the client has no mapped icon/routine for; clear pending instead.
                client.Send(ItemFailurePackets.BuildUseItemFail());
                Logger.Error(client,
                    $"Item battle: pickup {item.PickupId} has no mapped use effect yet.");
                return true;
            }

            member.BattleItems.RemoveAt(0);
            recipients = member.Room.Members.Select(value => value.Client).ToArray();
            effect = mappedEffect!;
            itemName = BattleItemCatalog.TryGet(
                item.PickupId, out BattleItemDefinition? definition)
                ? definition!.Name
                : "unknown probe";
        }

        Logger.Info(client,
            $"Item battle: slot {member.Slot} used pickup {item.PickupId} " +
            $"({itemName}, level {item.Level}) on slot {targetSlot}.");
        // The ack clears the sender's pending flag; every member gets it so the effect can
        // be shown on the target's panel.
        Send(recipients, OnUseItemAckPacket.Build(effect));
        return true;
    }

    /// <summary>
    /// The caller's room member when they are mid-song in an ITEM battle room (MatchMode
    /// 3); null otherwise, which means the request carries its ordinary lobby meaning.
    /// </summary>
    private bool TryGetBattlePlayerLocked(
        Client client,
        out LocalRoomMember? member)
    {
        return _memberships.TryGetValue(client, out member) &&
               member.Room.Settings.MatchMode == LocalRoomProtocol.ItemBattleMatchMode &&
               member.Room.Phase == RoomPlayPhase.Playing;
    }

    /// <summary>
    /// Forces pickups to one item id and initial level for live probing. Null restores
    /// the verified randomized drop pool.
    /// </summary>
    public static (short PickupId, byte Level)? BattleItemProbe { get; set; }

    /// <summary>
    /// Answers PlayOverReq. A failed player sends this while the other room members are
    /// still playing. In that case its pending flag deliberately remains set until the
    /// room-wide OnPlayOverInf broadcast: replying early switches only that client into
    /// an incomplete result scene and can crash it. A request received after the room was
    /// already finalized is the only case that needs an individual acknowledgement.
    /// </summary>
    public void PlayOver(Client client)
    {
        bool activeRoom;
        lock (_lock)
        {
            activeRoom = _memberships.TryGetValue(client, out LocalRoomMember? member) &&
                         member.Room.Phase != RoomPlayPhase.Waiting;
        }

        if (activeRoom)
        {
            // This finalizes and broadcasts only when every human has submitted 0x6F.
            // Otherwise wait for the last survivor instead of ending this client early.
            FinishPlay(client);
            return;
        }

        client.Send(OnPlayOverInfPacket.Build());
    }

    /// <summary>
    /// Awards this run's money and experience and returns what the result screen needs to
    /// report: the money credited, whether the level rose, and whether the score beat the
    /// stored best. The best and the level are both read before they are updated, since
    /// the comparison is against the previous values.
    /// </summary>
    private StageAward PersistStageResult(
        Client client,
        LocalRoomMember member,
        StageResult result)
    {
        if (client.VideoMode)
        {
            // The client played this itself. Nothing is persisted and nothing is paid, so
            // an auto-played run cannot reach the rankings or the wallet.
            Logger.Info(client,
                "Discarding the stage result: this run was started in video mode.");
            return new StageAward(0, false, false);
        }

        float accuracy = float.IsFinite(result.Accuracy)
            ? Math.Clamp(result.Accuracy, 0f, 100f)
            : 0f;
        GrantResultDisc(client, StageResultAward.Evaluate(accuracy, result.Failed));
        (uint Money, uint Experience) reward = StageRewardPolicy.Calculate(result);
        // MAX is the currency, so an item's `max` is a bonus on the money credited here,
        // and `exp` a bonus on the experience - neither is anything the client computes.
        // Taken from what they were WEARING plus whatever boosters were armed for this
        // run; the boosters are spent below whether or not the run was cleared.
        byte[] worn = Players(client).Read(
            profile => profile.Inventory.MountLoadout());
        EquipmentBonus bonus = BonusFor(client, worn);
        reward = (
            AddSaturating(reward.Money, bonus.Money),
            AddSaturating(reward.Experience, bonus.Experience));
        // Premium pays a percentage on the whole payout, base and item bonuses alike.
        bool premium = Players(client).Read(
            profile => AccountClassInfo.IsPremium(profile.AccountClass));
        if (premium)
        {
            reward = (
                StageRewardPolicy.AddPercent(reward.Money, PremiumRewardBonusPercent),
                StageRewardPolicy.AddPercent(
                    reward.Experience, PremiumRewardBonusPercent));
        }

        DateTimeOffset playedAt = DateTimeOffset.UtcNow;
        // Read the room's mode once, out here, rather than inside the update callback.
        bool ranked = member.Room.Settings.MatchMode == LocalRoomProtocol.RankedMatchMode;
        // The song belongs to the ROOM, and the room's disc is the HOST's selection. Only
        // the host carries one, so reading each member's own disc recorded "no song" for
        // everybody else - which is why their plays showed up as an unknown song and could
        // not be counted towards that song's scores.
        LocalRoomMember? discOwner = member.HasDisc
            ? member
            : member.Room.Members.FirstOrDefault(
                other => other.IsHost && other.HasDisc);
        uint? songId = discOwner?.DiscId;
        ushort? courseId = client.SelectedCourseId;
        int? courseStage = courseId.HasValue ? client.CourseStage : null;
        StageProgressUpdate update = Players(client).UpdateWithStageScore(profile =>
        {
            LocalPlayerProgress progress = profile.Progress;
            uint previousLevel = progress.Level;
            // The SCORE tab has two rows, 프리모드 (free) and 랭킹모드 (ranking), each split
            // by key mode. A ranked run belongs in the ranking row: sending every score to
            // the freemode field left the ranking row reading 0 no matter how much ranked
            // was played.
            bool sevenKey = Channel.KeyMode == SongKeyMode.SevenKey;
            // "내 최고 기록" on the result screen is the best for the row this run counts
            // towards, so the NEW RECORD banner has to compare against that same field.
            uint previousBest = ranked
                ? sevenKey ? progress.RankingBest7Key : progress.RankingBest5Key
                : sevenKey ? progress.FreemodeBest7Key : progress.FreemodeBest5Key;
            progress.MaxCombo = Math.Max(progress.MaxCombo, result.MaxCombo);
            progress.HighestAccuracy = Math.Max(progress.HighestAccuracy, accuracy);
            if (ranked)
            {
                if (sevenKey)
                {
                    progress.RankingBest7Key =
                        Math.Max(progress.RankingBest7Key, result.Score);
                }
                else
                {
                    progress.RankingBest5Key =
                        Math.Max(progress.RankingBest5Key, result.Score);
                }
            }
            else if (sevenKey)
            {
                progress.FreemodeBest7Key =
                    Math.Max(progress.FreemodeBest7Key, result.Score);
            }
            else
            {
                progress.FreemodeBest5Key =
                    Math.Max(progress.FreemodeBest5Key, result.Score);
            }

            // Running mean over every completed play, taken before the win/loss counter
            // moves so the new result is not counted twice.
            uint plays = progress.Wins + progress.Losses + progress.Draws;
            progress.AverageAccuracy =
                (progress.AverageAccuracy * plays + accuracy) / (plays + 1);

            if (result.Failed)
            {
                progress.Losses++;
            }
            else
            {
                progress.Wins++;
            }

            // Saturate rather than wrap: the client renders these as plain counters.
            progress.Money = AddSaturating(progress.Money, reward.Money);
            progress.Experience = AddSaturating(progress.Experience, reward.Experience);
            // A booster lasts exactly one song; it has now been paid out.
            profile.Inventory.ActiveBoosters.Clear();

            // Level up against the client's own curve so its EXP bar agrees with the
            // level it is showing.
            while (progress.Level < ExperienceCurve.MaxLevel)
            {
                uint required = ExperienceCurve.Required(progress.Level);
                if (required == uint.MaxValue || progress.Experience < required)
                {
                    break;
                }
                progress.Experience -= required;
                progress.Level++;
            }

            StageProgressUpdate progressUpdate = new(
                profile.WireUserId,
                progress.Money,
                progress.Experience,
                progress.Level,
                progress.Wins,
                progress.Losses,
                progress.Draws,
                progress.Level > previousLevel,
                result.Score > previousBest,
                UserStatisticsBlock.Build(progress));
            StageScoreRecord score = new()
            {
                UserId = profile.UserId,
                PlayedAt = playedAt,
                SongId = songId,
                CourseId = courseId,
                CourseStage = courseStage,
                RoomId = member.Room.Index,
                KeyMode = (byte)Channel.KeyMode,
                Difficulty = member.Difficulty,
                MatchMode = member.Room.Settings.MatchMode,
                SessionToken = result.SessionToken,
                ClientFlags = result.ClientFlags,
                TotalNotes = result.TotalNotes,
                Judgments = result.Judgments.ToArray(),
                Gauge = result.Gauge,
                EncodedCombo = result.EncodedCombo,
                CurrentCombo = result.CurrentCombo,
                MaxCombo = result.MaxCombo,
                AuxiliaryValue = result.AuxiliaryValue,
                ResultState = result.ResultState,
                Tail = result.Tail,
                Failed = result.Failed,
                FullCombo = result.FullCombo,
                NotesHit = result.NotesHit,
                Breaks = result.Breaks,
                Score = result.Score,
                BonusScore = result.BonusScore,
                Accuracy = accuracy,
                Rank = result.Rank,
                MoneyAwarded = reward.Money,
                ExperienceAwarded = reward.Experience
            };
            return (progressUpdate, score);
        });

        // These three are never sent anywhere else, so without them money, level and the
        // win/loss record only appear after a relogin.
        client.Send(OnUpdateUserPropertyMoneyInfPacket.Build(
            update.UserId, update.Money));
        client.Send(OnUpdateUserPropertyLevelInfPacket.Build(
            update.UserId, update.Experience, update.Level));
        client.Send(OnUpdateUserPropertyRecordInfPacket.Build(
            update.UserId, update.Wins, update.Losses, update.Draws));
        // Best scores, max combo and accuracy live at login-block +89..+128, and nothing
        // else refreshes them - so without this the SCORE tab and the result screen's
        // "my best" keep showing whatever was true at login, even right after you beat it.
        client.Send(OnUpdateUserPropertyMiscInfPacket.Build(
            update.UserId, update.Statistics));

        Logger.Info(client,
            $"Awarded {reward.Money} money and {reward.Experience} exp " +
            $"({(result.Failed ? "failed" : "cleared")}" +
            $"{(result.FullCombo ? ", full combo" : string.Empty)}): " +
            $"money={update.Money} level={update.Level} exp={update.Experience} " +
            $"record={update.Wins}/{update.Losses}/{update.Draws}" +
            $"{(update.LeveledUp ? ", LEVEL UP" : string.Empty)}" +
            $"{(update.NewRecord ? ", NEW RECORD" : string.Empty)}.");
        return new StageAward(reward.Money, update.LeveledUp, update.NewRecord)
        {
            Experience = reward.Experience
        };
    }

    /// <summary>Renders the 24-byte effector config as its 4 (index, a, b) triples.</summary>
    private static string DescribeEffectors(ReadOnlySpan<byte> configuration)
    {
        if (configuration.Length != GameplayProtocol.EffectorConfigurationSize)
        {
            return $"<{configuration.Length} bytes>";
        }

        string[] slots = new string[4];
        for (int slot = 0; slot < slots.Length; slot++)
        {
            ReadOnlySpan<byte> triple = configuration.Slice(slot * 6, 6);
            slots[slot] =
                $"[{BinaryPrimitives.ReadUInt16LittleEndian(triple)}," +
                $"{BinaryPrimitives.ReadUInt16LittleEndian(triple[2..])}," +
                $"{BinaryPrimitives.ReadUInt16LittleEndian(triple[4..])}]";
        }
        return string.Join(' ', slots);
    }

    /// <summary>
    /// The course ids offered to the client. The profile holds the unlocked set, but a
    /// course only makes sense to offer when the client's script actually defines it -
    /// selecting one the server cannot resolve just fails at start time - so the list is
    /// narrowed to the catalog whenever one is loaded.
    /// </summary>
    private ushort[] AvailableCourseIds(Client client)
    {
        if (UnlockAllCourses && _courses.Count > 0)
        {
            return _courses.Ids().ToArray();
        }

        ushort[] unlocked = Players(client).Read(profile =>
            profile.AvailableCourseIds.ToArray());
        if (_courses.Count == 0)
        {
            return unlocked;
        }

        ushort[] playable = unlocked
            .Where(id => _courses.TryGet(id, out _))
            .ToArray();
        if (playable.Length != unlocked.Length)
        {
            Logger.Info(
                $"{unlocked.Length - playable.Length} unlocked course id(s) are absent " +
                $"from {_courses.SourcePath} and were not offered.");
        }
        return playable;
    }

    /// <summary>
    /// A course stage's result has to say CourseCleared, not the ordinary Cleared: the
    /// course total-result scene reads struct+46 of the final stage's record and treats
    /// anything other than 3 as a failed course. Normal play is untouched.
    /// </summary>
    /// <summary>
    /// Builds a bot's end-of-song result from the host's own play. <see cref="BotOutcome.Mirror"/>
    /// copies it verbatim (so the bot looks like it played the same run); Win and Lose keep
    /// the host's clear/fail state - the field with the fragile encoding - and only rewrite
    /// the judgment counters and combo, which is what actually drives the battle score.
    /// </summary>
    private static StageResult SynthesizeBotResult(StageResult reference, RoomBot bot)
    {
        // Every bot record must carry a DISTINCT identity, or the result screen's leaderboard
        // collapses them onto one row - all "rank 1", overwriting each other - because they
        // share the host's session token. Key it off the bot's connection id.
        uint token = reference.SessionToken ^ ((uint)bot.ConnectionId << 8 | bot.Slot);

        if (bot.Outcome == BotOutcome.Mirror)
        {
            // Mirror the host's play, but nudge the combo down by the slot so no two bots -
            // and no bot and the host - tie exactly; identical scores also render as a pile
            // of joint firsts. The order (host, then bots by slot) stays deterministic.
            uint combo = reference.MaxCombo > bot.Slot
                ? reference.MaxCombo - bot.Slot
                : reference.MaxCombo;
            return reference with
            {
                SessionToken = token,
                CurrentCombo = combo,
                MaxCombo = combo
            };
        }

        // Distribute the same number of scored notes the host had, so the bot's chart looks
        // the same length. A perfect judgment (index 12, weight 100) maximises accuracy and
        // score; the low index (weight 1) with a slab of breaks (index 1) minimises them.
        int notes = Math.Max(
            reference.TotalNotes,
            reference.Judgments.Count == StageResult.JudgmentCount
                ? reference.Judgments.Sum(j => (int)j)
                : 0);
        if (notes <= 0)
        {
            notes = 100;
        }

        ushort[] judgments = new ushort[StageResult.JudgmentCount];
        uint maxCombo;
        if (bot.Outcome == BotOutcome.Win)
        {
            judgments[12] = (ushort)Math.Min(notes, ushort.MaxValue); // all perfect
            maxCombo = (uint)Math.Max(0, notes - bot.Slot);           // full combo, distinct
        }
        else
        {
            int breaks = Math.Max(1, notes / 3);
            judgments[1] = (ushort)Math.Min(breaks, ushort.MaxValue);      // misses
            judgments[2] = (ushort)Math.Min(notes - breaks, ushort.MaxValue); // weak hits
            maxCombo = (uint)Math.Max(0, notes / 8 - bot.Slot);
        }

        return reference with
        {
            SessionToken = token,
            TotalNotes = (ushort)Math.Min(notes, ushort.MaxValue),
            Judgments = judgments,
            CurrentCombo = maxCombo,
            MaxCombo = maxCombo
        };
    }

    /// <summary>
    /// The struct+46 value to send, chosen so the client only runs its game-over sequence
    /// for a player who actually died. Returning null lets the builder derive it from the
    /// result itself (Failed for an empty gauge, CourseCleared for a passed course).
    /// </summary>
    private static byte? ResultStateFor(bool battleMode, StageResult result) =>
        battleMode ? StageResultState.Battle
        : result.Failed || result.ResultState == StageResultState.CourseCleared ? null
        : StageResultState.Battle;

    private StageResult CourseResultState(LocalRoomMember member)
    {
        StageResult result = member.LastResult!;
        // CourseTotals is set only on a terminal stage (last stage or a fail); a mid-course
        // stage has none and just reports a normal clear. A failed final stage returns here
        // too - a course is never "cleared" on a failed stage.
        if (result.Failed ||
            member.CourseTotals is not StageTotals totals ||
            member.Client.SelectedCourseId is not ushort courseId ||
            !_courses.TryGet(courseId, out CourseDefinition? course) || course == null)
        {
            return result;
        }

        if (!MeetsObjectives(course, totals))
        {
            Logger.Info(member.Client,
                $"Course \"{course.Name}\" objectives not met; reporting a normal clear.");
            return result;
        }

        return result with { ResultState = StageResultState.CourseCleared };
    }

    /// <summary>
    /// Judges a course's [Clear] objectives against the ACCUMULATED course totals - the
    /// exact figures the server sends in the closing 0x70 and the client's total-result
    /// scene (sub_4963E3) draws its per-objective SUCCESS/FAIL rows from. Judging the final
    /// stage's own result instead (the old behaviour) let the server stamp "failed" while
    /// the on-screen rows all said "met", because combo and score span the whole course.
    /// </summary>
    private static bool MeetsObjectives(CourseDefinition course, StageTotals totals)
    {
        CourseObjectives objectives = course.Objectives;
        double accuracy = double.IsFinite(totals.Accuracy) ? totals.Accuracy : 0;
        // Accuracy alone treats 1 as "no condition"; the rest use 0.
        return Met(objectives.Accuracy, accuracy, inactiveValue: 1) &&
               Met(objectives.Score, (double)totals.Score + totals.BonusScore) &&
               Met(objectives.Breaks, totals.Breaks) &&
               Met(objectives.Combo, totals.MaxCombo);

        static bool Met(CourseCondition condition, double actual, uint inactiveValue = 0) =>
            !condition.IsActive(inactiveValue) || condition.IsMet(actual);
    }

    /// <summary>
    /// Moves a course one stage forward after a song. The client never tells the server
    /// which stage it is on, so progress is counted here; finishing the last stage posts
    /// the run to the course ranking board and starts the course over.
    /// </summary>
    /// <summary>
    /// Modes that cannot be played alone. SCORE and ITEM battle are contests against
    /// another player; free play and COURSE are solo by nature, and RANKED is solo too
    /// despite sitting in a room - it is three songs scored on their own.
    /// </summary>
    private static bool RequiresOpponent(byte matchMode) =>
        matchMode is LocalRoomProtocol.ScoreBattleMatchMode
                  or LocalRoomProtocol.ItemBattleMatchMode;

    /// <summary>
    /// Validates the SIDES of a battle, or null when the room is fine to start.
    ///
    /// A room is a team battle the moment anyone holds a real side - the same test the
    /// result path uses to pick the team layout. Once it is one, the client has three
    /// rejections it knows how to show and the server was sending none of them, so a
    /// one-sided or lopsided battle started and then resolved nonsensically.
    ///
    /// Bots count as participants: they occupy a slot, hold a side and play.
    /// </summary>
    private static StartResult? TeamRejection(LocalRoomState room)
    {
        List<byte> sides =
        [
            .. room.Members.Select(member => member.Team),
            .. room.Bots.Select(bot => bot.Team)
        ];
        if (sides.All(side => side == LocalRoomProtocol.SingleTeam))
        {
            // Nobody picked a side: an ordinary free-for-all battle, not a team one.
            return null;
        }

        // Someone is on a side and someone else is not - there is no sensible way to
        // score that, and the client has a code for it.
        if (sides.Any(side => side == LocalRoomProtocol.SingleTeam))
        {
            return StartResult.TeamHasInsufficientPlayers;
        }

        List<IGrouping<byte, byte>> teams = [.. sides.GroupBy(side => side)];
        if (teams.Count < 2)
        {
            return StartResult.TeamBattleRequiresTwoTeams;
        }
        if (teams.Select(team => team.Count()).Distinct().Count() > 1)
        {
            return StartResult.TeamPlayerCountMismatch;
        }
        return null;
    }

    private void AdvanceCourse(LocalRoomMember member)
    {
        Client client = member.Client;
        if (client.SelectedCourseId is not ushort courseId ||
            !_courses.TryGet(courseId, out CourseDefinition? course) || course == null)
        {
            return;
        }

        // Accumulate before any early exit so a failed final stage still reports the
        // course's real totals rather than that one stage's.
        StageResult? stage = member.LastResult;
        client.CourseScore = AddSaturating(client.CourseScore, stage?.Score ?? 0);
        client.CourseBonusScore = AddSaturating(
            client.CourseBonusScore, stage?.BonusScore ?? 0);
        client.CourseNotesHit = AddSaturating(client.CourseNotesHit, stage?.NotesHit ?? 0);
        client.CourseBreaks = AddSaturating(client.CourseBreaks, stage?.Breaks ?? 0);
        // The CLIENT does the carrying and reports the ACCUMULATED combo, not a per-stage
        // one: 0x6F's MaxCombo is player+216, the raw high-water mark of a run that already
        // includes whatever OnStartParameterInf seeded it with. A three-stage full combo of
        // 172/268/330 notes reports 172, then 440, then 770 - so the course max is simply
        // the largest reported value. Adding the carry-in here would double count it
        // (440 + 770 = 1210 instead of the true 770).
        client.CourseMaxCombo = Math.Max(client.CourseMaxCombo, stage?.MaxCombo ?? 0);
        if (stage != null && float.IsFinite(stage.Accuracy))
        {
            client.CourseAccuracySum += stage.Accuracy;
        }
        client.CourseStagesPlayed++;

        // A failed stage ends the course where it stands rather than advancing.
        if (member.LastResult is { Failed: true })
        {
            Logger.Info(client,
                $"Course \"{course.Name}\" failed at stage {client.CourseStage + 1}" +
                $"/{course.Stages.Count}.");
            member.CourseTotals = CourseTotalsFor(client);
            member.CarriedCombo = 0;
            client.ResetCourseProgress();
            return;
        }

        client.CourseStage++;
        if (client.CourseStage < course.Stages.Count)
        {
            // The combo the player was ON at the end of the stage is what continues into
            // the next chart - CurrentCombo, not the stage's best run. A stage that ended
            // on a break carries nothing, exactly like a break mid-song.
            member.CarriedCombo = stage?.CurrentCombo ?? 0;
            Logger.Info(client,
                $"Course \"{course.Name}\" advanced to stage " +
                $"{client.CourseStage + 1}/{course.Stages.Count}.");
            return;
        }

        // The course's own totals, which are also what the final 0x70 now carries - so the
        // ranking board and the Total Result panel show the same numbers.
        StageTotals totals = CourseTotalsFor(client);
        member.CourseTotals = totals;
        // The run is over; the next course must start from zero, not inherit this combo.
        member.CarriedCombo = 0;
        uint courseScore = AddSaturating(totals.Score, totals.BonusScore);
        bool passed = MeetsObjectives(course, totals);
        CourseRecord record = Players(client).RecordCourseClear(
            courseId, (byte)Channel.KeyMode, courseScore, totals.MaxCombo, passed);
        // REACHING THE LAST STAGE IS NOT CLEARING THE COURSE. [Clear] carries accuracy,
        // score, break and combo conditions, and the payout belongs to meeting them - the
        // published description is explicit that discs come from "satisfying conditions
        // such as clearing a specific course". The server already computes this verdict for
        // the result screen (CourseResultState sends CourseCleared only when it passes), it
        // just was not gating the rewards on it, so a run that missed every objective still
        // collected the disc, the items and the Max/Exp bonus.
        Logger.Info(client,
            $"Course \"{course.Name}\" {(passed ? "CLEARED" : "finished but FAILED its " +
                "objectives")}: score {courseScore}, " +
            $"combo {totals.MaxCombo}, MAX {totals.NotesHit}, breaks {totals.Breaks}, " +
            $"accuracy {totals.Accuracy:F2} " +
            $"(best {record.Score}/{record.Combo}, {record.Clears} clear(s)).");
        TimedInventoryItem? courseItem = null;
        if (passed)
        {
            if (GrantCourseRewards(client, course, member.Award.Money,
                    member.Award.Experience))
            {
                // The result record is sent after AdvanceCourse returns. Include a level
                // gained from the clear bonus in the same server-owned level-up flag.
                member.Award = member.Award with { LeveledUp = true };
            }
            courseItem = GrantCourseItem(client, course);
            GrantCourseDisc(client, course);
        }
        else
        {
            SendSystemChat(client,
                $"\"{course.Name}\" objectives not met - no course reward.");
        }
        RefreshCourseAvailability(client);
        // OnCourseListInf resets net+0xDA8F4 to 0xFFFF in sub_4328E0. Announce the
        // selected item AFTER that refresh so sub_432B90 leaves it available for the
        // total-result scene (sub_494FFE) that follows this method.
        if (courseItem != null)
        {
            client.Send(OnPostCourseItemReqPacket.Build(
                courseItem.ItemId, courseItem.Expiration));
        }
        client.ResetCourseProgress();
    }

    /// <summary>
    /// Awards the course's [ClearRes] DiscNum. Those codes (1056+) sit in the 0x400..0x43F
    /// event/disc band that sub_465AD1 reads out of the PRIZE/COLLECTION block, so the
    /// "reward disc" is a collection entry rather than anything in the song catalog.
    /// Awarding one twice would show a duplicate row, so a repeat clear is a no-op.
    /// </summary>
    /// <summary>
    /// Persists the exact disc/MAX medal displayed by the result screen. The displayed
    /// selector is authoritative: all eleven medal results belong in COLLECTION, while
    /// the ordinary B+..F grades do not. Repeats increase the displayed count instead of
    /// creating duplicate rows.
    /// </summary>
    private void GrantResultDisc(Client client, StageResultAward resultAward)
    {
        if (resultAward.CollectionCode is not ushort code)
        {
            return;
        }

        bool granted = Players(client).Update(profile =>
        {
            CollectionEntry? held = profile.Collection
                .FirstOrDefault(entry => entry.Code == code);
            // The dialog prints the VALUE beside the artwork, so a repeat is a count
            // rather than a duplicate row.
            if (held != null)
            {
                profile.Collection[profile.Collection.IndexOf(held)] =
                    held with { Value = (ushort)Math.Min(held.Value + 1, ushort.MaxValue) };
                return true;
            }
            profile.Collection.Add(new CollectionEntry(code, 1));
            return true;
        });

        if (granted)
        {
            SendCollectionRefresh(client);
            Logger.Info(client,
                $"Added result award {resultAward.Rank} (collection code 0x{code:X}) " +
                "to COLLECTION.");
        }
    }

    private void GrantCourseDisc(Client client, CourseDefinition course)
    {
        if (!course.Rewards.HasDisc)
        {
            return;
        }

        ushort code = course.Rewards.DiscCode;
        bool granted = Players(client).Update(profile =>
        {
            if (profile.Collection.Any(entry => entry.Code == code))
            {
                return false;
            }
            profile.Collection.Add(new CollectionEntry(code, 1));
            return true;
        });

        if (granted)
        {
            SendCollectionRefresh(client);
            Logger.Info(client,
                $"Course '{course.Name}' awarded collection disc 0x{code:X}.");
            SendSystemChat(client,
                $"Course reward disc awarded (code {code}); COLLECTION has been refreshed.");
        }
    }

    /// <summary>
    /// Recomputes which courses the Course Club may offer from the [Prerequisite] gates
    /// and what has actually been cleared, then republishes the list. Without this every
    /// course is offered from the start and the script's progression is inert.
    /// </summary>
    private void RefreshCourseAvailability(Client client)
    {
        if (_courses.Count == 0)
        {
            return;
        }

        if (UnlockAllCourses)
        {
            // Do not persist this override. If the setting is switched off later, the
            // account immediately returns to the prerequisite progress it actually earned.
            client.Send(OnCourseListInfPacket.Build(_courses.Ids()));
            return;
        }

        ushort[] available = Players(client).Update(profile =>
        {
            // Prerequisites are per channel too: clearing a course in SEOUL must not
            // unlock the TOKYO version, whose charts are different.
            byte keyMode = (byte)Channel.KeyMode;
            HashSet<ushort> cleared =
            [
                .. profile.CourseRecords
                    .Where(record => record.Clears > 0 && record.KeyMode == keyMode)
                    .Select(record => record.CourseId)
            ];
            ushort[] unlocked = _courses.Courses
                .Where(course => course.IsUnlocked(cleared))
                .Select(course => course.Id)
                .ToArray();
            profile.AvailableCourseIds = [.. unlocked];
            return unlocked;
        });

        client.Send(OnCourseListInfPacket.Build(available));
    }

    /// <summary>
    /// Pays the course's [ClearRes] bonus. Max/Exp are percentages of what the final stage
    /// itself earned, matching the "MAX 50% / 경험치 0%" panel the total-result screen draws.
    /// </summary>
    private bool GrantCourseRewards(
        Client client,
        CourseDefinition course,
        uint stageMoney,
        uint stageExperience)
    {
        CourseRewards rewards = course.Rewards;
        (uint money, uint experience) = CourseRewardPolicy.Calculate(
            stageMoney, stageExperience, rewards);
        if (money == 0 && experience == 0)
        {
            return false;
        }

        StageProgressUpdate update = Players(client).Update(profile =>
        {
            uint previousLevel = profile.Progress.Level;
            profile.Progress.Money = AddSaturating(profile.Progress.Money, money);
            profile.Progress.Experience =
                AddSaturating(profile.Progress.Experience, experience);
            // Course rewards are separate from the final stage's normal payout, but use
            // the same client EXP curve. Omitting this left stored EXP above the current
            // threshold and made the client render values such as 150%.
            ApplyLevelUps(profile.Progress);
            return new StageProgressUpdate(
                profile.WireUserId, profile.Progress.Money, profile.Progress.Experience,
                profile.Progress.Level, profile.Progress.Wins, profile.Progress.Losses,
                profile.Progress.Draws, profile.Progress.Level > previousLevel, false,
                UserStatisticsBlock.Build(profile.Progress));
        });

        client.Send(OnUpdateUserPropertyMoneyInfPacket.Build(update.UserId, update.Money));
        client.Send(OnUpdateUserPropertyLevelInfPacket.Build(
            update.UserId, update.Experience, update.Level));
        Logger.Info(client,
            $"Course \"{course.Name}\" bonus: +{money} MAX ({rewards.MoneyPercent}%), " +
            $"+{experience} exp ({rewards.ExperiencePercent}%).");
        return update.LeveledUp;
    }

    /// <summary>
    /// Selects at most one of the course's [ClearRes] Itemnum awards, grants it, and
    /// refreshes the item box. The returned packed item is announced after the course-list
    /// refresh, because that refresh clears the client's result-screen item slot.
    /// </summary>
    private TimedInventoryItem? GrantCourseItem(Client client, CourseDefinition course)
    {
        if (course.Rewards.Items.Count == 0)
        {
            return null;
        }

        // Itemnum entries are consecutive weighted outcomes from one roll. Their script
        // totals never exceed 100; the unused tail is the explicit no-item result. The
        // result screen has one item slot, so independently rolling every line could both
        // grant multiple items and display only one of them.
        CourseItemReward? rolled = CourseItemRewardPolicy.Roll(course.Rewards.Items);
        if (rolled == null)
        {
            Logger.Info(client,
                $"Course \"{course.Name}\" rolled no item this time ({string.Join(", ",
                    course.Rewards.Items.Select(item =>
                        $"0x{item.CatalogId:X4}@{item.ChancePercent}%"))}).");
            return null;
        }

        IReadOnlyList<TimedInventoryItem> granted = Players(client).GrantItems(
            [(rolled.CatalogId, Count: (ushort)1)], DateTimeOffset.UtcNow);
        if (granted.Count == 0)
        {
            Logger.Info(client,
                $"Course \"{course.Name}\" awarded no items (box full or unknown ids).");
            return null;
        }

        SendInventoryRefresh(client);
        TimedInventoryItem item = granted[0];
        Logger.Info(client,
            $"Course \"{course.Name}\" awarded " +
            $"0x{(ushort)item.ItemId:X4} x{item.ItemId >> 16}.");
        return item;
    }

    /// <summary>
    /// Charges the course's [Max] MaxPrice. Returns false when the player cannot afford it,
    /// which refuses the start rather than letting the course run for free.
    /// </summary>
    private bool TryChargeCourse(Client client, CourseDefinition course)
    {
        if (course.MaxPrice == 0)
        {
            return true;
        }

        bool paid = Players(client).Update(profile =>
        {
            if (profile.Progress.Money < course.MaxPrice)
            {
                return false;
            }
            profile.Progress.Money -= course.MaxPrice;
            return true;
        });

        if (!paid)
        {
            Logger.Error(client,
                $"Cannot start course \"{course.Name}\": it costs {course.MaxPrice} MAX.");
            return false;
        }

        uint money = Players(client).Read(profile => profile.Progress.Money);
        client.Send(OnUpdateUserPropertyMoneyInfPacket.Build(
            WireId(client), money));
        Logger.Info(client,
            $"Course \"{course.Name}\" charged {course.MaxPrice} MAX (balance {money}).");
        return true;
    }

    /// <summary>
    /// Snapshots the running course totals in the shape the 0x70 record needs. Accuracy is
    /// the mean across the stages played, matching how the panel presents a single figure.
    /// </summary>
    private static StageTotals CourseTotalsFor(Client client) => new(
        Clamp16(client.CourseNotesHit),
        Clamp16(client.CourseBreaks),
        client.CourseMaxCombo,
        client.CourseScore,
        client.CourseBonusScore,
        client.CourseStagesPlayed == 0
            ? 0f
            : (float)(client.CourseAccuracySum / client.CourseStagesPlayed));

    private static ushort Clamp16(uint value) =>
        value > ushort.MaxValue ? ushort.MaxValue : (ushort)value;

    private static uint AddSaturating(uint value, uint amount) =>
        amount > uint.MaxValue - value ? uint.MaxValue : value + amount;

    private sealed record StageProgressUpdate(
        uint UserId,
        uint Money,
        uint Experience,
        uint Level,
        uint Wins,
        uint Losses,
        uint Draws,
        bool LeveledUp,
        bool NewRecord,
        uint[] Statistics);

    public void SendIdentityAck(Client client, IReadOnlyList<uint> userIds)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(userIds);
        IReadOnlyList<LobbyUserIdentity> matches = userIds
            .Distinct()
            .Select(userId => TryResolveIdentity(client, userId))
            .OfType<LobbyUserIdentity>()
            .ToArray();
        client.Send(OnUserIdInfoAckPacket.Build(matches));
    }

    /// <summary>
    /// Resolves a user id to the 67-byte record the client caches: the real player, or one
    /// of the roster stand-ins. An id with no record is left out of the ack, which is what
    /// the client expects for an account it may not see - it simply draws no row.
    /// </summary>
    private LobbyUserIdentity? TryResolveIdentity(Client requester, uint userId)
    {
        if (userId == 0)
        {
            return null;
        }

        Client[] connected;
        lock (_lock)
        {
            connected = _clients.ToArray();
        }
        foreach (Client candidate in connected)
        {
            LobbyUserIdentity? identity = Players(candidate).Read(profile =>
                profile.WireUserId == userId || profile.WaiterKey == userId
                    ? LobbyUserIdentity.CreateLocal(profile)
                    : null);
            if (identity != null)
            {
                lock (_lock)
                {
                    _knownIdentities[userId] = identity;
                }
                return identity;
            }
        }

        LobbyUserIdentity? roster = Players(requester).Read(profile =>
            profile.Roster.FirstOrDefault(user => user.UserId == userId)?.ToIdentity());
        if (roster != null)
        {
            return roster;
        }

        // A contact who has LOGGED OUT still needs their record, or the friends panel has
        // no name to draw and falls back to showing the bare user id. Remember everyone
        // seen this session so an offline row keeps its nickname.
        lock (_lock)
        {
            return _knownIdentities.GetValueOrDefault(userId);
        }
    }

    /// <summary>
    /// Nickname records for every user id resolved this session, so a contact keeps their
    /// name in the friends list after they disconnect.
    /// </summary>
    private readonly Dictionary<uint, LobbyUserIdentity> _knownIdentities = [];

    private void AnnounceRoomEntry(
        Client client,
        IReadOnlyCollection<Client> waiting,
        LocalRoomState room)
    {
        if (client.UserId is uint userId && userId <= ushort.MaxValue)
        {
            Send(waiting, OnWaiterInfoEraseInfPacket.Build((ushort)userId));
        }
        Send(waiting, OnRoomInfoInfPacket.Build(RoomList(room)));
    }

    /// <summary>
    /// Rebuilds the entering client's waiter cache and announces it to everyone already
    /// waiting in the lobby. Room occupants are deliberately excluded: their client has
    /// switched away from the lobby list and will receive a fresh snapshot when it exits.
    /// Sending the entrant's own row unconditionally also fixes the multi-user case where
    /// it was omitted merely because another waiter already existed.
    /// </summary>
    private void SynchronizeLobbyWaiters(
        Client entrant,
        IReadOnlyCollection<Client> waitingPeers,
        IReadOnlyCollection<Client> roomPeers)
    {
        // A client can retain waiter rows while it is inside a room because room occupants
        // do not receive lobby-list broadcasts. Remove everyone who is currently in a
        // room before replaying the live waiter snapshot, otherwise those stale rows
        // survive when this client returns to the lobby.
        foreach (Client peer in roomPeers)
        {
            entrant.Send(OnWaiterInfoEraseInfPacket.Build(WaiterKey(peer)));
        }

        // ERASE BEFORE EVERY ADD. This snapshot is replayed whenever a client (re-)enters
        // the lobby - on login and again on leaving a room - so any row it already holds
        // would be added a second time. The net-level map at record+71 overwrites in
        // place, but the visible list is a separate widget that appends, so a repeat add
        // shows the same player twice. An erase for a row that is not there is a no-op,
        // so clearing first is always safe and makes the replay idempotent.
        LobbyWaiterInfo entrantWaiter =
            Players(entrant).Read(LobbyWaiterInfo.CreateLocal);
        entrant.Send(OnWaiterInfoEraseInfPacket.Build(entrantWaiter.MapKey));
        entrant.Send(OnWaiterInfoUpdateInfPacket.Build(entrantWaiter));
        foreach (Client peer in waitingPeers)
        {
            LobbyWaiterInfo peerWaiter = Players(peer).Read(LobbyWaiterInfo.CreateLocal);
            entrant.Send(OnWaiterInfoEraseInfPacket.Build(peerWaiter.MapKey));
            entrant.Send(OnWaiterInfoUpdateInfPacket.Build(peerWaiter));

            peer.Send(OnWaiterInfoEraseInfPacket.Build(entrantWaiter.MapKey));
            peer.Send(OnWaiterInfoUpdateInfPacket.Build(entrantWaiter));
        }

        Logger.Info(entrant,
            $"Waiter snapshot replayed: self key={entrantWaiter.MapKey} " +
            $"id={entrantWaiter.UserId}; " +
            $"peers=[{string.Join(", ", waitingPeers.Select(peer =>
                $"{Players(peer).Read(p => p.Nickname)}:key=" +
                $"{Players(peer).Read(p => p.WaiterKey)}"))}]; " +
            $"cleared {roomPeers.Count} in-room row(s).");
    }

    private Client[] WaitingClients(Client? except = null) => _clients
        .Where(client => client != except && !_memberships.ContainsKey(client))
        .ToArray();

    private RoomMemberInfo MemberInfo(LocalRoomMember member) => Players(member.Client).Read(profile =>
        RoomMemberInfo.CreateLocal(
            profile,
            member.Slot,
            member.ConnectionId,
            member.Team,
            member.IsHost));

    private static RoomListEntry RoomList(LocalRoomState room)
    {
        // The grid row advertises the host's selected song (record+44 is a disc index the
        // client resolves to a song name) and whether the room is mid-song (record+43).
        LocalRoomMember? host = room.Members.FirstOrDefault(member => member.IsHost);
        return RoomListEntry.Create(
            room.Index,
            room.Settings,
            room.OccupantCount,
            room.Phase == RoomPlayPhase.Waiting
                ? RoomListState.Waiting
                : RoomListState.Playing,
            host is { HasDisc: true } ? (ushort)host.DiscId : (ushort)0,
            // record+35 is the "x/y" denominator, so closing slots has to lower it: four
            // closed in a six-slot room reads "x/2".
            room.OpenSlotCount);
    }

    private ushort AllocateRoomIndex()
    {
        for (int candidate = 0; candidate <= ushort.MaxValue; candidate++)
        {
            ushort value = (ushort)candidate;
            if (!_rooms.ContainsKey(value))
            {
                return value;
            }
        }
        throw new InvalidOperationException("No local room indexes remain.");
    }

    private static ushort RequireAssignedUserId(Client client)
    {
        if (client.AssignedUserId == 0)
        {
            throw new InvalidOperationException(
                "The client must complete OnConnectAck before entering a room.");
        }
        return client.AssignedUserId;
    }

    private static void Send(IEnumerable<Client> recipients, Packet packet)
    {
        foreach (Client client in recipients)
        {
            client.Send(packet);
        }
    }

    private sealed class LocalRoomState
    {
        public LocalRoomState(ushort index, RoomCreateRequest settings)
        {
            Index = index;
            Settings = settings;
            SlotsEnabled = Enumerable.Repeat(true, LocalRoomProtocol.MaximumSlots).ToArray();
        }

        public ushort Index { get; }
        public RoomCreateRequest Settings { get; set; }
        public List<LocalRoomMember> Members { get; } = [];

        /// <summary>
        /// Roster stand-ins occupying slots as render-only opponents. They have no socket,
        /// so they are kept apart from <see cref="Members"/>: they count toward slot
        /// occupancy and the grid headcount, but never receive a packet and never gate
        /// play readiness. See <see cref="RoomBot"/>.
        /// </summary>
        public List<RoomBot> Bots { get; } = [];

        public bool[] SlotsEnabled { get; }
        public RoomPlayPhase Phase { get; set; }
        public uint CheckSequence { get; set; }

        /// <summary>
        /// Number of synchronized room-wide 0xB3 item signals already emitted. Each
        /// member tracks their own streak, while this shared count prevents equal reward
        /// tiers from generating duplicates in multiplayer.
        /// </summary>
        public int BattleItemSignalsEarned { get; set; }

        /// <summary>
        /// Combo tiers crossed faster than the client's five-second PlayStateInf cadence
        /// can safely represent. At most one 0xB3 is drained per state report so each
        /// signal can select a different future note.
        /// </summary>
        public int PendingBattleItemSignals { get; set; }

        /// <summary>Every slot index occupied by a real member or a bot.</summary>
        public IEnumerable<byte> OccupiedSlots() =>
            Members.Select(member => member.Slot).Concat(Bots.Select(bot => bot.Slot));

        /// <summary>Total occupants for capacity and the grid headcount.</summary>
        public int OccupantCount => Members.Count + Bots.Count;

        /// <summary>
        /// How many players the room actually admits: closing a slot has to reduce this,
        /// otherwise the grid keeps advertising the created capacity and the join gate
        /// keeps letting people in until every OPEN slot is gone - which then shows up as
        /// a room that says 2/6 but refuses to take anyone.
        /// </summary>
        public byte OpenSlotCount => checked((byte)Enumerable
            .Range(0, Settings.Capacity)
            .Count(index => SlotsEnabled[index]));

        /// <summary>Slots the host has closed, in the order the client must be told.</summary>
        public IEnumerable<byte> ClosedSlots() => Enumerable
            .Range(0, Settings.Capacity)
            .Where(index => !SlotsEnabled[index])
            .Select(index => (byte)index);
    }

    /// <summary>
    /// A roster stand-in placed in a room slot to test the multiplayer UI on a single
    /// account. It renders as a second occupant and can be teamed/readied for display, but
    /// it cannot receive packets, so it is excluded from every send and from the readiness,
    /// load, and result gates that real players drive.
    /// </summary>
    private sealed class RoomBot(
        RosterUser roster,
        byte slot,
        ushort connectionId,
        byte team)
    {
        public RosterUser Roster { get; } = roster;
        public byte Slot { get; } = slot;
        public ushort ConnectionId { get; } = connectionId;
        public byte Team { get; set; } = team;
        public bool IsReady { get; set; } = true;

        /// <summary>How the bot's synthesized end-of-song result is generated.</summary>
        public BotOutcome Outcome { get; set; } = BotOutcome.Mirror;

        // A bot has no real inventory, so it plays with an empty effector/mount loadout;
        // these exist only so its slot can be sent the same start packets a real member is.
        public byte[] EffectorConfiguration { get; } =
            new byte[GameplayProtocol.EffectorConfigurationSize];
        public byte[] MountSnapshot { get; } =
            new byte[GameplayProtocol.MountSnapshotSize];
    }

    /// <summary>
    /// How a room bot's end-of-song result is produced, for exercising the battle result
    /// screen. <see cref="Mirror"/> copies the host's own play so the match looks even;
    /// Win/Lose force the bot above/below it.
    /// </summary>
    private enum BotOutcome
    {
        Mirror,
        Win,
        Lose
    }

    private sealed class LocalRoomMember(
        LocalRoomState room,
        Client client,
        byte slot,
        ushort connectionId,
        byte team,
        bool isHost,
        int battleItemComboInterval =
            BattleItemComboRewardTracker.DefaultComboInterval)
    {
        public LocalRoomState Room { get; } = room;
        public Client Client { get; } = client;
        public byte Slot { get; } = slot;
        public ushort ConnectionId { get; } = connectionId;
        public byte Team { get; set; } = team;
        public bool IsHost { get; set; } = isHost;
        public bool IsReady { get; set; }
        public uint DiscId { get; set; }
        // Disc index is 0-based, so DiscId == 0 is a valid first chart and cannot mean
        // "nothing selected". Track selection explicitly instead.
        public bool HasDisc { get; set; }

        /// <summary>
        /// The host selected the synthetic RANDOM entry. It is resolved to a real playable
        /// song only after a valid StartReq, never while the host is browsing the song list.
        /// </summary>
        public bool RandomDiscPending { get; set; }

        /// <summary>
        /// /mission arm is waiting for the next concrete song selection. A pending RANDOM
        /// roll counts as that selection when StartGame resolves it.
        /// </summary>
        public bool MissionPending { get; set; }

        /// <summary>
        /// The CURRENT selection is a DJ Mission Match run. Set when an armed proc lands
        /// on a song; cleared by the next selection, so changing your mind loses it.
        /// It is what makes StartGame stamp the game-info mode byte.
        /// </summary>
        public bool IsMissionMatch { get; set; }
        // The player's own end-of-song result (from StageResultInf), echoed back in
        // OnStageResultExInf so the result screen shows the real play, not a template.
        public StageResult? LastResult { get; set; }

        /// <summary>
        /// The combo this member starts the next song on. Non-zero only mid-course: the
        /// client seeds its live combo from the value OnStartParameterInf carries, which is
        /// how a course combo survives from one chart to the next.
        /// </summary>
        public uint CarriedCombo { get; set; }

        /// <summary>
        /// Client sub_426BC0 stores four item/level pairs. Pickup appends, a full queue
        /// discards its oldest entry, and both level-up and use operate on entry zero.
        /// </summary>
        public List<BattleItemState> BattleItems { get; } = [];

        /// <summary>
        /// Infers this member's repeat item-drop rewards from retail PlayStateInf fields.
        /// Reset at every song boundary.
        /// </summary>
        public BattleItemComboRewardTracker BattleItemComboRewards { get; } =
            new(battleItemComboInterval);

        /// <summary>
        /// The server-owned half of this run's result record: money earned (struct+42),
        /// whether it raised the level (struct+39) and whether it beat the stored best
        /// (struct+44). The client cannot derive any of them from its own report.
        /// </summary>
        public StageAward Award { get; set; } = StageAward.None;

        /// <summary>
        /// Set only on the stage that ENDS a course: the summed course figures the client
        /// cannot compute itself. Null for ordinary play and for mid-course stages.
        /// </summary>
        public StageTotals? CourseTotals { get; set; }
        public byte Difficulty { get; set; }
        public byte[] EffectorConfiguration { get; set; } =
            new byte[GameplayProtocol.EffectorConfigurationSize];
        public byte[] MountSnapshot { get; set; } =
            MountItemState.Empty.Snapshot;
        public bool IsLoaded { get; set; }
    }

    private LocalPlayerStore Players(Client client) => client.PlayerStoreOr(_players);

    private static bool IsCourseInProgress(Client client) =>
        client.SelectedCourseId.HasValue && client.CourseStagesPlayed > 0;

    /// <summary>
    /// The id this client is known by on the wire. Everything cross-player - messenger
    /// contacts and routing, presence, the waiter list, the profile view - must use this
    /// and never the persistent account key, which defaults to 1 for every new profile.
    /// </summary>
    private uint WireId(Client client) => Players(client).Read(p => p.WireUserId);

    /// <summary>
    /// Stable per-account key the lobby waiter list files this client under. Never the
    /// session id - see <see cref="LobbyWaiterInfo"/>'s MapKey.
    /// </summary>
    private ushort WaiterKey(Client client) => Players(client).Read(p => p.WaiterKey);

    /// <summary>
    /// Assigns a unique session id, low enough to survive the u16 fields that carry it
    /// (OnWaiterInfoEraseInf is a u16, so an id above 0xFFFF could never erase its row).
    /// Kept clear of the small persistent account keys so the two can never be confused.
    /// </summary>
    private const uint FirstSessionUserId = 0x1000;
    private const uint LastSessionUserId = 0xFFF0;

    public void AssignSessionId(Client client)
    {
        ArgumentNullException.ThrowIfNull(client);
        LocalPlayerStore store = Players(client);
        HashSet<uint> taken = [];
        lock (_lock)
        {
            foreach (Client peer in _clients)
            {
                if (peer == client)
                {
                    continue;
                }
                taken.Add(Players(peer).Read(p => p.WireUserId));
            }
        }
        // Roster stand-ins derive their ids from the owner's, so keep a gap around each
        // live id rather than only avoiding exact collisions.
        for (int attempt = 0; attempt < 64; attempt++)
        {
            uint candidate = (uint)Random.Shared.NextInt64(
                FirstSessionUserId, LastSessionUserId);
            if (taken.Any(used => candidate >= used - 64 && candidate <= used + 64))
            {
                continue;
            }
            store.Update(profile => profile.SessionUserId = candidate);
            client.UserId = candidate;
            return;
        }
        Logger.Error(client, "Could not allocate a free session user id.");
    }

    /// <summary>Both ids a client answers to, for diagnosing cross-player routing.</summary>
    private string DescribeIds(Client client) =>
        $"{client.Identity}(conn={client.UserId?.ToString() ?? "-"}," +
        $"profile={WireId(client)})";

    private sealed record BattleItemState(short PickupId, byte Level);

    private enum RoomPlayPhase
    {
        Waiting,
        Loading,
        Playing
    }
}



