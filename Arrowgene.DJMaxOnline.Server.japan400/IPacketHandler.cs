namespace Arrowgene.DJMaxOnline.Server.Japan400;

public interface IPacketHandler
{
    void Handle(Client client, Packet packet);
    PacketId Id { get; }
}