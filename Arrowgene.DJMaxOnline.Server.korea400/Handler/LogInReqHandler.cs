using Arrowgene.DJMaxOnline.Server.Korea400.Packets;
using Arrowgene.Logging;

namespace Arrowgene.DJMaxOnline.Server.Korea400.Handler;

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
            $"(userId {request.UserId}, code 0x{request.RequestCode:X4}); " +
            $"sending Korean OnLogInAck for authenticated user {profile.UserId} " +
            $"(1874B, level {profile.Progress.Level}, " +
            $"exp {profile.Progress.Experience}, icon {profile.IconWireValue():X}, " +
            $"class {profile.AccountClass}, wins {profile.Progress.Wins}, " +
            $"losses {profile.Progress.Losses}, " +
            $"items {profile.Inventory.ItemBox.Count}).");
        client.Send(OnLogInAckPacket.BuildKorean(
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
            profile.Gender));

        // The Korean login path uses the monolithic OnLogInAck above instead
        // of LocalLobbyBootstrap, so send the environment settings explicitly.
        // Without DOWNLOADURL, the package downloader keeps the client's old
        // configured endpoint and never reaches the local FTP server.
        client.Send(OnEnvironmentInfPacket.Build(
            OnEnvironmentInfPacket.DifficultyMixFilter));
        client.Send(OnEnvironmentInfPacket.Build(
            OnEnvironmentInfPacket.DownloadUrl(_downloadUrl)));
        Logger.Info(client,
            $"Sent OnEnvironmentInf: DIFFMIX_FILTER and DOWNLOADURL={_downloadUrl}");

        _lobby.Join(client);
    }
}
