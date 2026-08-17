using Arrowgene.DJMaxOnline.Server.Japan400.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Japan400.Packets;

public sealed record UserIconUpdate(
    uint UserId,
    ushort ProfileCode,
    uint IconId,
    uint AccountClass);

/// <summary>
/// Updates the zero-based global user-icon id. sub_435A40 decrements the
/// transmitted value before storing it in both the local and lobby user data.
/// </summary>
public static class OnUpdateUserIconInfPacket
{
    // Korean OnUpdateUserIconInf (0x24) is 17 wire bytes. Two client sites read it:
    //   - storage sub_435A40: userId@3 (matched vs the local userId to write
    //     net+794153), connid@7, icon dword@9 (decremented before storing).
    //   - lobby-scene repaint sub_441675: it repaints the self-profile card ONLY
    //     when connid@7 == net+793744 (the peer key the OnConnectAck assigned at
    //     word+35, i.e. the client's AssignedUserId — NOT profile.ProfileCode), and
    //     it forwards icon@9 AND accountClass@13 to the icon setter sub_461DC5.
    // So @7 must be the client's assigned connection id and @13 the account class,
    // or the card never repaints / loses its premium frame.
    public static Packet Build(
        uint userId,
        ushort profileCode,
        uint iconId,
        uint accountClass,
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnUpdateUserIconInf, control)
            .WriteUInt32(userId)                              // @3
            .WriteUInt16(profileCode)                         // @7 peer key (connid)
            .WriteUInt32(LocalPlayerProfile.EncodeIconId(iconId)) // @9 icon (client decrements)
            .WriteUInt32(accountClass)                        // @13 account class (premium frame)
            .Build();

    public static UserIconUpdate Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint userId = reader.ReadUInt32();
        ushort profileCode = reader.ReadUInt16();
        uint iconId = unchecked(reader.ReadUInt32() - 1);
        uint accountClass = reader.ReadUInt32();
        reader.EnsureComplete();
        return new UserIconUpdate(userId, profileCode, iconId, accountClass);
    }
}

public sealed record UserPropertyUpdate(
    uint UserId,
    ushort UpdateCode,
    LocalPlayerProgress Progress);

/// <summary>
/// Replaces the complete progress block. Field offsets are taken directly from
/// client handler sub_437BD0.
/// </summary>
public static class OnUpdateUserPropertyInfPacket
{
    public static Packet Build(
        uint userId,
        LocalPlayerProgress progress,
        ushort updateCode = 0,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(progress);
        progress.Validate();
        DjMaxPacketBuilder builder = DjMaxPacketBuilder.Fixed(
                PacketMeta.OnUpdateUserPropertyInf, control)
            .WriteUInt32(userId)
            .WriteUInt16(updateCode)
            .WriteUInt32(progress.Level)
            .WriteUInt32(progress.Experience)
            .WriteUInt32(progress.Money)
            .WriteUInt32(progress.Wins)
            .WriteUInt32(progress.Losses)
            .WriteUInt32(progress.Draws);
        WriteMisc(builder, progress.MiscStatistics);
        return builder.Build();
    }

    public static UserPropertyUpdate Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint userId = reader.ReadUInt32();
        ushort updateCode = reader.ReadUInt16();
        LocalPlayerProgress progress = new()
        {
            Level = reader.ReadUInt32(),
            Experience = reader.ReadUInt32(),
            Money = reader.ReadUInt32(),
            Wins = reader.ReadUInt32(),
            Losses = reader.ReadUInt32(),
            Draws = reader.ReadUInt32(),
            MiscStatistics = ReadMisc(reader)
        };
        reader.EnsureComplete();
        return new UserPropertyUpdate(userId, updateCode, progress);
    }

    internal static void WriteMisc(DjMaxPacketBuilder builder, IReadOnlyList<uint> misc)
    {
        if (misc.Count != LocalPlayerProgress.MiscStatisticCount)
        {
            throw new ArgumentException(
                $"MISC block must contain {LocalPlayerProgress.MiscStatisticCount} values.",
                nameof(misc));
        }

        foreach (uint value in misc)
        {
            builder.WriteUInt32(value);
        }
    }

    internal static uint[] ReadMisc(DjMaxPacketReader reader)
    {
        uint[] values = new uint[LocalPlayerProgress.MiscStatisticCount];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = reader.ReadUInt32();
        }
        return values;
    }
}

public sealed record UserLevelUpdate(
    uint UserId,
    ushort UpdateCode,
    uint Experience,
    uint Level);

public static class OnUpdateUserPropertyLevelInfPacket
{
    public static Packet Build(
        uint userId,
        uint experience,
        uint level,
        ushort updateCode = 0,
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnUpdateUserPropertyLevelInf, control)
            .WriteUInt32(userId)
            .WriteUInt16(updateCode)
            // sub_437EE0 uses the opposite ordering from the complete block.
            .WriteUInt32(experience)
            .WriteUInt32(level)
            .Build();

    public static UserLevelUpdate Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        UserLevelUpdate value = new(
            reader.ReadUInt32(),
            reader.ReadUInt16(),
            reader.ReadUInt32(),
            reader.ReadUInt32());
        reader.EnsureComplete();
        return value;
    }
}

public sealed record UserMoneyUpdate(
    uint UserId,
    ushort UpdateCode,
    uint Money);

public static class OnUpdateUserPropertyMoneyInfPacket
{
    public static Packet Build(
        uint userId,
        uint money,
        ushort updateCode = 0,
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnUpdateUserPropertyMoneyInf, control)
            .WriteUInt32(userId)
            .WriteUInt16(updateCode)
            .WriteUInt32(money)
            .Build();

    public static UserMoneyUpdate Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        UserMoneyUpdate value = new(
            reader.ReadUInt32(), reader.ReadUInt16(), reader.ReadUInt32());
        reader.EnsureComplete();
        return value;
    }
}

public sealed record UserRecordUpdate(
    uint UserId,
    ushort UpdateCode,
    uint Wins,
    uint Losses,
    uint Draws);

public static class OnUpdateUserPropertyRecordInfPacket
{
    public static Packet Build(
        uint userId,
        uint wins,
        uint losses,
        uint draws,
        ushort updateCode = 0,
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnUpdateUserPropertyRecordInf, control)
            .WriteUInt32(userId)
            .WriteUInt16(updateCode)
            .WriteUInt32(wins)
            .WriteUInt32(losses)
            .WriteUInt32(draws)
            .Build();

    public static UserRecordUpdate Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        UserRecordUpdate value = new(
            reader.ReadUInt32(),
            reader.ReadUInt16(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32());
        reader.EnsureComplete();
        return value;
    }
}

public sealed record UserMiscUpdate(
    uint UserId,
    ushort UpdateCode,
    IReadOnlyList<uint> MiscStatistics);

public static class OnUpdateUserPropertyMiscInfPacket
{
    /// <summary>
    /// Pushes the ten stat words the client keeps at login-block +89..+128. sub_435D10
    /// memcpy's this payload straight over that span, so the values must be the block
    /// <see cref="UserStatisticsBlock"/> builds - best scores, max combo, accuracy, cash.
    /// Feeding it anything else silently corrupts the player's SCORE tab.
    /// </summary>
    public static Packet Build(
        uint userId,
        IReadOnlyList<uint> miscStatistics,
        ushort updateCode = 0,
        byte control = ProtocolPadding.Unused)
    {
        DjMaxPacketBuilder builder = DjMaxPacketBuilder.Fixed(
                PacketMeta.OnUpdateUserPropertyMiscInf, control)
            .WriteUInt32(userId)
            .WriteUInt16(updateCode);
        OnUpdateUserPropertyInfPacket.WriteMisc(builder, miscStatistics);
        return builder.Build();
    }

    public static UserMiscUpdate Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint userId = reader.ReadUInt32();
        ushort updateCode = reader.ReadUInt16();
        uint[] misc = OnUpdateUserPropertyInfPacket.ReadMisc(reader);
        reader.EnsureComplete();
        return new UserMiscUpdate(userId, updateCode, misc);
    }
}

/// <summary>
/// 0x2A, 199 bytes. In this Korean executable <c>sub_435F30</c> copies the 192-byte
/// tail directly into net+892715, the same 48-slot collection block populated by
/// OnLogInAck. Each slot is {code:u16, value:u16}; 0xFFFF is the empty sentinel.
/// Use <see cref="BuildKorean"/> for the live collection cache. <see cref="Build"/>
/// remains for capture-verifier compatibility with clients that treated the block as
/// 48 plain u32 item ids.
/// </summary>
public static class OnUpdateUserInventoryDefaultItemInfPacket
{
    public static Packet BuildKorean(
        uint userId,
        IReadOnlyList<CollectionEntry> entries,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(entries);
        int slots = OnInventoryInfoInfPacket.DefaultItemCount;
        if (entries.Count > slots)
        {
            throw new ArgumentException(
                $"Collection array holds at most {slots} entries.", nameof(entries));
        }

        DjMaxPacketBuilder builder = DjMaxPacketBuilder.Fixed(
                PacketMeta.OnUpdateUserInventoryDefaultItemInf, control)
            .WriteUInt32(userId);
        for (int index = 0; index < slots; index++)
        {
            if (index < entries.Count)
            {
                builder.WriteUInt16(entries[index].Code).WriteUInt16(entries[index].Value);
            }
            else
            {
                builder.WriteUInt16(0xFFFF).WriteUInt16(0);
            }
        }
        return builder.Build();
    }

    public static (uint UserId, IReadOnlyList<CollectionEntry> Entries) ParseKorean(
        Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint userId = reader.ReadUInt32();
        List<CollectionEntry> entries = [];
        for (int index = 0; index < OnInventoryInfoInfPacket.DefaultItemCount; index++)
        {
            ushort code = reader.ReadUInt16();
            ushort value = reader.ReadUInt16();
            if (code != 0xFFFF)
            {
                entries.Add(new CollectionEntry(code, value));
            }
        }
        reader.EnsureComplete();
        return (userId, entries);
    }

    public static Packet Build(
        uint userId,
        IReadOnlyList<uint> items,
        byte control = ProtocolPadding.Unused)
    {
        ValidateCount(items, OnInventoryInfoInfPacket.DefaultItemCount, nameof(items));
        DjMaxPacketBuilder builder = DjMaxPacketBuilder.Fixed(
                PacketMeta.OnUpdateUserInventoryDefaultItemInf, control)
            .WriteUInt32(userId);
        WriteItemIds(builder, items);
        return builder.Build();
    }

    public static (uint UserId, IReadOnlyList<uint> Items) Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint userId = reader.ReadUInt32();
        uint[] items = ReadItemIds(reader, OnInventoryInfoInfPacket.DefaultItemCount);
        reader.EnsureComplete();
        return (userId, items);
    }

    internal static void ValidateCount<T>(
        IReadOnlyCollection<T>? items,
        int expected,
        string name)
    {
        if (items == null || items.Count != expected)
        {
            throw new ArgumentException(
                $"Inventory update {name} must contain exactly {expected} entries.", name);
        }
    }

    internal static void WriteItemIds(DjMaxPacketBuilder builder, IReadOnlyList<uint> items)
    {
        foreach (uint item in items)
        {
            builder.WriteUInt32(item);
        }
    }

    internal static uint[] ReadItemIds(DjMaxPacketReader reader, int count)
    {
        uint[] items = new uint[count];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = reader.ReadUInt32();
        }
        return items;
    }
}

/// <summary>
/// 0x2B, 135 bytes. <b>The Korean client does not treat this as a list of item ids.</b>
/// <c>sub_435F90</c> memcpy's the 128-byte tail straight into net+892907, and both readers
/// of that array — <c>sub_4372E0</c> ("do I own code X", returning the entry's VALUE) and
/// the 마이컬렉션 dialog <c>sub_465AD1</c> — treat it as 32 x {code:u16, value:u16}. Writing
/// 32 plain u32 ids puts a 0 in every high half, so <c>sub_4372E0</c> answers "not owned"
/// for everything we send. Use <see cref="BuildKorean"/>; <see cref="Build"/> is the China
/// layout kept only so the capture verifier can still round-trip China captures.
///
/// The handler also acts on the delta: with an event armed (net+894420 = 3..7 from
/// OnJoinEventInf 0x61), a code of 0xF801..0xF805 that was NOT owned before this packet
/// and IS after makes the client immediately fire 0x23 to equip it as its icon.
/// </summary>
public static class OnUpdateUserInventoryEventItemInfPacket
{
    /// <summary>
    /// The Korean shape: 32 x {code:u16, value:u16}, 0xFFFF code = empty slot. A value of
    /// zero reads as "not owned" no matter what the code is, so an owned entry needs a
    /// non-zero value.
    /// </summary>
    public static Packet BuildKorean(
        uint userId,
        IReadOnlyList<CollectionEntry> entries,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(entries);
        int slots = OnInventoryInfoInfPacket.EventItemCount;
        if (entries.Count > slots)
        {
            throw new ArgumentException(
                $"Event/collection array holds at most {slots} entries.", nameof(entries));
        }

        DjMaxPacketBuilder builder = DjMaxPacketBuilder.Fixed(
                PacketMeta.OnUpdateUserInventoryEventItemInf, control)
            .WriteUInt32(userId);
        for (int index = 0; index < slots; index++)
        {
            if (index < entries.Count)
            {
                builder.WriteUInt16(entries[index].Code).WriteUInt16(entries[index].Value);
            }
            else
            {
                builder.WriteUInt16(0xFFFF).WriteUInt16(0);
            }
        }
        return builder.Build();
    }

    /// <summary>Reads back what <see cref="BuildKorean"/> wrote.</summary>
    public static (uint UserId, IReadOnlyList<CollectionEntry> Entries) ParseKorean(
        Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint userId = reader.ReadUInt32();
        List<CollectionEntry> entries = [];
        for (int index = 0; index < OnInventoryInfoInfPacket.EventItemCount; index++)
        {
            ushort code = reader.ReadUInt16();
            ushort value = reader.ReadUInt16();
            if (code != 0xFFFF)
            {
                entries.Add(new CollectionEntry(code, value));
            }
        }
        reader.EnsureComplete();
        return (userId, entries);
    }

    public static Packet Build(
        uint userId,
        IReadOnlyList<uint> items,
        byte control = ProtocolPadding.Unused)
    {
        OnUpdateUserInventoryDefaultItemInfPacket.ValidateCount(
            items, OnInventoryInfoInfPacket.EventItemCount, nameof(items));
        DjMaxPacketBuilder builder = DjMaxPacketBuilder.Fixed(
                PacketMeta.OnUpdateUserInventoryEventItemInf, control)
            .WriteUInt32(userId);
        OnUpdateUserInventoryDefaultItemInfPacket.WriteItemIds(builder, items);
        return builder.Build();
    }

    public static (uint UserId, IReadOnlyList<uint> Items) Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint userId = reader.ReadUInt32();
        uint[] items = OnUpdateUserInventoryDefaultItemInfPacket.ReadItemIds(
            reader, OnInventoryInfoInfPacket.EventItemCount);
        reader.EnsureComplete();
        return (userId, items);
    }
}

public static class OnUpdateUserInventoryShopItemInfPacket
{
    public static Packet Build(
        uint userId,
        IReadOnlyList<TimedInventoryItem> items,
        byte control = ProtocolPadding.Unused) =>
        BuildTimed(
            PacketMeta.OnUpdateUserInventoryShopItemInf,
            userId,
            items,
            OnInventoryInfoInfPacket.ShopItemCount,
            control);

    /// <summary>
    /// Pushes the 30-slot item box, padding the empty slots exactly as the shop
    /// acknowledgements do. sub_4361E0 memcpy's 240 bytes from wire+7 into net+893035 -
    /// the same region OnPurchaseItemAck/OnDeleteItemAck write - so this is how the box
    /// refreshes after a change that no shop acknowledgement covers.
    /// </summary>
    public static Packet BuildItemBox(
        uint userId,
        IReadOnlyList<TimedInventoryItem> itemBoxItems,
        byte control = ProtocolPadding.Unused)
    {
        DjMaxPacketBuilder builder = DjMaxPacketBuilder
            .Fixed(PacketMeta.OnUpdateUserInventoryShopItemInf, control)
            .WriteUInt32(userId);
        OnPurchaseItemAckPacket.WriteItemBox(builder, itemBoxItems);
        return builder.Build();
    }

    public static (uint UserId, IReadOnlyList<TimedInventoryItem> Items) Parse(Packet packet) =>
        ParseTimed(packet, OnInventoryInfoInfPacket.ShopItemCount);

    internal static Packet BuildTimed(
        PacketMeta meta,
        uint userId,
        IReadOnlyList<TimedInventoryItem> items,
        int expected,
        byte control)
    {
        OnUpdateUserInventoryDefaultItemInfPacket.ValidateCount(items, expected, nameof(items));
        DjMaxPacketBuilder builder = DjMaxPacketBuilder.Fixed(meta, control).WriteUInt32(userId);
        foreach (TimedInventoryItem item in items)
        {
            builder.WriteUInt32(item.ItemId).WriteUInt32(item.Expiration);
        }
        return builder.Build();
    }

    internal static (uint UserId, IReadOnlyList<TimedInventoryItem> Items) ParseTimed(
        Packet packet,
        int count)
    {
        DjMaxPacketReader reader = new(packet);
        uint userId = reader.ReadUInt32();
        TimedInventoryItem[] items = new TimedInventoryItem[count];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = new TimedInventoryItem(reader.ReadUInt32(), reader.ReadUInt32());
        }
        reader.EnsureComplete();
        return (userId, items);
    }
}

public static class OnUpdateUserInventoryMountItemInfPacket
{
    public static Packet Build(
        uint userId,
        IReadOnlyList<TimedInventoryItem> items,
        byte control = ProtocolPadding.Unused) =>
        OnUpdateUserInventoryShopItemInfPacket.BuildTimed(
            PacketMeta.OnUpdateUserInventoryMountItemInf,
            userId,
            items,
            OnInventoryInfoInfPacket.MountItemCount,
            control);

    public static (uint UserId, IReadOnlyList<TimedInventoryItem> Items) Parse(Packet packet) =>
        OnUpdateUserInventoryShopItemInfPacket.ParseTimed(
            packet, OnInventoryInfoInfPacket.MountItemCount);
}

/// <summary>
/// Refreshes the ten-slot present/gift box. JPmax registers 0x2E at 127 bytes and
/// its handler <c>sub_437570</c> copies the 120-byte block at raw+7 into the same
/// cache updated by <see cref="OnGetPresentItemAckPacket"/>.
/// </summary>
public static class OnUpdateUserInventoryPresentItemInfPacket
{
    public static Packet Build(
        uint userId,
        IReadOnlyList<PresentInventoryItem> presents,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(presents);
        if (presents.Count > OnInventoryInfoInfPacket.PresentItemCount)
        {
            throw new ArgumentException(
                $"Present list cannot exceed {OnInventoryInfoInfPacket.PresentItemCount} entries.",
                nameof(presents));
        }

        DjMaxPacketBuilder builder = DjMaxPacketBuilder.Fixed(
                PacketMeta.OnUpdateUserInventoryPresentItemInf, control)
            .WriteUInt32(userId);
        return OnInventoryInfoInfPacket.WritePresentBox(builder, presents).Build();
    }

    public static (uint UserId, IReadOnlyList<PresentInventoryItem> Presents) Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint userId = reader.ReadUInt32();
        PresentInventoryItem[] presents = OnInventoryInfoInfPacket.ReadPresentBox(reader);
        reader.EnsureComplete();
        return (userId, presents);
    }
}

public sealed record UserAccountClassUpdate(uint UserId, uint AccountClass);

/// <summary>
/// Updates a user's account-class flags. The client handler sub_437D90 reads the
/// user ID at raw+3 and the complete class mask at raw+7; JPmax's 19-byte registration
/// leaves the remaining eight bytes reserved. No inventory follows; present items
/// use the dedicated <see cref="OnUpdateUserInventoryPresentItemInfPacket"/> refresh.
/// </summary>
public static class OnUpdateUserAccountClassInfPacket
{
    public static Packet Build(
        uint userId,
        uint accountClass,
        byte control = ProtocolPadding.Unused)
        => DjMaxPacketBuilder.Fixed(PacketMeta.OnUpdateUserAccountClassInf, control)
            .WriteUInt32(userId)
            .WriteUInt32(accountClass)
            .Build();

    public static UserAccountClassUpdate Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        UserAccountClassUpdate update = new(reader.ReadUInt32(), reader.ReadUInt32());
        reader.EnsureComplete();
        return update;
    }
}

/// <summary>Builds every packet needed to refresh mutable local-player data.</summary>
public static class LocalPlayerDataUpdatePackets
{
    public static IReadOnlyList<Packet> Build(LocalPlayerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        InventorySnapshot inventory = profile.Inventory.CreateSnapshot();
        List<Packet> packets = [];
        if (profile.IconId is uint iconId)
        {
            packets.Add(OnUpdateUserIconInfPacket.Build(
                profile.WireUserId, profile.ProfileCode, iconId, profile.AccountClass));
        }
        packets.Add(OnUpdateUserAccountClassInfPacket.Build(
            profile.WireUserId, profile.AccountClass));
        packets.AddRange(new Packet[]
        {
            OnUpdateUserPropertyInfPacket.Build(profile.WireUserId, profile.Progress),
            // JP 0x2A/0x2B are {code,value} collection caches. Never serialize
            // the legacy u32 DefaultItems/EventItems lists into them: item 1025 is
            // little-endian code 0x401, which the collection UI renders as a disc.
            OnUpdateUserInventoryDefaultItemInfPacket.BuildKorean(
                profile.WireUserId, profile.Collection),
            OnUpdateUserInventoryEventItemInfPacket.BuildKorean(
                profile.WireUserId, []),
            OnUpdateUserInventoryShopItemInfPacket.Build(
                profile.WireUserId, inventory.ShopItems),
            OnUpdateUserInventoryPresentItemInfPacket.Build(
                profile.WireUserId, inventory.PresentItems),
            OnUpdateUserInventoryMountItemInfPacket.Build(
                profile.WireUserId, inventory.MountItems)
        });
        return packets;
    }
}

