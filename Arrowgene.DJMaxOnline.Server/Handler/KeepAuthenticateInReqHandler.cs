using Arrowgene.DJMaxOnline.Server.Packets;

namespace Arrowgene.DJMaxOnline.Server.Handler;

public class KeepAuthenticateInReqHandler : IPacketHandler
{
    public void Handle(Client client, Packet packet)
    {
        client.Send(OnKeepAuthenticateInAckPacket.Build());
    }

    public PacketId Id => PacketId.KeepAuthenticateInReq;
}
