using Arrowgene.DJMaxOnline.Server.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Packets;

/// <summary>
/// Notifications for timed items whose expiry has passed. Both carry eight fixed slots of
/// {itemId, value}; an unused slot is the 0xFFFFFFFF sentinel, which the client skips
/// because sub_437680 tests the record's leading u16 against 0xFFFF.
/// </summary>
public static class ExpiredItemProtocol
{
    /// <summary>Slots each expiry notification carries; the client loops exactly 8.</summary>
    public const int RecordCount = 8;

    internal static void WriteRecords(
        DjMaxPacketBuilder builder,
        IReadOnlyList<TimedInventoryItem> expired)
    {
        ArgumentNullException.ThrowIfNull(expired);
        if (expired.Count > RecordCount)
        {
            throw new ArgumentException(
                $"At most {RecordCount} expiries fit one notification.", nameof(expired));
        }

        for (int i = 0; i < RecordCount; i++)
        {
            if (i < expired.Count)
            {
                builder.WriteUInt32(expired[i].ItemId).WriteUInt32(expired[i].Expiration);
            }
            else
            {
                builder.WriteUInt32(0xFFFFFFFF).WriteUInt32(0xFFFFFFFF);
            }
        }
    }

    internal static IReadOnlyList<TimedInventoryItem> ReadRecords(DjMaxPacketReader reader)
    {
        List<TimedInventoryItem> expired = [];
        for (int i = 0; i < RecordCount; i++)
        {
            uint itemId = reader.ReadUInt32();
            uint value = reader.ReadUInt32();
            if (itemId != 0xFFFFFFFF)
            {
                expired.Add(new TimedInventoryItem(itemId, value));
            }
        }
        return expired;
    }
}

/// <summary>
/// Expired mount items (0xE4, 131 bytes). sub_437680 walks eight 8-byte records at wire+3,
/// then memcpy's the 64-byte mount block from wire+67 into net+893395 - so the notification
/// also refreshes the equipped loadout in one packet.
/// </summary>
public static class OnExpiredMountItemInfPacket
{
    public static Packet Build(
        IReadOnlyList<TimedInventoryItem> expired,
        ReadOnlySpan<byte> mountLoadout,
        byte control = ProtocolPadding.Unused)
    {
        if (mountLoadout.Length != GameplayProtocol.MountSnapshotSize)
        {
            throw new ArgumentException(
                $"Mount loadout must be {GameplayProtocol.MountSnapshotSize} bytes.",
                nameof(mountLoadout));
        }

        DjMaxPacketBuilder builder =
            DjMaxPacketBuilder.Fixed(PacketMeta.OnExpiredMountItemInf, control);
        ExpiredItemProtocol.WriteRecords(builder, expired);
        return builder.WriteBytes(mountLoadout).Build();
    }

    public static (IReadOnlyList<TimedInventoryItem> Expired, byte[] MountLoadout) Parse(
        Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        IReadOnlyList<TimedInventoryItem> expired = ExpiredItemProtocol.ReadRecords(reader);
        byte[] loadout = reader.ReadBytes(GameplayProtocol.MountSnapshotSize);
        reader.EnsureComplete();
        return (expired, loadout);
    }
}

/// <summary>
/// Expired shop items (0xE5, 483 bytes). sub_437730 reads the same eight records at wire+3,
/// but its shop snapshot begins at raw +243 - the 176 bytes between are reserved and the
/// handler never touches them.
/// </summary>
public static class OnExpiredShopItemInfPacket
{
    /// <summary>Bytes between the last expiry record and the item box.</summary>
    private const int ReservedGap = 176;

    public static Packet Build(
        IReadOnlyList<TimedInventoryItem> expired,
        IReadOnlyList<TimedInventoryItem> itemBoxItems,
        byte control = ProtocolPadding.Unused)
    {
        DjMaxPacketBuilder builder =
            DjMaxPacketBuilder.Fixed(PacketMeta.OnExpiredShopItemInf, control);
        ExpiredItemProtocol.WriteRecords(builder, expired);
        builder.WritePadding(ReservedGap);
        OnPurchaseItemAckPacket.WriteItemBox(builder, itemBoxItems);
        return builder.Build();
    }

    public static (IReadOnlyList<TimedInventoryItem> Expired,
        IReadOnlyList<TimedInventoryItem> ItemBox) Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        IReadOnlyList<TimedInventoryItem> expired = ExpiredItemProtocol.ReadRecords(reader);
        reader.Skip(ReservedGap);
        List<TimedInventoryItem> box = [];
        for (int i = 0; i < OnPurchaseItemAckPacket.ItemBoxSlots; i++)
        {
            box.Add(new TimedInventoryItem(reader.ReadUInt32(), reader.ReadUInt32()));
        }
        reader.EnsureComplete();
        return (expired, box);
    }
}

/// <summary>
/// Billing/account alert (0xD4, 28 bytes): result u32@3, flag u8@7, five u32 @8..27.
/// sub_436B00 clears account-class bits 0x10000 and 0x20000 when the result is zero, so
/// this is also the packet that revokes premium.
/// </summary>
public static class OnUserAlertInfPacket
{
    public const int TrailingValueCount = 5;

    public static Packet Build(
        uint result,
        byte flag = 0,
        IReadOnlyList<uint>? values = null,
        byte control = ProtocolPadding.Unused)
    {
        DjMaxPacketBuilder builder = DjMaxPacketBuilder
            .Fixed(PacketMeta.OnUserAlertInf, control)
            .WriteUInt32(result)
            .WriteByte(flag);
        for (int i = 0; i < TrailingValueCount; i++)
        {
            builder.WriteUInt32(values != null && i < values.Count ? values[i] : 0);
        }
        return builder.Build();
    }

    public static (uint Result, byte Flag, uint[] Values) Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint result = reader.ReadUInt32();
        byte flag = reader.ReadByte();
        uint[] values = new uint[TrailingValueCount];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = reader.ReadUInt32();
        }
        reader.EnsureComplete();
        return (result, flag, values);
    }
}
