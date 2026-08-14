using Arrowgene.DJMaxOnline.Server.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Packets;

/// <summary>
/// The equipment screen's "equip mount loadout" request (0xD7, sub_4395C0). The
/// 64-byte structured payload is 8 slots × 8 bytes (item id u32 + expiration u32);
/// empty slots are 0xFF-filled.
/// </summary>
public static class MountItemReqPacket
{
    public const int SlotCount = 8;
    public const int SlotSize = 8;
    public const int LoadoutSize = SlotCount * SlotSize; // 64

    public static byte[] Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        byte[] loadout = reader.ReadBytes(LoadoutSize);
        reader.EnsureComplete();
        return loadout;
    }
}

public enum MountItemResult : ushort
{
    Failed = 0,
    Success = 165
}

public sealed record MountItemResponse(MountItemResult Result, byte[] Loadout);

/// <summary>
/// Confirms an equip request (0xD8). The Korean client handler sub_437430 only
/// applies the loadout when the leading u16 result at packet+3 == 165, then copies
/// the following 64 bytes (packet+5) as the equipped set — so we echo the requested
/// loadout back with result 165. (Was 167, which the client rejected → equip did
/// nothing.) Total wire size is 69 bytes.
/// </summary>
public static class OnMountItemAckPacket
{
    public static Packet Build(
        MountItemResponse response,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Loadout.Length != MountItemReqPacket.LoadoutSize)
        {
            throw new ArgumentException(
                $"Mount loadout must be {MountItemReqPacket.LoadoutSize} bytes.",
                nameof(response));
        }

        return DjMaxPacketBuilder.Fixed(PacketMeta.OnMountItemAck, control)
            .WriteUInt16((ushort)response.Result)
            .WriteBytes(response.Loadout)
            .Build();
    }

    public static Packet BuildSuccess(
        ReadOnlySpan<byte> loadout,
        byte control = ProtocolPadding.Unused) =>
        Build(new MountItemResponse(MountItemResult.Success, loadout.ToArray()), control);
}
