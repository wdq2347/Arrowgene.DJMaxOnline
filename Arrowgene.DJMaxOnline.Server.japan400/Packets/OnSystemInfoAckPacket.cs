using Arrowgene.DJMaxOnline.Server.Japan400.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Japan400.Packets;

/// <summary>
/// Reply to the equipment-room open request (0xFD). The client (sub_434B40) sets a
/// "request in flight" flag when it opens 소지품/Belongings and stalls the screen
/// until this ack arrives; the handler (sub_434BA0) clears that flag and fires the
/// dialog, which then renders the inventory we already sent at login. The 66-byte
/// structured payload is taken from a decrypted retail capture (system date/version
/// fields followed by 0xCC padding); the equipment view doesn't parse it.
/// </summary>
public static class OnSystemInfoAckPacket
{
    private static readonly byte[] SystemInfo =
        Convert.FromHexString("0000e8070600100000002c003300c0366639");
    private const int PaddingSize = 48;

    public static Packet Build(byte control = ProtocolPadding.Unused)
    {
        return DjMaxPacketBuilder.Fixed(PacketMeta.OnSystemInfoAck, control)
            .WriteBytes(SystemInfo)
            .WritePadding(PaddingSize, ProtocolPadding.Unused)
            .Build();
    }
}
