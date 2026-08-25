using Arrowgene.DJMaxOnline.Server.China260.Packets;

namespace Arrowgene.DJMaxOnline.Server.China260.Handler;

public class KeepAuthenticateInReqHandler : IPacketHandler
{
    public void Handle(Client client, Packet packet)
    {
        client.Send(OnKeepAuthenticateInAckPacket.Build());
    }

    public PacketId Id => PacketId.KeepAuthenticateInReq;
}
