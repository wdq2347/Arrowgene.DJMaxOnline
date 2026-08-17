using Arrowgene.DJMaxOnline.Server.Korea400;
using Arrowgene.DJMaxOnline.Server.Korea400.Handler;
using Arrowgene.DJMaxOnline.Server.Korea400.Packets;
using Arrowgene.Logging;
using Arrowgene.Networking.Tcp.Server.AsyncEvent;

public class DjMaxServer
{
    private static readonly ServerLogger Logger = LogProvider.Logger<ServerLogger>(typeof(DjMaxServer));
    private readonly IReadOnlyList<(ushort Port, Consumer Consumer, AsyncEventServer Server)> _endpoints;
    private readonly LocalFtpServer _ftpServer;
    private readonly LocalContentServer _contentServer;
    private readonly LocalStatusApi _statusApi;
    private readonly Setting _setting;
    private readonly LocalPlayerStore? _players;
    private readonly ShopCatalog _shop;
    private readonly LocalAccountResolver _accounts;
    private readonly IPlayerRepository? _playerRepository;
    private readonly LoginTicketService _loginTickets;
    private readonly LocalLoginServer? _loginServer;
    private readonly LoginApi? _loginApi;
    private readonly SongCatalog _songs;
    private readonly CourseCatalog _courses;
    private readonly List<LocalLobby> _lobbies = [];

    /// <summary>
    /// Finished multiplayer matches, shared by every channel so the status API can serve
    /// one ordered feed. In memory only - see <see cref="MatchHistory"/>.
    /// </summary>
    private readonly MatchHistory _matches = new();
    private int _shutdownDraining;
    private int _stopped;

    /// <summary>
    /// How long a connection may stay silent before the server pings it, and how long it
    /// may then stay silent before being dropped. A crashed or killed client usually
    /// leaves its socket open, so without this it never disconnects: it keeps its lobby
    /// waiter row and its room slot forever.
    /// </summary>
    private static readonly TimeSpan KeepAliveIdle = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(10);
    private Timer? _keepAlive;

    public DjMaxServer(
        Setting setting,
        LocalPlayerProfile? profile = null,
        string? profilePath = null,
        IPlayerRepository? playerRepository = null)
    {
        _setting = setting;
        _playerRepository = playerRepository;
        _shop = ShopCatalog.Load(_setting.ShopDataDirectory);
        // No account is loaded at startup. Each player is read from the database when
        // their launcher ticket is redeemed, and Consumer drops every player packet from a
        // socket that has not authenticated - so the server needs no stand-in identity.
        // The only exception is the legacy single-player mode, which has no database and
        // passes its one profile in explicitly.
        _players = profile != null
            ? new LocalPlayerStore(
                profile, _shop, profilePath, playerRepository, _setting.ItemsNeverExpire)
            : null;
        _loginTickets = new LoginTicketService(
            lifetime: TimeSpan.FromSeconds(
                Math.Clamp(_setting.LoginTicketLifetimeSeconds, 1, 300)),
            reconnectGrace: TimeSpan.FromSeconds(
                _setting.LoginReconnectGraceSeconds));
        _accounts = new LocalAccountResolver(
            _players, _shop, playerRepository, _loginTickets, _setting.ItemsNeverExpire);
        // Server-wide account-class grant, e.g. ["Premium"] to make everyone premium
        // without touching a single row.
        _accounts.DefaultAccountClass = AccountClassInfo.FromNames(
            _setting.DefaultAccountClassFlags, out IReadOnlyList<string> unknownFlags);
        if (unknownFlags.Count != 0)
        {
            Logger.Error(
                "Ignoring unknown DefaultAccountClassFlags: " +
                $"{string.Join(", ", unknownFlags)}. Valid names are " +
                $"{string.Join(", ", Enum.GetNames<AccountClassFlags>())}.");
        }
        if (_accounts.DefaultAccountClass != 0)
        {
            Logger.Info(
                "Every account will be granted account class " +
                $"0x{_accounts.DefaultAccountClass:X} " +
                $"[{string.Join(", ", AccountClassInfo.ToNames(_accounts.DefaultAccountClass))}].");
        }
        _loginServer = playerRepository == null
            ? null
            : new LocalLoginServer(
                playerRepository, _loginTickets, _setting.LoginPipeName);
        // Remote launchers, when enabled. It authenticates through the login server above,
        // so there is exactly one implementation of "is this password right".
        _loginApi = _loginServer == null ? null : new LoginApi(_setting, _loginServer);
        _songs = SongCatalog.Load(_setting.SongCatalogPath);
        Logger.Info(
            $"Loaded {_songs.Count} songs from {_songs.SourcePath}.");
        // Courses live only in the client's own script, so without it the Course Club can
        // list courses but never start one; that is a degraded mode, not a fatal error.
        string? courseScript = CourseCatalog.FindCourseScript();
        _courses = courseScript == null
            ? CourseCatalog.Empty
            : CourseCatalog.Load(courseScript);
        Logger.Info(_courses.Count == 0
            ? "No CourseSection.ini found; Course Club play is unavailable."
            : $"Loaded {_courses.Count} courses from {_courses.SourcePath}.");
        Logger.Info(
            $"Loaded {_shop.ItemCount} shop items, {_shop.ListingCount} client listings, " +
            $"and {_shop.SetCount} item sets from {_shop.DataDirectory}." +
            (_shop.DanglingListings.Count == 0
                ? string.Empty
                : $" Ignoring {_shop.DanglingListings.Count} list entries absent from ItemStock."));
        ClientLookup = new ClientLookup();
        _ftpServer = new LocalFtpServer(_setting);
        _contentServer = new LocalContentServer(_setting);
        _endpoints = CreateEndpoints();
        // Snapshot function rather than the list itself: the API must read live
        // state each request, and must never be able to mutate a lobby.
        _statusApi = new LocalStatusApi(
            _setting,
            () => _lobbies.Select(lobby => lobby.StatusSnapshot()).ToArray(),
            playerRepository is SqlitePlayerRepository sqlite
                ? new ScoreFeedQueries(sqlite.DatabasePath)
                : null,
            _matches);
    }

    public ClientLookup ClientLookup { get; }

    /// <summary>
    /// Launcher tickets and reconnect sessions. Exposed so administration tools built
    /// outside this class can revoke them - a ban that does not revoke the session is not a
    /// ban, because the client simply reconnects and resumes it.
    /// </summary>
    public LoginTicketService LoginTickets => _loginTickets;

    public void Start()
    {
        Logger.Info("Starting local 5-key and 7-key channels...");
        List<(ushort Port, AsyncEventServer Server)> started = new();
        try
        {
            _loginServer?.Start();
            _loginApi?.Start();
            // Only one of the two content paths runs: HTTP mode makes the FTP server dead
            // weight, and leaving it bound would just hold port 21 for nothing.
            if (_setting.ContentDelivery == ContentDeliveryMode.Http)
            {
                _contentServer.Start();
            }
            else
            {
                _ftpServer.Start();
            }
            _statusApi.Start();
            foreach ((ushort port, _, AsyncEventServer server) in _endpoints)
            {
                server.Start();
                started.Add((port, server));
                Logger.Info($"Listening on {_setting.ListenIpAddress}:{port}");
            }
        }
        catch
        {
            foreach ((_, AsyncEventServer server) in started.AsEnumerable().Reverse())
            {
                server.Stop();
            }

            _statusApi.Stop();
            _contentServer.Stop();
            _ftpServer.Stop();
            _loginApi?.Stop();
            _loginServer?.Stop();

            throw;
        }

        // Base rates, password cost and disc tolerance come from the settings file; the
        // multipliers below scale whatever those rates produce.
        SettingFile.Apply(_setting);
        StageRewardPolicy.ExperienceMultiplier = Math.Max(1, _setting.ExperienceMultiplier);
        StageRewardPolicy.MoneyMultiplier = Math.Max(1, _setting.MoneyMultiplier);
        _keepAlive = new Timer(
            _ => SweepIdleClients(), null, KeepAliveInterval, KeepAliveInterval);
        Logger.Info($"Advertising channels on {_setting.AdvertisedIpAddress}.");
    }

    /// <summary>
    /// Pings connections that have gone quiet and drops the ones that stopped answering.
    /// The client replies to OnAliveReq (0x07) with AliveAck (0x06); any traffic at all
    /// refreshes <see cref="Client.LastSeenUtc"/>, so this only ever touches idle links.
    /// Closing the socket makes the transport raise its normal disconnect callback, which
    /// is what removes the player from the lobby and any room.
    /// </summary>
    private void SweepIdleClients()
    {
        DateTime now = DateTime.UtcNow;
        foreach (Client client in ClientLookup.GetAll())
        {
            try
            {
                TimeSpan silent = now - client.LastSeenUtc;
                if (silent > KeepAliveTimeout)
                {
                    Logger.Info(
                        $"{client.Identity} stopped answering for {silent.TotalSeconds:F0}s; " +
                        "closing the connection.");
                    client.Close();
                }
                else if (silent > KeepAliveIdle)
                {
                    client.Send(OnAliveReqPacket.Build());
                }
            }
            catch (Exception ex)
            {
                // A socket that dies mid-sweep must not take the timer down with it.
                Logger.Exception(client, ex);
            }
        }
    }

    public bool IsShutdownDraining => Volatile.Read(ref _shutdownDraining) != 0;

    /// <summary>
    /// Atomically enters drain mode: packet consumers reject every newly accepted socket,
    /// launcher tickets stop being issued, and lobbies refuse new songs while allowing an
    /// already-started course to continue between stages. The transport and its consumer
    /// threads deliberately stay alive so protected players can finish and receive the
    /// native scheduled-disconnect packet before final shutdown.
    /// </summary>
    public bool BeginShutdownDrain()
    {
        if (Interlocked.CompareExchange(ref _shutdownDraining, 1, 0) != 0)
        {
            return false;
        }

        Logger.Info("Shutdown drain started; refusing new game connections and starts.");
        foreach (LocalLobby lobby in _lobbies)
        {
            lobby.BeginShutdownDrain();
        }
        foreach ((_, Consumer consumer, _) in _endpoints)
        {
            consumer.RefuseNewConnections();
        }

        _loginServer?.Stop();
        return true;
    }

    /// <summary>Authenticated room members whose current run must be preserved.</summary>
    public IReadOnlyList<Client> GetShutdownProtectedClients() =>
        ClientLookup.GetAll()
            .Where(client => _lobbies.Any(lobby => lobby.IsShutdownProtected(client)))
            .ToArray();

    public void Stop()
    {
        if (Interlocked.CompareExchange(ref _stopped, 1, 0) != 0)
        {
            return;
        }

        Logger.Info("Stopping...");
        _keepAlive?.Dispose();
        _keepAlive = null;
        BeginShutdownDrain();

        foreach ((_, _, AsyncEventServer server) in _endpoints.Reverse())
        {
            server.Stop();
        }
        _statusApi.Stop();
        _contentServer.Stop();
        _ftpServer.Stop();
        _loginServer?.Stop();
        Logger.Info("Stopped");
    }

    private void ClientConnected(Client client)
    {
        ClientLookup.Add(client);
        client.Send(OnPingTestInfPacket.Build());

        // EXPERIMENT: this (JP) client's init looks server-driven — it never
        // sends ConnectReq. Its id-9 handler (client sub_430AB0) expects a
        // 37-byte OnConnectAck laid out as:
        //   [0:2] id=0x0009  [2] ctrl  [3:5] result WORD (== 4, NOT 5)
        //   [5:35] 30-byte seed  [35:37] assigned-id WORD
        // Push it directly after the ping and watch RAW RECV for the client's
        // next packet. NOTE: the client derives its cipher from this 30-byte
        // seed via a different algorithm than DjMaxCrypto, so anything it sends
        // afterward will be encrypted with keys the server can't yet reproduce —
        // this only tests whether the client accepts the ack and advances.
        byte[] seed = new byte[30];
        Random.Shared.NextBytes(seed);
        byte[] connectAck = new byte[37];
        connectAck[0] = 0x09;
        connectAck[1] = 0x00;
        connectAck[2] = 0xCC;
        connectAck[3] = 0x04; // result = 4 (accepted, JP)
        connectAck[4] = 0x00;
        seed.CopyTo(connectAck.AsSpan(5)); // 30-byte seed at offset 5..34
        ushort assignedUserId = ConnectReqHandler.AllocateAssignedUserId();
        connectAck[35] = (byte)assignedUserId;
        connectAck[36] = (byte)(assignedUserId >> 8);
        Logger.Info($"EXPERIMENT sending JP OnConnectAck: {Convert.ToHexString(connectAck)}");
        client.SendRaw(connectAck);
        // Track the transient id carried at bytes 35-36;
        // track it so room/lobby code (RequireAssignedUserId) has a non-zero id.
        client.AssignedUserId = assignedUserId;

        // Stash the seed; the cipher is enabled in ConnectReqHandler AFTER the
        // (unencrypted) ConnectReq frames, so we don't decrypt ConnectReq itself
        // and desync the keystream for the encrypted packets that follow.
        client.CipherSeed = seed;
    }

    private void ClientDisconnected(Client client)
    {
        if (client.LoginSessionLease is { } lease)
        {
            _loginTickets.Release(lease);
            client.LoginSessionLease = null;
        }
        ClientLookup.Remove(client);
    }

    private IReadOnlyList<(ushort Port, Consumer Consumer, AsyncEventServer Server)> CreateEndpoints()
    {
        IReadOnlyList<ChannelInfo> channels = LocalChannelCatalog.Create(_setting);
        List<(ushort Port, Consumer Consumer, AsyncEventServer Server)> endpoints = new();
        foreach (ChannelInfo channel in channels)
        {
            ushort port = channel.Port;
            Setting endpointSetting = new(_setting)
            {
                Name = $"{_setting.Name}:{port}",
                ServerPort = port
            };
            LocalLobby lobby = new(
                channel,
                _players,
                _songs,
                CreateGameInfoProvider(channel),
                _courses)
            {
                MatchHistory = _matches,
                MessageOfTheDay = [.. _setting.MessageOfTheDay ?? []],
                EquipmentHpPercent = _setting.EquipmentHpPercent,
                EquipmentMaxPercent = _setting.EquipmentMaxPercent,
                EquipmentExperiencePercent = _setting.EquipmentExperiencePercent,
                PremiumRewardBonusPercent = _setting.PremiumRewardBonusPercent,
                MissionMatchChancePercent = _setting.MissionMatchChancePercent,
                // In-game admin commands need the account database. Without one the
                // lobby reports them unavailable rather than acting on nothing.
                Administration = _playerRepository == null
                    ? null
                    : new ServerAdministrationService(
                        _playerRepository, ClientLookup, _loginTickets),
                BattleItemComboInterval = _setting.BattleItemComboInterval,
                UnlockAllCourses = _setting.UnlockAllCourses,
                JudgmentAdjustmentMsByMatchMode =
                    [.. _setting.JudgmentAdjustmentMsByMatchMode ?? []],
                AccuracyDiscs = [.. _setting.AccuracyDiscs ?? []]
            };
            _lobbies.Add(lobby);
            Consumer consumer = new(endpointSetting);
            consumer.ClientConnected += ClientConnected;
            consumer.ClientDisconnected += client =>
            {
                lobby.Leave(client);
                ClientDisconnected(client);
            };
            LoadPacketHandlers(consumer, channel, lobby);
            AsyncEventServer server = new(
                endpointSetting.ListenIpAddress,
                endpointSetting.ServerPort,
                consumer,
                endpointSetting.AsyncEventSettings);
            endpoints.Add((port, consumer, server));
        }

        return endpoints;
    }

    private IGameInfoProvider CreateGameInfoProvider(ChannelInfo channel)
    {
        SongKeyMode keyMode = channel.KeyMode;

        if (!string.IsNullOrWhiteSpace(_setting.PatternsDirectory))
        {
            // channel.Description is the configured session label the client CRCs
            // (for example ".[7KEY] DjMaxServer").
            Logger.Info(
                $"Channel {channel.Name}: generating game-info from patterns in " +
                $"{_setting.PatternsDirectory} ({(int)keyMode}-key, session " +
                $"\"{channel.Description}\").");
            return new PatternGameInfoProvider(
                _songs, _setting.PatternsDirectory, keyMode, channel.Description);
        }

        return new GameInfoCatalog(
            _setting.GameInfoDirectory, _setting.GameInfoFallbackDiscId);
    }

    private void LoadPacketHandlers(
        Consumer consumer,
        ChannelInfo channel,
        LocalLobby lobby)
    {
        consumer.AddHandler(new ConnectReqHandler());
        consumer.AddHandler(new NetmarbleAuthenticateReqHandler(ChannelSnapshot, _accounts));
        consumer.AddHandler(new JpConnectConfirmReqHandler(ChannelSnapshot, _accounts));
        consumer.AddHandler(new AuthenticateInSndAccReqHandler(ChannelSnapshot, _players));
        consumer.AddHandler(new KeepAuthenticateInReqHandler());
        consumer.AddHandler(new LogInReqHandler(
            channel, _accounts, _players, lobby, ContentDelivery.SongUrl(_setting),
            _playerRepository));
        consumer.AddHandler(new PingTestInfHandler());
        consumer.AddHandler(new UserIdInfoReqHandler(lobby));
        consumer.AddHandler(new ChatInfHandler(lobby));
        consumer.AddHandler(new WChatReqHandler(lobby));
        consumer.AddHandler(new MsgChatReqHandler(lobby));
        consumer.AddHandler(new UpdateUserAccountNickReqHandler(_players, lobby));
        consumer.AddHandler(new UpdateUserProfileReqHandler(_players, lobby));
        consumer.AddHandler(new MsgRegisterUserReqHandler(_players, lobby));
        consumer.AddHandler(new InviteRejectReqHandler(lobby));
        consumer.AddHandler(new QuickInviteReqHandler(lobby));
        consumer.AddHandler(new CreateRoomReqHandler(lobby));
        consumer.AddHandler(new JoinRoomReqHandler(lobby));
        consumer.AddHandler(new LeaveRoomReqHandler(lobby));
        consumer.AddHandler(new ReadyReqHandler(lobby));
        consumer.AddHandler(new TeamControlReqHandler(lobby));
        consumer.AddHandler(new RoomChangeInfoReqHandler(lobby));
        consumer.AddHandler(new ChangeDiscReqHandler(lobby));
        consumer.AddHandler(new SlotControlReqHandler(lobby));
        consumer.AddHandler(new UseEffectorInfHandler(lobby));
        consumer.AddHandler(new StartReqHandler(lobby));
        consumer.AddHandler(new PlayStartReqHandler(lobby));
        consumer.AddHandler(new PlaySkipReqHandler(lobby));
        consumer.AddHandler(new PlayOverReqHandler(lobby));
        consumer.AddHandler(new SongCompleteHandler(lobby));
        consumer.AddHandler(new PlayStateInfHandler(lobby));
        consumer.AddHandler(new UbsAccountAuthenticationReqHandler());
        consumer.AddHandler(new PurchaseItemReqHandler(_players));
        consumer.AddHandler(new ResaleItemReqHandler(_players));
        consumer.AddHandler(new LogOutReqHandler(ChannelSnapshot, lobby));
        consumer.AddHandler(new UserInfoReqHandler(lobby));
        consumer.AddHandler(new GetItemReqHandler(lobby));
        consumer.AddHandler(new ItemLevelUpReqHandler(lobby));
        consumer.AddHandler(new UseItemReqHandler(_players, lobby));
        consumer.AddHandler(new UseEffectorSetInfHandler(lobby));
        consumer.AddHandler(new AliveAckHandler());
        consumer.AddHandler(new ProbeObfuscatedHandler());
        consumer.AddHandler(new VerifyCodeInfHandler());
        consumer.AddHandler(new CheckDataReplyHandler());
        consumer.AddHandler(new Unknown434B40Handler());
        consumer.AddHandler(new MountItemReqHandler(_players));
        consumer.AddHandler(new DeleteItemReqHandler(_players));
        consumer.AddHandler(new GetPresentItemReqHandler(_players));
        consumer.AddHandler(new StageResultInfHandler());
        consumer.AddHandler(new AuthenticateInSndeKeyAckHandler());
        consumer.AddHandler(new Sub434390Handler(_players, lobby));
        consumer.AddHandler(new Sub434450Handler());
        consumer.AddHandler(new Sub434510Handler());
        consumer.AddHandler(new Sub434620Handler());
        consumer.AddHandler(new Sub436120Handler());
        consumer.AddHandler(new Sub4362D0Handler());
        consumer.AddHandler(new UpdateUserIconReqHandler(_players, lobby));
        consumer.AddHandler(new UseMountItemInfHandler(lobby));
        consumer.AddHandler(new Sub4323D0Handler());
    }

    /// <summary>
    /// Builds channel-list records at send time. Caching LocalChannelCatalog.Create here
    /// permanently advertised the default UserCount=0 even while lobbies were occupied.
    /// </summary>
    private IReadOnlyList<ChannelInfo> ChannelSnapshot() => _lobbies
        .Select(lobby => lobby.Channel with { UserCount = lobby.UserCount })
        .ToArray();
}
