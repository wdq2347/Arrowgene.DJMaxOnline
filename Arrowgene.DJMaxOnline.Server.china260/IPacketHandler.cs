namespace Arrowgene.DJMaxOnline.Server.China260;

public interface IPacketHandler
{
    void Handle(Client client, Packet packet);
    PacketId Id { get; }
}