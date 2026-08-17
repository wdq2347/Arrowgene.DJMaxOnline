namespace Arrowgene.DJMaxOnline.Server.Korea400;

public interface IPacketHandler
{
    void Handle(Client client, Packet packet);
    PacketId Id { get; }
}