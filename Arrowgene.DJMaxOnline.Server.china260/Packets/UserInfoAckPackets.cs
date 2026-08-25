using Arrowgene.DJMaxOnline.Server.China260.Protocol;

namespace Arrowgene.DJMaxOnline.Server.China260.Packets;

/// <summary>Reads the target user id from a profile-view request (0x1D).</summary>
public static class UserInfoReqPacket
{
    public static uint Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint userId = reader.ReadUInt32();
        // The remaining bytes (a name/hash the retail server matched against its
        // account table) are unused on this single-profile server.
        return userId;
    }
}

/// <summary>
/// The 892-byte profile response (0x1F). Layout PROVEN from the client:
/// <c>sub_452A64</c> (dispatcher case 31) calls
/// <c>sub_463D0C(raw+3, raw+138, raw+144)</c> - "CCommonUI::ShowUserInfo" - so the packet
/// is three regions, and the first is the SAME 135-byte user block OnLogInAck carries:
/// <code>
///   raw+3   block[135]   the user record
///   raw+138 status[6]    three words: connection state (see ConnectionState)
///   raw+144 tables[744]  inventory / collection, 0xFF = empty slot
/// </code>
/// Field offsets inside the block, each read directly by sub_463D0C:
/// <code>
///   +29  NAME (drawn as 아이디 AND under the portrait, via sub_462460)
///   +54  gender byte          +57  icon  -> sub_461DC5(icon, accountClass)
///   +65  level               +69  experience
///   +73  money/MAX           +77, +85, +97  counters
///   +101 accuracy x100 (signed) +105 u16 clear-rate x100
///   +113 play count          +129 accountClass (sub_42857C = the admin test)
/// </code>
/// The old layout put a userId at +0 and the name at +4, so the panel drew whatever
/// happened to sit at +29 - the account id - as the player's name, and every stat came
/// from a hardcoded capture, identical for everyone.
/// </summary>
public static class OnUserInfoAckPacket
{
    public const int BlockSize = 135;
    private const int NameOffset = 29;
    private const int NameSize = 25;
    private const int StatusSize = 6;
    private const int TableSize = 744;
    private const int TrailingSize = 4;
    private const byte NoRecord = 0xFF;

    /// <summary>
    /// The three status words at raw+138. sub_463D0C shows "offline" unless word0 and
    /// word1 are both non-zero, and only treats the player as being in THIS channel when
    /// they match net+894413/894415 - which a remote player's client cannot know - so a
    /// non-matching pair is the honest "online, elsewhere" answer.
    /// </summary>
    private const ushort OnlineElsewhere = 1;

    private const int CollectionSlots = 48;
    private const int DiscSlots = 32;
    private const int ItemSlots = 30;
    private const int MissionSlots = 10;
    private const int MountSlots = 8;

    public static Packet Build(
        uint userId,
        string nickname,
        LocalPlayerProfile? profile = null,
        IReadOnlyList<CollectionEntry>? collection = null,
        byte control = ProtocolPadding.Unused)
    {
        LocalPlayerProgress progress = profile?.Progress ?? new LocalPlayerProgress();
        collection ??= [];

        // The block is written FIELD FOR FIELD the same way OnLogInAck writes it - it is
        // the same 135-byte record - so the inspected player's panel shows the same
        // numbers their own 내정보 does. Writing only a handful of fields left every
        // untouched stat at zero: no wins/losses, no scores, 0.00% accuracy, no discs.
        DjMaxPacketBuilder builder = DjMaxPacketBuilder.Fixed(PacketMeta.OnUserInfoAck, control)
            .WriteUInt32(userId)                          // block+0
            // NEVER the account id: that is the player's login name, and this packet goes
            // to whoever clicked them. The panel reads the name from +29 only.
            .WriteFixedAscii(nickname, NameSize)          // block+4
            .WriteFixedAscii(nickname, NameSize)          // block+29 NAME (아이디)
            .WriteByte(profile?.Gender ?? 0)              // block+54
            .WritePadding(57 - 55)
            .WriteUInt32(profile?.IconWireValue() ?? 0)   // block+57
            .WritePadding(65 - 61)
            .WriteUInt32(progress.Level)                  // block+65
            .WriteUInt32(progress.Experience)             // block+69
            .WriteUInt32(progress.Money)                  // block+73  맥스
            .WriteUInt32(progress.Wins)                   // block+77  WIN
            .WriteUInt32(0)                               // block+81
            .WriteUInt32(progress.Losses)                 // block+85  LOSE
            .WriteUInt32Array(UserStatisticsBlock.Build(progress))  // block+89..128
            .WriteUInt32(profile?.AccountClass ?? 0)      // block+129
            .WritePadding(BlockSize - 133);

        builder
            .WriteUInt16(OnlineElsewhere)
            .WriteUInt16(OnlineElsewhere)
            .WriteUInt16(0);

        // The tables at raw+144, same shapes the login block uses. 0xFFFF is the empty
        // sentinel every client loop tests for.
        for (int i = 0; i < CollectionSlots; i++)
        {
            if (i < collection.Count)
            {
                builder.WriteUInt16(collection[i].Code).WriteUInt16(collection[i].Value);
            }
            else
            {
                builder.WriteUInt16(0xFFFF).WriteUInt16(0);
            }
        }
        for (int i = 0; i < DiscSlots; i++)
        {
            builder.WriteUInt16(0xFFFF).WriteUInt16(0);
        }
        for (int i = 0; i < ItemSlots; i++)
        {
            builder.WriteUInt16(0xFFFF).WriteUInt16(0).WriteUInt32(0);
        }
        for (int i = 0; i < MissionSlots; i++)
        {
            builder.WriteUInt16(0xFFFF).WriteUInt16(0).WriteUInt32(0).WriteUInt32(0);
        }
        for (int i = 0; i < MountSlots; i++)
        {
            builder.WriteUInt16(0xFFFF).WriteUInt16(0).WriteUInt32(0);
        }

        return builder.WritePadding(TrailingSize, ProtocolPadding.Unused).Build();
    }
}
