using Arrowgene.DJMaxOnline.Server.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Packets;

public enum AuthenticationResult : ushort
{
    Rejected = 0,
    // Korean client sub_431020 gates authentication on this WORD == 20 (0x14).
    ChannelAccepted = 0x14
}

public sealed record AuthenticationIdentity(
    uint UserId,
    uint AccountClass,
    uint Level,
    string AccountId,
    string SecondaryId,
    string Nickname);

public static class OnAuthenticateInAckPacket
{
    public const int AccountIdSize = 25;
    public const int NicknameSize = 25;
    // Retained for LocalPlayerProfile validation; the Korean wire layout omits
    // the SecondaryId field the China 92-byte OnAuthenticateInAck carried.
    public const int SecondaryIdSize = 21;

    // Korean layout (client sub_431020): userId@3, accountClass@7, level@11,
    // result WORD@15, accountId[25]@17, nickname[25]@42 = 67 wire bytes. No
    // reserved padding and no SecondaryId field (China had both).
    public static Packet Build(
        AuthenticationIdentity identity,
        AuthenticationResult result = AuthenticationResult.ChannelAccepted,
        byte control = ProtocolPadding.Unused)
    {
        return DjMaxPacketBuilder.Fixed(PacketMeta.OnAuthenticateInAck, control)
            .WriteUInt32(identity.UserId)
            .WriteUInt32(identity.AccountClass)
            .WriteUInt32(identity.Level)
            .WriteUInt16((ushort)result)
            .WriteFixedAscii(identity.AccountId, AccountIdSize)
            .WriteFixedAscii(identity.Nickname, NicknameSize)
            .Build();
    }

    public static (AuthenticationIdentity Identity, AuthenticationResult Result) Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint userId = reader.ReadUInt32();
        uint accountClass = reader.ReadUInt32();
        uint level = reader.ReadUInt32();
        AuthenticationResult result = (AuthenticationResult)reader.ReadUInt16();
        string accountId = reader.ReadFixedAscii(AccountIdSize);
        string nickname = reader.ReadFixedAscii(NicknameSize);
        reader.EnsureComplete();
        return (new AuthenticationIdentity(
            userId, accountClass, level, accountId, string.Empty, nickname), result);
    }
}
