using Arrowgene.DJMaxOnline.Server.China260.Packets;
using Arrowgene.Logging;

namespace Arrowgene.DJMaxOnline.Server.China260.Handler;

/// <summary>
/// China 2.60's first-socket authentication request (0x11).
///
/// The retail order is ConnectReq, OnConnectAck, AuthenticateInSndAccReq, then
/// OnAuthenticateInAck, account class, game start, and channel list. The follow-up
/// game-channel socket subsequently uses LogInReq.
/// </summary>
public sealed class AuthenticateInSndAccReqHandler : IPacketHandler
{
    private static readonly ServerLogger Logger =
        LogProvider.Logger<ServerLogger>(typeof(AuthenticateInSndAccReqHandler));

    private readonly Func<IReadOnlyList<ChannelInfo>> _channelSnapshot;
    private readonly LocalAccountResolver _accounts;

    public AuthenticateInSndAccReqHandler(
        Func<IReadOnlyList<ChannelInfo>> channelSnapshot,
        LocalAccountResolver accounts)
    {
        _channelSnapshot = channelSnapshot ??
            throw new ArgumentNullException(nameof(channelSnapshot));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    }

    public PacketId Id => PacketId.AuthenticateInSndAccReq;

    public void Handle(Client client, Packet packet)
    {
        AuthenticationRequest request = AuthenticateInSndAccReqPacket.Parse(packet);
        if (client.CipherSeed == null ||
            !request.CipherSeed.AsSpan().SequenceEqual(client.CipherSeed))
        {
            Reject(client, "cipher seed mismatch");
            return;
        }

        if (!TryBindAccount(client, out LocalPlayerStore? store, out string source) ||
            store == null)
        {
            Reject(client, "no unambiguous launcher session");
            return;
        }

        LocalPlayerProfile profile = store.Profile;
        client.UserId = profile.UserId;

        AuthenticationIdentity identity = new(
            profile.UserId,
            AccountClass: profile.AccountClass,
            Level: profile.Progress.Level,
            AccountId: profile.AccountId,
            SecondaryId: profile.SecondaryId,
            Nickname: profile.Nickname);

        // Match the retail first-socket order from the capture. In particular, do not
        // emit this sequence from ConnectReq and do not add a peer-count packet here.
        client.Send(OnAuthenticateInAckPacket.Build(identity));
        client.Send(OnUpdateUserAccountClassInfPacket.Build(
            profile.WireUserId, profile.AccountClass));
        client.Send(OnGameStartInfPacket.Build(LocalLobbyBootstrap.GameStartParameters));
        IReadOnlyList<ChannelInfo> channels = _channelSnapshot();
        client.Send(OnChannelInfoInfPacket.Build(channels));

        Logger.Info(client,
            $"Authenticated {profile.AccountId} via {source}; sent China server-list " +
            $"bootstrap ({channels.Count} channel(s)).");
    }

    private bool TryBindAccount(
        Client client,
        out LocalPlayerStore? store,
        out string source)
    {
        if (client.PlayerStore is { } existing)
        {
            store = existing;
            source = "existing session";
            return true;
        }

        if (_accounts.TryResolveDevelopment(out store) && store != null)
        {
            client.PlayerStore = store;
            source = "development account";
            return true;
        }

        if (_accounts.TryResolveSoleLauncherSession(
                out store, out LoginSessionLease? lease) &&
            store != null && lease != null)
        {
            client.PlayerStore = store;
            client.LoginSessionLease = lease;
            source = lease.IsReconnect ? "launcher reconnect" : "launcher ticket";
            return true;
        }

        store = null;
        source = string.Empty;
        return false;
    }

    private static void Reject(Client client, string reason)
    {
        client.PlayerStore = null;
        client.UserId = null;
        client.Send(OnAuthenticateInAckPacket.Build(
            new AuthenticationIdentity(0, 0, 0, string.Empty, string.Empty, string.Empty),
            AuthenticationResult.Rejected));
        Logger.Error(client, $"Rejected AuthenticateInSndAccReq: {reason}.");
    }
}
