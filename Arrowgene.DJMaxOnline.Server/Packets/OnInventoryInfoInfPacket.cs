using Arrowgene.DJMaxOnline.Server.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Packets;

public sealed record TimedInventoryItem(uint ItemId, uint Expiration)
{
    public static TimedInventoryItem Empty { get; } = new(uint.MaxValue, uint.MaxValue);
}

/// <summary>
/// One row of the 선물 (present) box. THE WIRE ORDER IS NOT THE FIELD ORDER: the client
/// stores the box at net+893275 as 10 x 12 bytes and <c>sub_472FA5</c> reads
/// <c>{itemId @+0, value @+4, senderUserId @+8}</c> - it feeds the +8 dword to
/// <c>sub_433B80</c> (the user-id -> record map) and prints that record's nickname@+29 as
/// the "from" column, falling back to a TOOLBARMSG3 format of the raw id. Writing the
/// sender at +4 therefore printed the expiration as the sender and the sender id as the
/// value. Always serialise ItemId, Expiration, SenderUserId in that order - see
/// <see cref="WritePresentBox"/>, which is the single place that knows this.
/// </summary>
public sealed record PresentInventoryItem(
    uint ItemId,
    uint SenderUserId,
    uint Expiration)
{
    public static PresentInventoryItem Empty { get; } =
        new(uint.MaxValue, uint.MaxValue, uint.MaxValue);
}

/// <summary>
/// Inventory section boundaries and entry widths established by sub_432B80,
/// sub_432A30, sub_432AA0, and sub_432B10 in the retail client.
/// </summary>
public sealed record InventorySnapshot(
    IReadOnlyList<uint> DefaultItems,
    IReadOnlyList<uint> EventItems,
    IReadOnlyList<TimedInventoryItem> ShopItems,
    IReadOnlyList<PresentInventoryItem> PresentItems,
    IReadOnlyList<TimedInventoryItem> MountItems,
    uint State)
{
    public static InventorySnapshot Empty { get; } = new(
        Enumerable.Repeat(uint.MaxValue, OnInventoryInfoInfPacket.DefaultItemCount).ToArray(),
        Enumerable.Repeat(uint.MaxValue, OnInventoryInfoInfPacket.EventItemCount).ToArray(),
        Enumerable.Repeat(
            TimedInventoryItem.Empty,
            OnInventoryInfoInfPacket.ShopItemCount).ToArray(),
        Enumerable.Repeat(
            PresentInventoryItem.Empty,
            OnInventoryInfoInfPacket.PresentItemCount).ToArray(),
        Enumerable.Repeat(
            TimedInventoryItem.Empty,
            OnInventoryInfoInfPacket.MountItemCount).ToArray(),
        State: 0);
}

public static class OnInventoryInfoInfPacket
{
    public const int DefaultItemCount = 48;
    public const int EventItemCount = 32;
    public const int ShopItemCount = 30;
    public const int PresentItemCount = 10;
    public const int MountItemCount = 8;

    /// <summary>Serialises the 10-slot present box in the client's field order.</summary>
    public static DjMaxPacketBuilder WritePresentBox(
        DjMaxPacketBuilder builder,
        IReadOnlyList<PresentInventoryItem> presents)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(presents);
        for (int index = 0; index < PresentItemCount; index++)
        {
            PresentInventoryItem item = index < presents.Count
                ? presents[index]
                : PresentInventoryItem.Empty;
            builder
                .WriteUInt32(item.ItemId)
                .WriteUInt32(item.Expiration)
                .WriteUInt32(item.SenderUserId);
        }
        return builder;
    }

    /// <summary>Reads back what <see cref="WritePresentBox"/> wrote.</summary>
    public static PresentInventoryItem[] ReadPresentBox(DjMaxPacketReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        PresentInventoryItem[] presents = new PresentInventoryItem[PresentItemCount];
        for (int index = 0; index < presents.Length; index++)
        {
            uint itemId = reader.ReadUInt32();
            uint expiration = reader.ReadUInt32();
            uint senderUserId = reader.ReadUInt32();
            presents[index] = new PresentInventoryItem(itemId, senderUserId, expiration);
        }
        return presents;
    }

    public static Packet Build(
        InventorySnapshot inventory,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ValidateCount(inventory.DefaultItems, DefaultItemCount, nameof(inventory.DefaultItems));
        ValidateCount(inventory.EventItems, EventItemCount, nameof(inventory.EventItems));
        ValidateCount(inventory.ShopItems, ShopItemCount, nameof(inventory.ShopItems));
        ValidateCount(inventory.PresentItems, PresentItemCount, nameof(inventory.PresentItems));
        ValidateCount(inventory.MountItems, MountItemCount, nameof(inventory.MountItems));

        DjMaxPacketBuilder builder = DjMaxPacketBuilder.Fixed(
            PacketMeta.OnInventoryInfoInf, control);
        foreach (uint itemId in inventory.DefaultItems)
        {
            builder.WriteUInt32(itemId);
        }
        foreach (uint itemId in inventory.EventItems)
        {
            builder.WriteUInt32(itemId);
        }
        foreach (TimedInventoryItem item in inventory.ShopItems)
        {
            builder.WriteUInt32(item.ItemId).WriteUInt32(item.Expiration);
        }
        WritePresentBox(builder, inventory.PresentItems);
        foreach (TimedInventoryItem item in inventory.MountItems)
        {
            builder.WriteUInt32(item.ItemId).WriteUInt32(item.Expiration);
        }

        return builder.WriteUInt32(inventory.State).Build();
    }

    public static InventorySnapshot Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint[] defaultItems = ReadItemIds(reader, DefaultItemCount);
        uint[] eventItems = ReadItemIds(reader, EventItemCount);
        TimedInventoryItem[] shopItems = ReadTimedItems(reader, ShopItemCount);
        PresentInventoryItem[] presentItems = ReadPresentBox(reader);
        TimedInventoryItem[] mountItems = ReadTimedItems(reader, MountItemCount);
        uint state = reader.ReadUInt32();
        reader.EnsureComplete();
        return new InventorySnapshot(
            defaultItems, eventItems, shopItems, presentItems, mountItems, state);
    }

    private static uint[] ReadItemIds(DjMaxPacketReader reader, int count)
    {
        uint[] items = new uint[count];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = reader.ReadUInt32();
        }
        return items;
    }

    private static TimedInventoryItem[] ReadTimedItems(
        DjMaxPacketReader reader,
        int count)
    {
        TimedInventoryItem[] items = new TimedInventoryItem[count];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = new TimedInventoryItem(
                reader.ReadUInt32(), reader.ReadUInt32());
        }
        return items;
    }

    private static void ValidateCount<T>(
        IReadOnlyCollection<T> values,
        int expected,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count != expected)
        {
            throw new ArgumentException(
                $"Section must contain {expected} entries, received {values.Count}.",
                parameterName);
        }
    }
}
