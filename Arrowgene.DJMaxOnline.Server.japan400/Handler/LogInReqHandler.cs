using Arrowgene.DJMaxOnline.Server.Japan400.Packets;
using Arrowgene.Logging;

namespace Arrowgene.DJMaxOnline.Server.Japan400.Handler;

public sealed class LogInReqHandler : IPacketHandler
{
    private static readonly ServerLogger Logger =
        LogProvider.Logger<ServerLogger>(typeof(LogInReqHandler));

    /// <summary>
    /// How long the refusal ack gets before the socket closes. Enough for the client to read
    /// it and raise its dialog; Send queues asynchronously, so closing at once discards it.
    /// </summary>
    private const int LockKickGraceMilliseconds = 2000;

    private readonly ChannelInfo _channel;
    private readonly LocalAccountResolver _accounts;
    private readonly IPlayerRepository? _playerRepository;
    private readonly LocalPlayerStore? _fallbackPlayers;
    private readonly LocalLobby _lobby;
    private readonly string _downloadUrl;

    public LogInReqHandler(
        ChannelInfo channel,
        LocalAccountResolver accounts,
        LocalPlayerStore? fallbackPlayers,
        LocalLobby lobby,
        string downloadUrl,
        IPlayerRepository? playerRepository = null)
    {
        _playerRepository = playerRepository;
        _channel = channel;
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _fallbackPlayers = fallbackPlayers;
        _lobby = lobby;
        _downloadUrl = downloadUrl;
    }

    public PacketId Id => PacketId.LogInReq;

    public void Handle(Client client, Packet packet)
    {

        LoginRequest request = LogInReqPacket.Parse(packet);
        // The 30-byte seed echoed back is the real proof of a valid session; the
        // Korean RequestCode value differs from China's GameChannel so we don't
        // gate on it.
        if (client.CipherSeed == null ||
            !request.CipherSeed.AsSpan().SequenceEqual(client.CipherSeed))
        {
            Logger.Error(client,
                $"Rejected LogInReq: seed mismatch (userId {request.UserId}, " +
                $"code 0x{request.RequestCode:X4}).");
            client.Close();
            return;
        }

        bool resumedLauncherSession = false;
        if (client.PlayerStore == null)
        {
            if (!_accounts.TryResolveAuthenticatedUser(
                    request.UserId,
                    out LocalPlayerStore? resumedStore,
                    out LoginSessionLease? resumedLease) ||
                resumedStore == null || resumedLease == null)
            {
                Logger.Error(client,
                    "Rejected LogInReq: no active launcher session exists for " +
                    $"userId {request.UserId}.");
                client.Close();
                return;
            }

            client.PlayerStore = resumedStore;
            client.LoginSessionLease = resumedLease;
            resumedLauncherSession = true;
        }

        LocalPlayerProfile profile = client.PlayerStoreOr(_fallbackPlayers).Profile;

        // Read the lock state STRAIGHT FROM THE DATABASE, not from the cached profile.
        // The cached copy is whatever was loaded when the session began, so a ban or an
        // unban applied while the server is running would not be seen here - an unbanned
        // player stayed refused until a restart.
        AccountLockState lockState = profile.LockState;
        if (_playerRepository == null)
        {
            Logger.Error(client,
                "No player repository: the account lock cannot be checked, so a banned " +
                "account will be let in.");
        }
        else if (_playerRepository.TryLoad(
                     profile.UserId, out LocalPlayerProfile? current) && current != null)
        {
            lockState = current.LockState;
            profile.LockState = lockState;
            profile.LockReason = current.LockReason;
            Logger.Info(client,
                $"Lock check for user {profile.UserId} ('{profile.AccountId}'): " +
                $"{AccountLockReasons.Describe(lockState)}.");
        }
        else
        {
            // Falling back to the cached copy silently is how a live ban gets missed.
            Logger.Error(client,
                $"Lock check FAILED: no row for user {profile.UserId} " +
                $"('{profile.AccountId}'); using the cached state " +
                $"({AccountLockReasons.Describe(lockState)}).");
        }

        // A LOCKED ACCOUNT GOES NO FURTHER, and is failed with a dialog rather than dropped.
        //
        // Refused HERE, before any of the login work, rather than after the lobby join.
        // Running the full handshake first and only then dropping them gave the client a
        // whole session to reconnect out of, which is what produced the reconnect loop.
        //
        // A FAILING ACK, not a bare close. Closing the socket silently leaves the client
        // with nothing on screen, so it just tries again; a failing result puts a dialog up
        // and it waits for the player instead. The text is not ours to choose - at this
        // point the ack is read by sub_4499DA, which picks from its own hardcoded strings
        // and has no account-lock case, so a lock reason lands on its default and shows
        // "failed to connect to the channel". The ban itself is explained elsewhere: by the
        // launcher when they try to start the game, and by the dialog the live kick sends
        // (see OnLogOutAckPacket) if they were online when it happened.
        if (AccountLockReasons.ReasonFor(lockState) is { } lockReason)
        {
            Logger.Info(client,
                $"Rejected LogInReq for '{profile.AccountId}' (user {profile.UserId}): " +
                $"account is {AccountLockReasons.Describe(lockState)}.");
            client.SendThenClose(
                OnLogInAckPacket.BuildKorean(
                    client.CipherSeed,
                    profile.WireUserId,
                    profile.AccountId,
                    profile.Nickname,
                    profile.AccountClass,
                    profile.IconWireValue(),
                    profile.Progress,
                    profile.Collection,
                    profile.Inventory.ItemBoxItems(),
                    profile.Inventory.MountLoadout(),
                    profile.Gender,
                    result: (ushort)lockReason),
                LockKickGraceMilliseconds);
            return;
        }

        // Accept EITHER id. The launcher hands out the persistent account key, but
        // OnLogInAck tells the client its SESSION id - so a player returning from the
        // channel list logs in again quoting the session id they were last given. Checking
        // only the account key rejected that and closed the socket, which looked like the
        // session being destroyed on leaving a channel.
        if (request.UserId != profile.UserId && request.UserId != profile.WireUserId)
        {
            Logger.Error(client,
                $"Rejected LogInReq: requested userId {request.UserId} matches neither " +
                $"the account key {profile.UserId} nor the session id {profile.WireUserId}.");
            client.Close();
            return;
        }

        // Give this login its own wire identity BEFORE the ack, which is what tells the
        // client its own id (net+794096) - every later packet the client sends carries
        // that value, so the ack and the lobby must agree on it. Every fresh profile's
        // account key is 1, so without this two players answer to the same id and every
        // cross-player feature collapses onto whoever asked.
        _lobby.AssignSessionId(client);
        Logger.Info(client,
            $"LogInReq accepted{(resumedLauncherSession ? " via launcher handoff" : string.Empty)} " +
            $"(userId {request.UserId}, code 0x{request.RequestCode:X4}) for " +
            $"authenticated user {profile.UserId}.");

        // THE ACK CARRIES A SEED BUT DOES NOT RE-KEY. Do not "fix" this back.
        //
        // sub_431ED0 un-inverts the seed and stores it at net+793712 - the same slot
        // OnConnectAck uses - takes its checksum, and logs "CRYPTOKEY-%xh,%d". That is
        // ALL. It never calls sub_44E404, which is what actually installs a key; compare
        // sub_4312C0, which stores the seed AND re-keys both contexts AND sets the secure
        // flag at net+793748.
        //
        // So this seed is for the NEXT connection's handshake, not a live switch: it is
        // what the channel socket echoes back in LogInReq, which is why that packet
        // carries 32 seed bytes.
        //
        // Re-keying here put the server on a keystream the client never adopted, and
        // everything after login turned to noise in BOTH directions - no rooms, garbage
        // nicknames and icons in the waiter list, no chat, and client packets arriving as
        // garbage. Individual packet layouts were all correct; they were simply being
        // enciphered with the wrong key.
        //
        // Nothing of Korea's 1874-byte profile ack belongs here; JP's is 47 bytes and
        // carries no profile at all.
        byte[] nextSeed = new byte[OnLogInAckPacket.SeedSize];
        Random.Shared.NextBytes(nextSeed);
        // Japan's compact login acknowledgement is the lobby-scene transition. Its
        // profile packets MUST precede it: sub_431ED0 starts the scene as soon as the
        // acknowledgement succeeds, and that scene reads the user cache only while it is
        // constructed. Sending these afterward left the opening lobby as Player/female
        // until entering/leaving a room caused a redraw.
        foreach (Packet bootstrapPacket in
                 LocalLobbyBootstrap.BuildBeforeLoginAcknowledgement(profile))
        {
            client.Send(bootstrapPacket);
        }
        Logger.Info(client,
            $"Staged JP profile bootstrap for {profile.WireUserId} " +
            $"({profile.Nickname}, gender={profile.Gender}, icon={profile.IconWireValue()}) " +
            "before OnLogInAck.");

        // Without DOWNLOADURL, the package downloader keeps the client's old configured
        // endpoint and never reaches the local FTP server. These values are cache data
        // too, so keep them before the acknowledgement's lobby transition.
        client.Send(OnEnvironmentInfPacket.Build(
            OnEnvironmentInfPacket.DifficultyMixFilter));
        client.Send(OnEnvironmentInfPacket.Build(
            OnEnvironmentInfPacket.DownloadUrl(_downloadUrl)));
        Logger.Info(client,
            $"Staged OnEnvironmentInf: DIFFMIX_FILTER and DOWNLOADURL={_downloadUrl}");

        client.Send(OnLogInAckPacket.BuildJapanese(nextSeed));
        // Mirror what the client stores, so a later echo of it still matches - but leave
        // the live cipher exactly as OnConnectAck keyed it.
        client.CipherSeed = nextSeed[..DjMaxCrypto.JapaneseSeedSize];
        Logger.Info(client,
            "Sent JP OnLogInAck (result 41); seed stored for the next handshake, " +
            "cipher deliberately NOT re-keyed (sub_431ED0 does not call sub_44E404).");

        // Course data depends on the successful game-channel session, so it remains
        // after the acknowledgement. It is not read by the initial lobby profile card.
        client.Send(OnCourseListInfPacket.Build(profile.AvailableCourseIds));

        _lobby.Join(client);
    }
}
