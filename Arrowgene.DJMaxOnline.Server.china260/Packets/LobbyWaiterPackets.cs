using Arrowgene.DJMaxOnline.Server.China260.Protocol;

namespace Arrowgene.DJMaxOnline.Server.China260.Packets;

/// <summary>
/// The 73-byte lobby waiter record consumed by sub_435090/sub_434E50. The icon
/// is encoded plus one because the client decrements it before storing it.
/// </summary>
/// <param name="MapKey">
/// record+71, and the ONLY thing the waiter list is keyed on:
/// <c>sub_4333F0</c> reads <c>*(WORD *)(record + 71)</c>, looks that up, and either
/// inserts a new row or overwrites the existing one in place - and <c>sub_4334A0</c>
/// (the erase) removes by the same 16-bit value.
///
/// Two ways to get this wrong, both seen:
/// sending 0 filed EVERY player under key 0, so rows clobbered each other and no erase
/// matched; sending the randomised session id made every reconnect insert ANOTHER row
/// while the erase only named the newest, so the same player stacked up duplicates.
/// It must be STABLE per account and equal to whatever the erase will name.
/// </param>
public sealed record LobbyWaiterInfo(
    uint UserId,
    string AccountId,
    string Nickname,
    byte Gender,
    uint IconWireValue,
    uint ActivityValue,
    uint Level,
    uint AccountClass,
    ushort MapKey)
{
    public static LobbyWaiterInfo CreateLocal(LocalPlayerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        return new LobbyWaiterInfo(
            // record+0 IS THE ROW'S IDENTITY, and it must be STABLE and fit a u16.
            // sub_44342B opens with `if (sub_49EF30(*record) != 0)` - an existing row is
            // updated in place, anything else CREATES A NEW ROW - and the erase
            // (sub_4334A0) only carries 16 bits. Putting the randomised session id here
            // meant every login built another row that no erase could ever name, which is
            // how the same player stacked up in the list.
            profile.WaiterKey,
            profile.AccountId,
            profile.Nickname,
            profile.Gender,
            profile.IconWireValue(),
            profile.Progress.Experience,
            profile.Progress.Level,
            profile.AccountClass,
            // STABLE per account, never the session id - a key that changes each login
            // inserts a duplicate row instead of overwriting the existing one.
            MapKey: profile.WaiterKey);
    }
}

public static class OnWaiterInfoUpdateInfPacket
{
    public const int AccountIdSize = 25;
    public const int NicknameSize = 25;

    public static Packet Build(
        LobbyWaiterInfo waiter,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(waiter);
        // Korean waiter record (73 bytes), fully mapped by the user-list row render
        // sub_44342B (draws WaiterGender/WaiterIcon/Lavel_Label per row):
        //   +0 userId, +4 accountId[25], +29 nickname[25],
        //   +54 gender byte (==1 male, else female),
        //   +55 icon (client decrements; passed to sub_461DC5),
        //   +59 experience, +63 level (both also set by sub_435AE0),
        //   +67 accountClass (icon frame + admin check sub_42857C), +71 peer key.
        return DjMaxPacketBuilder.Fixed(PacketMeta.OnWaiterInfoUpdateInf, control)
            .WriteUInt32(waiter.UserId)              // record+0
            .WriteFixedAscii(waiter.AccountId, AccountIdSize)   // record+4
            .WriteFixedAscii(waiter.Nickname, NicknameSize)     // record+29 (25 bytes)
            .WriteByte(waiter.Gender)                // record+54 gender
            .WriteUInt32(waiter.IconWireValue)       // record+55 icon (client decrements)
            .WriteUInt32(waiter.ActivityValue)       // record+59 experience
            .WriteUInt32(waiter.Level)               // record+63 level
            .WriteUInt32(waiter.AccountClass)        // record+67 accountClass
            // record+71 is the MAP KEY, not a spare: sub_4333F0 reads
            // *(WORD *)(record + 71), looks it up and inserts or overwrites in place, and
            // sub_4334A0 erases by the same value. Hardcoding 0 filed every player under
            // key 0, so rows clobbered each other and no erase ever matched - a player who
            // left the channel stayed in the list forever.
            .WriteUInt16(waiter.MapKey)
            .Build();
    }

    public static LobbyWaiterInfo Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        LobbyWaiterInfo waiter = new(
            reader.ReadUInt32(),                 // record+0 userId
            reader.ReadFixedAscii(AccountIdSize),// record+4 accountId
            reader.ReadFixedAscii(NicknameSize), // record+29 nickname
            reader.ReadByte(),                   // record+54 gender
            reader.ReadUInt32(),                 // record+55 icon
            reader.ReadUInt32(),                 // record+59 experience
            reader.ReadUInt32(),                 // record+63 level
            reader.ReadUInt32(),                 // record+67 accountClass
            reader.ReadUInt16());                // record+71 peer key
        reader.EnsureComplete();
        return waiter;
    }
}

/// <summary>
/// Korean OnWaiterInfoEraseInf (0x3D), 9 bytes, and it removes the row TWICE - once from
/// the net-level record map and once from the visible list - using TWO SEPARATE FIELDS:
/// <code>
///   wire+3  u16  net map key   -> sub_433660 -> sub_4334A0, matched against record+71
///   wire+5  u32  ROW USER ID   -> sub_441657 -> sub_4A03F0, matched against record+0
/// </code>
/// The builder used to write the id at wire+3 and leave wire+5 as PADDING, so the visible
/// row was erased with id 0 and never went away - the player stayed in the user list
/// forever even though the underlying record had been dropped. Both fields must name the
/// same player.
/// </summary>
public static class OnWaiterInfoEraseInfPacket
{
    public static Packet Build(
        ushort userId,
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnWaiterInfoEraseInf, control)
            .WriteUInt16(userId)   // wire+3, the net map key (record+71)
            .WriteUInt32(userId)   // wire+5, the visible row's id (record+0)
            .Build();

    public static ushort Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        ushort userId = reader.ReadUInt16();
        uint rowUserId = reader.ReadUInt32();
        reader.EnsureComplete();
        if (rowUserId != userId)
        {
            throw new InvalidDataException(
                $"Waiter erase names two different players: map key {userId}, " +
                $"row id {rowUserId}.");
        }
        return userId;
    }
}
