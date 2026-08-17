using Arrowgene.DJMaxOnline.Server.Korea400.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Korea400.Packets;

/// <summary>
/// The 135-byte local-user snapshot copied by client handler sub_432900.
/// Level, experience, money, and record fields are established by the retail
/// property-update handlers. The final ten MISC values remain deliberately
/// numbered because their gameplay labels are not established by the client.
/// </summary>
public sealed record UserInfoSnapshot(
    uint UserId,
    string AccountId,
    string Nickname,
    byte State,
    ushort ProfileCode,
    uint IconWireValue,
    uint ProfileFlags,
    uint Level,
    uint Experience,
    uint Money,
    uint Wins,
    uint Losses,
    uint Draws,
    uint Misc01,
    uint Misc02,
    uint Misc03,
    uint Misc04,
    uint Misc05,
    uint Misc06,
    uint Misc07,
    uint Misc08,
    uint Misc09,
    uint Misc10,
    uint AccountClass,
    ushort Reserved)
{
    public static UserInfoSnapshot CreateLocal(LocalPlayerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        LocalPlayerProgress progress = profile.Progress;
        uint[] misc = progress.MiscStatistics;
        return new UserInfoSnapshot(
            profile.WireUserId,
            profile.AccountId,
            profile.Nickname,
            State: profile.State,
            ProfileCode: profile.ProfileCode,
            IconWireValue: profile.IconWireValue(),
            ProfileFlags: profile.ProfileFlags,
            Level: progress.Level,
            Experience: progress.Experience,
            Money: progress.Money,
            Wins: progress.Wins,
            Losses: progress.Losses,
            Draws: progress.Draws,
            Misc01: misc[0],
            Misc02: misc[1],
            Misc03: misc[2],
            Misc04: misc[3],
            Misc05: misc[4],
            Misc06: misc[5],
            Misc07: misc[6],
            Misc08: misc[7],
            Misc09: misc[8],
            Misc10: misc[9],
            AccountClass: profile.AccountClass,
            Reserved: profile.Reserved);
    }
}

public static class OnUserInfoInfPacket
{
    public const int AccountIdSize = 25;
    public const int NicknameSize = 25;

    public static Packet Build(
        UserInfoSnapshot user,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(user);
        return DjMaxPacketBuilder.Fixed(PacketMeta.OnUserInfoInf, control)
            .WriteUInt32(user.UserId)
            .WriteFixedAscii(user.AccountId, AccountIdSize)
            .WriteFixedAscii(user.Nickname, NicknameSize)
            .WriteByte(user.State)
            .WriteUInt16(user.ProfileCode)
            .WriteUInt32(user.IconWireValue)
            .WriteUInt32(user.ProfileFlags)
            .WriteUInt32(user.Level)
            .WriteUInt32(user.Experience)
            .WriteUInt32(user.Money)
            .WriteUInt32(user.Wins)
            .WriteUInt32(user.Losses)
            .WriteUInt32(user.Draws)
            .WriteUInt32(user.Misc01)
            .WriteUInt32(user.Misc02)
            .WriteUInt32(user.Misc03)
            .WriteUInt32(user.Misc04)
            .WriteUInt32(user.Misc05)
            .WriteUInt32(user.Misc06)
            .WriteUInt32(user.Misc07)
            .WriteUInt32(user.Misc08)
            .WriteUInt32(user.Misc09)
            .WriteUInt32(user.Misc10)
            .WriteUInt32(user.AccountClass)
            .WriteUInt16(user.Reserved)
            .Build();
    }

    public static UserInfoSnapshot Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        UserInfoSnapshot user = new(
            reader.ReadUInt32(),
            reader.ReadFixedAscii(AccountIdSize),
            reader.ReadFixedAscii(NicknameSize),
            reader.ReadByte(),
            reader.ReadUInt16(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt16());
        reader.EnsureComplete();
        return user;
    }
}

