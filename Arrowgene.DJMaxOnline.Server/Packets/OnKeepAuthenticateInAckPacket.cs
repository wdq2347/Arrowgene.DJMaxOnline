using Arrowgene.DJMaxOnline.Server.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Packets;

public static class OnKeepAuthenticateInAckPacket
{
    // Korean OnKeepAuthenticateInAck is 7 bytes total: id(2) + control(1) + 4 header
    // bytes, no data. (China carried 12 reserved bytes for a 15-byte packet.)
    private const int ReservedSize = 4;

    public static Packet Build(byte control = ProtocolPadding.Unused)
    {
        return DjMaxPacketBuilder.Fixed(PacketMeta.OnKeepAuthenticateInAck, control)
            .WritePadding(ReservedSize, ProtocolPadding.Unused)
            .Build();
    }

    public static void Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        reader.Skip(ReservedSize);
        reader.EnsureComplete();
    }
}
