using Arrowgene.DJMaxOnline.Server.Japan400.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Japan400.Packets;

public static class OnPingTestInfPacket
{
    public static Packet Build() =>
        new(PacketMeta.OnPingTestInf, new[] { ProtocolPadding.Unused });
}
