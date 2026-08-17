using Arrowgene.DJMaxOnline.Server.Japan400.Packets;

namespace Arrowgene.DJMaxOnline.Server.Japan400.Handler;

public class KeepAuthenticateInReqHandler : IPacketHandler
{
    public void Handle(Client client, Packet packet)
    {
        client.Send(OnKeepAuthenticateInAckPacket.Build());
    }

    public PacketId Id => PacketId.KeepAuthenticateInReq;
}
