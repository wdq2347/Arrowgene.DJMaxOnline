using Arrowgene.DJMaxOnline.Server.China260.Protocol;

namespace Arrowgene.DJMaxOnline.Server.China260.Packets;

public static class OnPingTestInfPacket
{
    public static Packet Build() =>
        new(PacketMeta.OnPingTestInf, new[] { ProtocolPadding.Unused });
}
