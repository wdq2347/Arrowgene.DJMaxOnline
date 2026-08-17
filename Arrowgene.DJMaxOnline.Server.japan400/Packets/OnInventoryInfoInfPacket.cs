using Arrowgene.DJMaxOnline.Server.Japan400.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Japan400.Packets;

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
/// Inventory section boundaries and entry widths established by the Japanese
/// client's <c>DJMaxNet::OnInventoryInfoInf</c> handler. Its 748-byte body is
/// copied verbatim to net+892783: the first 48 records are the collection cache
/// (<c>{code:u16, value:u16}</c>), followed by 32 event-item records of the same
/// shape, then the actual shop, present, and mount inventories.
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

    /// <summary>
    /// Rebuilds a captured packet without changing its two opaque cache sections.
    /// Runtime JP code should use the collection overload below so normal item ids
    /// cannot leak into the collection cache.
    /// </summary>
    public static Packet Build(
        InventorySnapshot inventory,
        byte control = ProtocolPadding.Unused) =>
        BuildCore(
            inventory,
            ToEntries(inventory.DefaultItems),
            ToEntries(inventory.EventItems),
            control);

    /// <summary>
    /// Builds Japan's 748-byte inventory/cache snapshot. The names
    /// <see cref="InventorySnapshot.DefaultItems"/> and
    /// <see cref="InventorySnapshot.EventItems"/> are retained for compatible
    /// profile storage, but they are <em>not</em> u32 item-id arrays on this wire
    /// format. Writing them here caused item 1025 (0x401) to be decoded by the
    /// collection dialog as a Rainbow disc.
    /// </summary>
    public static Packet Build(
        InventorySnapshot inventory,
        IReadOnlyList<CollectionEntry> collection,
        byte control = ProtocolPadding.Unused) =>
        BuildCore(inventory, collection, [], control);

    private static Packet BuildCore(
        InventorySnapshot inventory,
        IReadOnlyList<CollectionEntry> collection,
        IReadOnlyList<CollectionEntry> eventItems,
        byte control)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(eventItems);
        if (collection.Count > DefaultItemCount)
        {
            throw new ArgumentException(
                $"Collection cache cannot exceed {DefaultItemCount} entries.", nameof(collection));
        }
        if (eventItems.Count > EventItemCount)
        {
            throw new ArgumentException(
                $"Event-item cache cannot exceed {EventItemCount} entries.", nameof(eventItems));
        }
        ValidateCount(inventory.ShopItems, ShopItemCount, nameof(inventory.ShopItems));
        ValidateCount(inventory.PresentItems, PresentItemCount, nameof(inventory.PresentItems));
        ValidateCount(inventory.MountItems, MountItemCount, nameof(inventory.MountItems));

        DjMaxPacketBuilder builder = DjMaxPacketBuilder.Fixed(
            PacketMeta.OnInventoryInfoInf, control);
        for (int index = 0; index < DefaultItemCount; index++)
        {
            if (index < collection.Count)
            {
                CollectionEntry entry = collection[index];
                builder.WriteUInt16(entry.Code).WriteUInt16(entry.Value);
            }
            else
            {
                builder.WriteUInt16(0xFFFF).WriteUInt16(0);
            }
        }

        for (int index = 0; index < EventItemCount; index++)
        {
            if (index < eventItems.Count)
            {
                CollectionEntry entry = eventItems[index];
                builder.WriteUInt16(entry.Code).WriteUInt16(entry.Value);
            }
            else
            {
                builder.WriteUInt16(0xFFFF).WriteUInt16(0);
            }
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

    private static CollectionEntry[] ToEntries(IReadOnlyList<uint> rawEntries)
    {
        ArgumentNullException.ThrowIfNull(rawEntries);
        CollectionEntry[] entries = new CollectionEntry[rawEntries.Count];
        for (int index = 0; index < entries.Length; index++)
        {
            uint raw = rawEntries[index];
            entries[index] = new CollectionEntry((ushort)raw, (ushort)(raw >> 16));
        }
        return entries;
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
