using Arrowgene.DJMaxOnline.Server.Packets;
using Arrowgene.Logging;

namespace Arrowgene.DJMaxOnline.Server.Handler;

public class ConnectReqHandler : IPacketHandler
{
    private const ushort FirstAssignedUserId = 0x014D;
    private static readonly object AssignedUserIdLock = new();
    private static ushort _nextAssignedUserId = FirstAssignedUserId;

    private static readonly ServerLogger Logger = LogProvider.Logger<ServerLogger>(typeof(ConnectReqHandler));

    public void Handle(Client client, Packet packet)
    {
        // JP flow: ClientConnected already pushed the OnConnectAck (seed stashed in
        // client.CipherSeed) before the cipher. ConnectReq arrives unencrypted, so we
        // only NOW enable the MT+XTEA cipher keyed on that 30-byte seed — every packet
        // after ConnectReq (the encrypted auth, etc.) then decodes.
        if (client.CipherSeed is { Length: 30 } seed)
        {
            client.InitCrypto(DjMaxCrypto.InitJapanese(seed));
            Logger.Info("JP cipher enabled from OnConnectAck seed.");
        }
        else
        {
            Logger.Error("ConnectReq received but no 30-byte JP seed was stashed.");
        }
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
