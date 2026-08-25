using Arrowgene.DJMaxOnline.Server.China260.Protocol;

namespace Arrowgene.DJMaxOnline.Server.China260.Packets;

public enum AuthenticationResult : ushort
{
    Rejected = 0,
    // China 2.60 sub_431EB0 gates the server-list transition on this WORD == 22 (0x16).
    ChannelAccepted = 0x16
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
    // China 2.60 stores a fixed-width secondary account identifier between its account
    // id and nickname. Omitting it turns this into a 71-byte Japan packet and makes the
    // client begin its next frame 21 bytes too early.
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

    // China 2.60: userId@3, accountClass@7, level@11, result WORD@15, 4 reserved@17,
    // accountId[25]@21, secondaryId[21]@46, nickname[25]@67 = 92 wire bytes.
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
            // The China capture has four literal zeroes here.  These are not generic
            // padding bytes: using the usual 0xCC pattern breaks byte-exact replay.
            .WritePadding(ReservedAfterResultSize, 0)
            .WriteFixedAscii(identity.AccountId, AccountIdSize)
            .WriteFixedAscii(identity.SecondaryId, SecondaryIdSize)
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
        string secondaryId = reader.ReadFixedAscii(SecondaryIdSize);
        string nickname = reader.ReadFixedAscii(NicknameSize);
        reader.EnsureComplete();
        return (new AuthenticationIdentity(
            userId, accountClass, level, accountId, secondaryId, nickname), result);
    }
}
