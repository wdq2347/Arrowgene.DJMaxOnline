using Arrowgene.DJMaxOnline.Server.Protocol;
using System.Text;

namespace Arrowgene.DJMaxOnline.Server.Packets;

/// <summary>
/// The 67-byte user record the client caches per user id and the messenger renders from.
/// Field meanings were established by sweeping each one live against the client's display:
///   +0  u32      userId
///   +4  char[25] accountId
///   +29 char[23] nickname
///   +52 u32      UNIDENTIFIED - the last unknown field in this record
///   +56 u16      account tier: 0 normal, 1 premium, 8 PC-bang, 9 PC-bang + premium
///   +58 u32      level
///   +62 u8       gender: 1 = male, 0 = female
///   +63 u32      icon, sent one-based (sub_4339F0 decrements record+63 on receipt)
/// </summary>
public sealed record LobbyUserIdentity(
    uint UserId,
    string AccountId,
    string Nickname,
    uint Unknown52,
    ushort AccountTier,
    uint Level,
    byte Gender,
    uint IconWireValue,
    byte AccountPadding = ProtocolPadding.Unused,
    byte NicknamePadding = ProtocolPadding.Unused)
{
    /// <summary>Account-tier values the client understands at record+56.</summary>
    public const ushort TierNormal = 0;
    public const ushort TierPremium = 1;
    public const ushort TierPcBang = 8;
    public const ushort TierPcBangPremium = 9;

    public static LobbyUserIdentity CreateLocal(LocalPlayerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        return new LobbyUserIdentity(
            // Session id, never the account key - see LocalPlayerProfile.WireUserId.
            profile.WireUserId,
            profile.AccountId,
            profile.Nickname,
            profile.ProfileFlags,
            TierFor(profile.AccountClass),
            profile.Progress.Level,
            profile.Gender,
            profile.IconWireValue());
    }

    /// <summary>
    /// Derives record+56 from the account class. The tier is a small enumeration, not a
    /// bit field: PC-bang is 8 and premium adds 1, giving the four values the client draws.
    /// </summary>
    public static ushort TierFor(uint accountClass)
    {
        ushort tier = (accountClass & (uint)AccountClassFlags.PcBang) != 0
            ? TierPcBang
            : TierNormal;
        if (AccountClassInfo.IsPremium(accountClass))
        {
            tier += TierPremium;
        }
        return tier;
    }
}

public static class OnUserIdInfoInfPacket
{
    public const int EntrySize = 67;
    public const int AccountIdSize = 25;
    public const int NicknameSize = 23;

    public static Packet Build(
        IReadOnlyList<LobbyUserIdentity> users,
        byte control = ProtocolPadding.Unused) =>
        Build(PacketMeta.OnUserIdInfoInf, users, control);

    internal static Packet Build(
        PacketMeta meta,
        IReadOnlyList<LobbyUserIdentity> users,
        byte control)
    {
        ArgumentNullException.ThrowIfNull(users);
        int wireSize = checked(
            DjMaxPacketBuilder.PacketIdSize + DjMaxPacketBuilder.HeaderSize +
            users.Count * EntrySize);
        DjMaxPacketBuilder builder = DjMaxPacketBuilder.Dynamic(meta, wireSize, control);

        foreach (LobbyUserIdentity user in users)
        {
            builder
                .WriteUInt32(user.UserId)
                .WriteFixedAscii(user.AccountId, AccountIdSize, user.AccountPadding)
                .WriteFixedAscii(user.Nickname, NicknameSize, user.NicknamePadding)
                .WriteUInt32(user.Unknown52)   // record+52 (unidentified)
                .WriteUInt16(user.AccountTier) // record+56 tier
                .WriteUInt32(user.Level)       // record+58
                .WriteByte(user.Gender)        // record+62 (1=male)
                .WriteUInt32(user.IconWireValue); // record+63, one-based
        }

        return builder.Build();
    }

    public static IReadOnlyList<LobbyUserIdentity> Parse(Packet packet) =>
        ParseEntries(packet);

    internal static IReadOnlyList<LobbyUserIdentity> ParseEntries(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint declaredWireSize = reader.ReadUInt32();
        int actualWireSize = DjMaxPacketBuilder.PacketIdSize +
                             DjMaxPacketBuilder.HeaderSize + packet.Data.Length;
        if (declaredWireSize != actualWireSize || reader.Remaining % EntrySize != 0)
        {
            throw new InvalidDataException("Invalid lobby-user-list framing.");
        }

        List<LobbyUserIdentity> users = new(reader.Remaining / EntrySize);
        while (reader.Remaining != 0)
        {
            uint userId = reader.ReadUInt32();
            (string accountId, byte accountPadding) = ReadFixedAscii(
                reader, AccountIdSize);
            (string nickname, byte nicknamePadding) = ReadFixedAscii(
                reader, NicknameSize);
            users.Add(new LobbyUserIdentity(
                userId,
                accountId,
                nickname,
                reader.ReadUInt32(),
                reader.ReadUInt16(),
                reader.ReadUInt32(),
                reader.ReadByte(),
                reader.ReadUInt32(),
                accountPadding,
                nicknamePadding));
        }

        return users;
    }

    private static (string Value, byte Padding) ReadFixedAscii(
        DjMaxPacketReader reader,
        int fieldSize)
    {
        byte[] field = reader.ReadBytes(fieldSize);
        int terminator = Array.IndexOf(field, (byte)0);
        int length = terminator >= 0 ? terminator : field.Length;
        byte padding = terminator >= 0 && terminator + 1 < field.Length
            ? field[terminator + 1]
            : (byte)0;
        return (Encoding.ASCII.GetString(field, 0, length), padding);
    }
}

public static class OnUserIdInfoAckPacket
{
    public static Packet Build(
        IReadOnlyList<LobbyUserIdentity> users,
        byte control = ProtocolPadding.Unused) =>
        OnUserIdInfoInfPacket.Build(PacketMeta.OnUserIdInfoAck, users, control);

    public static IReadOnlyList<LobbyUserIdentity> Parse(Packet packet) =>
        OnUserIdInfoInfPacket.ParseEntries(packet);
}

public static class UserIdInfoReqPacket
{
    public static IReadOnlyList<uint> Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint declaredWireSize = reader.ReadUInt32();
        int actualWireSize = DjMaxPacketBuilder.PacketIdSize +
                             DjMaxPacketBuilder.HeaderSize + packet.Data.Length;
        if (declaredWireSize != actualWireSize || reader.Remaining % sizeof(uint) != 0)
        {
            throw new InvalidDataException("Invalid user-identity request framing.");
        }

        uint[] userIds = new uint[reader.Remaining / sizeof(uint)];
        for (int i = 0; i < userIds.Length; i++)
        {
            userIds[i] = reader.ReadUInt32();
        }
        return userIds;
    }
}

