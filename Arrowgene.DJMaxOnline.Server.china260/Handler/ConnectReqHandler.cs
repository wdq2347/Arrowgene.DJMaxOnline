using Arrowgene.DJMaxOnline.Server.China260.Packets;
using Arrowgene.Logging;

namespace Arrowgene.DJMaxOnline.Server.China260.Handler;

/// <summary>
/// Starts the China 2.60 pre-login handshake.
///
/// The server must stop here. The client responds with AuthenticateInSndAccReq
/// (0x11) on this first socket; only that request is allowed to select an account
/// and begin the server-list bootstrap. Sending the bootstrap immediately after
/// OnConnectAck skips the state transition the client uses to authenticate itself.
/// </summary>
public sealed class ConnectReqHandler : IPacketHandler
{
    private const ushort FirstAssignedUserId = 0x014D;
    private const int ConnectResponseDelayMilliseconds = 250;
    private static readonly object AssignedUserIdLock = new();
    private static ushort _nextAssignedUserId = FirstAssignedUserId;
    private static readonly ServerLogger Logger =
        LogProvider.Logger<ServerLogger>(typeof(ConnectReqHandler));

    public void Handle(Client client, Packet packet)
    {
        // The capture has a short gap between ConnectReq and OnConnectAck. Keeping it
        // small gives the opening scene time to attach its network sink without turning
        // the login sequence into a server-side timeout.
        _ = Task.Run(async () =>
        {
            await Task.Delay(ConnectResponseDelayMilliseconds);
            Respond(client);
        });
    }

    private static void Respond(Client client)
    {
        if (client.PendingConnectSeed is not { Length: OnConnectAckPacket.SeedSize } seed32)
        {
            Logger.Error(client, "ConnectReq has no valid pending cipher seed.");
            client.Close();
            return;
        }

        // OnConnectAck is the only clear-text server packet. It establishes the cipher
        // that protects the captured 0x11 request and every packet after it.
        client.Send(OnConnectAckPacket.Build(seed32, client.AssignedUserId));
        client.PendingConnectSeed = null;
        client.CipherSeed = seed32[..DjMaxCrypto.ChineseSeedSize];
        client.InitCrypto(DjMaxCrypto.InitChinese(seed32));

        Logger.Info(client,
            $"Sent OnConnectAck (result {(ushort)ConnectResult.Accepted}, assigned id " +
            $"{client.AssignedUserId}); awaiting AuthenticateInSndAccReq (0x11).");
    }

    public PacketId Id => PacketId.ConnectReq;

    public static ushort AllocateAssignedUserId()
    {
        lock (AssignedUserIdLock)
        {
            ushort assignedUserId = _nextAssignedUserId;
            _nextAssignedUserId = assignedUserId == ushort.MaxValue
                ? FirstAssignedUserId
                : checked((ushort)(assignedUserId + 1));
            return assignedUserId;
        }
    }
}
