using Arrowgene.DJMaxOnline.Server.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Packets;

public static class OnPingTestInfPacket
{
    public static Packet Build() =>
        new(PacketMeta.OnPingTestInf, new[] { ProtocolPadding.Unused });
}
