using Arrowgene.DJMaxOnline.Server.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Packets;

/// <summary>
/// Session values copied verbatim by client handler sub_438C20. Their gameplay
/// meanings are not asserted until they can be verified at their consumers.
/// </summary>
public sealed record GameStartParameters(
    byte Mode,
    byte Flags,
    uint SessionValue1,
    uint SessionValue2,
    uint SessionValue3,
    uint SessionValue4,
    uint SessionValue5);

public static class OnGameStartInfPacket
{
    // Korean layout (client sub_436A50): Mode@3, Flags@4, five session dwords
    // @5/9/13/17/21 = 25 wire bytes. China appended 8 trailing padding bytes (33).
    //
    // DANGER: sub_436A50 stores these at net+895136..895156, which the COURSE module uses
    // as its request gates (895136 = ChangeCourseReq pending, 895140 = ContinueCourseReq
    // pending). A non-zero Mode, Flags or SessionValue1 therefore permanently blocks the
    // matching course request. See LocalLobbyBootstrap.GameStartParameters. The same
    // storage is shared by 0xD2 (OnBillingAuthInf) and 0xD4 (OnUserAlertInf), so those two
    // must not be sent outside deliberate testing.
    public static Packet Build(
        GameStartParameters parameters,
        byte control = ProtocolPadding.Unused)
    {
        return DjMaxPacketBuilder.Fixed(PacketMeta.OnGameStartInf, control)
            .WriteByte(parameters.Mode)
            .WriteByte(parameters.Flags)
            .WriteUInt32(parameters.SessionValue1)
            .WriteUInt32(parameters.SessionValue2)
            .WriteUInt32(parameters.SessionValue3)
            .WriteUInt32(parameters.SessionValue4)
            .WriteUInt32(parameters.SessionValue5)
            .Build();
    }

    public static GameStartParameters Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        GameStartParameters result = new(
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32());
        reader.EnsureComplete();
        return result;
    }
}
