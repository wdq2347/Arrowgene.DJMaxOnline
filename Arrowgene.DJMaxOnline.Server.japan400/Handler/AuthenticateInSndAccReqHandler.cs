using Arrowgene.DJMaxOnline.Server.Japan400.Packets;
using Arrowgene.Logging;

namespace Arrowgene.DJMaxOnline.Server.Japan400.Handler;

/// <summary>
/// The pre-login handshake used by the older China-style flow. The Korean client does not
/// take this path - it goes straight to LogInReq carrying a launcher ticket.
///
/// This handler must never hand out an account. It used to attach the server's shared
/// bootstrap store - the first row of the player database - so anything that sent this
/// packet became that account, and because LogInReq skips its ticket check when a store is
/// already attached, that was a complete bypass of authentication. The server now holds no
/// account until one is loaded from its own launcher ticket, so there is nothing to leak.
/// </summary>
public class AuthenticateInSndAccReqHandler : IPacketHandler
{
    private static readonly ServerLogger Logger =
        LogProvider.Logger<ServerLogger>(typeof(AuthenticateInSndAccReqHandler));

    private readonly Func<IReadOnlyList<ChannelInfo>> _channelSnapshot;
    private readonly LocalPlayerStore? _players;

    public AuthenticateInSndAccReqHandler(
        Func<IReadOnlyList<ChannelInfo>> channelSnapshot,
        LocalPlayerStore? players)
    {
        _channelSnapshot = channelSnapshot ??
            throw new ArgumentNullException(nameof(channelSnapshot));
        _players = players;
    }

    public void Handle(Client client, Packet packet)
    {
        AuthenticationRequest request = AuthenticateInSndAccReqPacket.Parse(packet);
        if (client.CipherSeed == null ||
            !request.CipherSeed.AsSpan().SequenceEqual(client.CipherSeed))
        {
            client.Close();
            return;
        }

        // Answer only for a connection that has already authenticated. There is no
        // stand-in identity to fall back on, and inventing one would be a way to reach an
        // account without a launcher ticket.
        LocalPlayerStore? store = client.PlayerStore ?? _players;
        if (store == null)
        {
            Logger.Error(client,
                "Refusing AuthenticateInSndAccReq: no authenticated player on this " +
                "connection. Log in through the launcher.");
            client.Close();
            return;
        }

        LocalPlayerProfile profile = store.Profile;

        AuthenticationIdentity identity = new(
            profile.UserId,
            AccountClass: profile.AccountClass,
            Level: profile.Progress.Level,
            AccountId: profile.AccountId,
            SecondaryId: profile.SecondaryId,
            Nickname: profile.Nickname);

        client.UserId = profile.UserId;
        client.Send(OnAuthenticateInAckPacket.Build(identity));
        client.Send(OnGameStartInfPacket.Build(LocalLobbyBootstrap.GameStartParameters));
        client.Send(OnChannelInfoInfPacket.Build(_channelSnapshot()));
    }

    public PacketId Id => PacketId.AuthenticateInSndAccReq;
}
