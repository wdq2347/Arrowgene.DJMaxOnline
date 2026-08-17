using Arrowgene.DJMaxOnline.Server.Korea400.Packets;
using Arrowgene.Logging;

namespace Arrowgene.DJMaxOnline.Server.Korea400.Handler;

/// <summary>
/// ConnectFromNM=0 launcher authentication. Client sub_430C50 sends the third
/// command-line argument in a 27-byte field and echoes the OnConnectAck seed.
/// The command-line value must be a short-lived ticket from the secure launcher.
/// </summary>
public sealed class JpConnectConfirmReqHandler : IPacketHandler
{
    private readonly Func<IReadOnlyList<ChannelInfo>> _channelSnapshot;
    private readonly LocalAccountResolver _accounts;

    public JpConnectConfirmReqHandler(
        Func<IReadOnlyList<ChannelInfo>> channelSnapshot,
        LocalAccountResolver accounts)
    {
        _channelSnapshot = channelSnapshot ??
            throw new ArgumentNullException(nameof(channelSnapshot));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    }

    public void Handle(Client client, Packet packet) =>
        LauncherAuthenticationFlow.Authenticate(
            client,
            JpConnectConfirmReqPacket.Parse(packet),
            _accounts,
            _channelSnapshot(),
            "ConnectFromNM=0/0x0F");

    public PacketId Id => PacketId.JpConnectConfirmReq;
}

/// <summary>
/// ConnectFromNM=1 launcher authentication. Client sub_430F50 sends the fifth
/// field extracted from the decrypted Netmarble_ClipFormat clipboard payload.
/// The local launcher may place the same short-lived ticket in that field.
/// </summary>
public sealed class NetmarbleAuthenticateReqHandler : IPacketHandler
{
    private readonly Func<IReadOnlyList<ChannelInfo>> _channelSnapshot;
    private readonly LocalAccountResolver _accounts;

    public NetmarbleAuthenticateReqHandler(
        Func<IReadOnlyList<ChannelInfo>> channelSnapshot,
        LocalAccountResolver accounts)
    {
        _channelSnapshot = channelSnapshot ??
            throw new ArgumentNullException(nameof(channelSnapshot));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
    }

    public void Handle(Client client, Packet packet) =>
        LauncherAuthenticationFlow.Authenticate(
            client,
            NetmarbleAuthenticateReqPacket.Parse(packet),
            _accounts,
            _channelSnapshot(),
            "ConnectFromNM=1/0x0D");

    public PacketId Id => PacketId.NetmarbleAuthenticateReq;
}

internal static class LauncherAuthenticationFlow
{
    private static readonly ServerLogger Logger =
        LogProvider.Logger<ServerLogger>(typeof(LauncherAuthenticationFlow));

    public static void Authenticate(
        Client client,
        LauncherAuthenticationRequest request,
        LocalAccountResolver accounts,
        IReadOnlyList<ChannelInfo> channels,
        string source)
    {
        if (client.CipherSeed == null ||
            !request.CipherSeed.AsSpan().SequenceEqual(client.CipherSeed))
        {
            Reject(client, $"{source}: connection seed mismatch");
            return;
        }

        if (!accounts.TryResolveTicket(
                request.Ticket,
                out LocalPlayerStore? store,
                out LoginSessionLease? lease) ||
            store == null || lease == null)
        {
            Reject(client,
                $"{source}: invalid or expired ticket/reconnect session");
            return;
        }

        LocalPlayerProfile profile = store.Profile;
        client.LoginSessionLease = lease;
        client.PlayerStore = store;
        client.UserId = profile.UserId;
        AuthenticationIdentity identity = new(
            profile.UserId,
            AccountClass: profile.AccountClass,
            Level: profile.Progress.Level,
            AccountId: profile.AccountId,
            SecondaryId: profile.SecondaryId,
            Nickname: profile.Nickname);

        client.Send(OnAuthenticateInAckPacket.Build(identity));
        client.Send(OnGameStartInfPacket.Build(LocalLobbyBootstrap.GameStartParameters));
        client.Send(OnChannelInfoInfPacket.Build(channels));
        Logger.Info(client,
            $"Authenticated {(lease.IsReconnect ? "reconnecting" : "new")} " +
            $"{source} account {profile.AccountId} " +
            $"({profile.Nickname}, user {profile.UserId}).");
    }

    private static void Reject(Client client, string reason)
    {
        client.PlayerStore = null;
        client.UserId = null;
        client.Send(OnAuthenticateInAckPacket.Build(
            new AuthenticationIdentity(0, 0, 0, string.Empty, string.Empty, string.Empty),
            AuthenticationResult.Rejected));
        Logger.Error(client, $"Rejected launcher authentication: {reason}.");
    }
}
