using Arrowgene.DJMaxOnline.Server.Korea400.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Korea400.Packets;

public static class OnPingTestInfPacket
{
    public static Packet Build() =>
        new(PacketMeta.OnPingTestInf, new[] { ProtocolPadding.Unused });
}
