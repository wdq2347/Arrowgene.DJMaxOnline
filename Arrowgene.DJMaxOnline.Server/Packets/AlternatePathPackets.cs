using Arrowgene.DJMaxOnline.Server.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Packets;

/// <summary>
/// Bare Korean 0xA3 lobby/invitation transition request. The sender at sub_4344B0
/// passes a total wire size of three to sub_437F20, so this request has no structured
/// fields. Its state transition remains deliberately unnamed until a retail capture
/// proves whether it means invite acceptance or another room-entry branch.
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
/// Korean 0x56 peer-state request. sub_434620 sends one byte selected by the peer
/// lookup path in sub_4528AF. Keep the value intact without assigning boolean or
/// acknowledgement semantics that the client code alone does not prove.
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
