using Arrowgene.DJMaxOnline.Server.China260.Protocol;

namespace Arrowgene.DJMaxOnline.Server.China260.Packets;

/// <summary>
/// JP 0xA3 lobby/invitation transition request. The sender at sub_435320 emits an
/// 11-byte tail-padded frame with no structured fields. Its state transition remains
/// deliberately unnamed until a retail capture proves whether it means invite acceptance
/// or another room-entry branch.
/// </summary>
public static class UnknownA3ReqPacket
{
    public static void Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        reader.EnsureComplete();
    }
}

/// <summary>
/// JP 0x56 peer-state request. sub_4354D0 sends one byte at raw+3 followed by the normal
/// eight-byte tail. Keep the value intact without assigning boolean or acknowledgement
/// semantics that the client code alone does not prove.
/// </summary>
public static class PeerStateReqPacket
{
    public static byte Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        byte state = reader.ReadByte();
        reader.EnsureComplete();
        return state;
    }
}
