using Arrowgene.DJMaxOnline.Server.Japan400.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Japan400.Packets;

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

    /// <summary>
    /// Four bytes the client never reads, between the result and the strings.
    ///
    /// Its handler sub_4317F0 takes userId@3, accountClass@7, level@11, result@15 and then
    /// the two 25-byte strings at @21 and @46 - so something occupies 17..20. Korea has no
    /// such gap (its strings sit at 17 and 42, making 67); leaving it out here shifts both
    /// strings four bytes early and the client reads them from the middle of the wrong
    /// field.
    /// </summary>
    private const int ReservedAfterResultSize = 4;

    // JP layout (client sub_4317F0): userId@3, accountClass@7, level@11, result WORD@15,
    // 4 reserved@17, accountId[25]@21, nickname[25]@46 = 71 wire bytes.
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
            .WritePadding(ReservedAfterResultSize, ProtocolPadding.Unused)
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
        reader.Skip(ReservedAfterResultSize);
        string accountId = reader.ReadFixedAscii(AccountIdSize);
        string nickname = reader.ReadFixedAscii(NicknameSize);
        reader.EnsureComplete();
        return (new AuthenticationIdentity(
            userId, accountClass, level, accountId, string.Empty, nickname), result);
    }
}
