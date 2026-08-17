using Arrowgene.DJMaxOnline.Server.Korea400.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Korea400.Packets;

public static class OnAliveReqPacket
{
    public static Packet Build(byte control = ProtocolPadding.Unused) =>
        new(PacketMeta.OnAliveReq, [control]);
}

public sealed record DisconnectPeerResponse(short Reason);

/// <summary>
/// Values forwarded directly to the client's DisconnectionDirector. The retail client
/// does not assign a dedicated reason to scheduled maintenance: zero follows its generic
/// connection-loss path, while sparse non-zero values select account, version, timeout or
/// anti-cheat errors. DISCONNECTMSG1 is selected by the transport's disconnected state,
/// not by reason 1.
/// </summary>
public enum DisconnectPeerReason : short
{
    Scheduled = 0
}

public static class OnDisconnectPeerInfPacket
{
    public static Packet Build(
        short reason = (short)DisconnectPeerReason.Scheduled,
        byte control = ProtocolPadding.Unused) =>
        // Korean OnDisconnectPeerInf is 5 wire bytes: reason i16@3, no reserved tail.
        DjMaxPacketBuilder.Fixed(PacketMeta.OnDisconnectPeerInf, control)
            .WriteInt16(reason)
            .Build();

    public static DisconnectPeerResponse Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        DisconnectPeerResponse response = new(reader.ReadInt16());
        reader.EnsureComplete();
        return response;
    }
}

public static class OnUserInfoResNotFoundPacket
{
    // Korean OnUserInfoResNotFound is a bare 3-byte signal; China had an 8-byte tail.
    public static Packet Build(byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnUserInfoResNotFound, control).Build();
}

/// <summary>
/// One ten-byte peer row consumed by sub_434D50. The first two words form the
/// peer key; a zero fourth word removes the peer from the client's table.
/// </summary>
public sealed record PeerCountEntry(
    ushort KeyHigh,
    ushort KeyLow,
    ushort Value,
    ushort State,
    ushort Flags);

public static class OnPeerCountInfPacket
{
    public const int EntrySize = 10;

    public static Packet Build(
        IReadOnlyList<PeerCountEntry> entries,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(entries);
        int wireSize = checked(7 + entries.Count * EntrySize);
        DjMaxPacketBuilder builder =
            DjMaxPacketBuilder.Dynamic(PacketMeta.OnPeerCountInf, wireSize, control);
        foreach (PeerCountEntry entry in entries)
        {
            builder
                .WriteUInt16(entry.KeyHigh)
                .WriteUInt16(entry.KeyLow)
                .WriteUInt16(entry.Value)
                .WriteUInt16(entry.State)
                .WriteUInt16(entry.Flags);
        }
        return builder.Build();
    }

    public static IReadOnlyList<PeerCountEntry> Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint declaredWireSize = reader.ReadUInt32();
        int actualWireSize = 7 + packet.Data.Length;
        if (declaredWireSize != actualWireSize ||
            reader.Remaining % EntrySize != 0)
        {
            throw new InvalidDataException(
                $"Invalid peer-count framing: declared {declaredWireSize}, " +
                $"actual {actualWireSize}, body {reader.Remaining}.");
        }

        List<PeerCountEntry> entries = new(reader.Remaining / EntrySize);
        while (reader.Remaining != 0)
        {
            entries.Add(new PeerCountEntry(
                reader.ReadUInt16(),
                reader.ReadUInt16(),
                reader.ReadUInt16(),
                reader.ReadUInt16(),
                reader.ReadUInt16()));
        }
        return entries;
    }
}

public enum QuickInviteResult : byte
{
    // sub_458334 subtracts 0x86 from wire+3 and selects QUICKINVITEMSG1..4.
    // These are consecutive CLIENT message selectors, not the 0x88-based values that
    // were previously guessed. Sending 0x88 for success displays MSG3: "You cannot send
    // an invitation" even though the target has already received one.
    Sent = 0x86,
    NoUsersAvailable = 0x87,
    UnableToInvite = 0x88,
    MaximumInvitationsExceeded = 0x89
}

public static class QuickInviteReqPacket
{
    // A bare 3-byte signal: id + control and nothing else, so there is no body to read
    // and the packet is too short for the structured reader's header.
    public static void Parse(Packet packet) => ArgumentNullException.ThrowIfNull(packet);
}

/// <summary>
/// The invitation a player receives (0xA2), 16 bytes. Layout proven from its handler
/// <c>sub_44419D</c>:
/// <code>
///   wire+3   char[11]  inviter's NAME  (sub_5019B0(this+10194, a2+3, 11))
///   wire+14  u16       ROOM INDEX      (*(this+10193) = *(__int16 *)(a2+14))
/// </code>
/// The client then resolves that index against its OWN lobby grid (sub_433810) to build
/// the popup - so the room must already be in the invitee's grid, or they get LOBBYMSG20
/// ("the stage no longer exists") instead of an invitation.
/// </summary>
public static class OnInviteReqPacket
{
    public const int NameSize = 11;

    public static Packet Build(
        string inviterNickname,
        ushort roomIndex,
        byte control = ProtocolPadding.Unused)
    {
        // The field is fixed, so a longer nickname is TRUNCATED rather than rejected -
        // an invitation must not fail because of the inviter's name length.
        string name = inviterNickname ?? string.Empty;
        if (name.Length > NameSize - 1)
        {
            name = name[..(NameSize - 1)];
        }
        return DjMaxPacketBuilder.Fixed(PacketMeta.OnInviteReq, control)
            .WriteFixedAscii(name, NameSize)
            .WriteUInt16(roomIndex)
            .Build();
    }

    public static (string Inviter, ushort RoomIndex) Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        string inviter = reader.ReadFixedAscii(NameSize);
        ushort roomIndex = reader.ReadUInt16();
        reader.EnsureComplete();
        return (inviter, roomIndex);
    }
}

public static class OnQuickInviteAckPacket
{
    public static Packet Build(
        QuickInviteResult result,
        byte control = ProtocolPadding.Unused) =>
        // Korean OnQuickInviteAck is 4 wire bytes: result byte@3, no reserved tail.
        DjMaxPacketBuilder.Fixed(PacketMeta.OnQuickInviteAck, control)
            .WriteByte((byte)result)
            .Build();
}

public static class ItemFailurePackets
{
    public static Packet BuildGetItemFail(byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnGetItemFail, control).Build();

    public static Packet BuildItemLevelUpFail(
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnItemLevelUpFail, control).Build();

    public static Packet BuildUseItemFail(byte control = ProtocolPadding.Unused) =>
        new(PacketMeta.OnUseItemFail, [control]);
}

/// <summary>
/// A use-item request. The Korean client identifies the item by its item-box slot only:
/// sub_436510 sends four wire bytes with a single byte at +3. China's {itemId, value}
/// pair does not exist here, so the server resolves the slot against its own box.
/// </summary>
public sealed record UseItemRequest(byte Slot);

public static class UseItemReqPacket
{
    public static Packet Build(
        byte slot,
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.UseItemReq, control)
            .WriteByte(slot)
            .Build();

    public static UseItemRequest Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        UseItemRequest request = new(reader.ReadByte());
        reader.EnsureComplete();
        return request;
    }
}

public static class OnCrItemInfPacket
{
    /// <summary>
    /// The Korean client treats this as an opaque room-wide signal and reads no body.
    /// It marks a suitable future chart note as an item note for each active player.
    /// </summary>
    // Korean OnCrItemInf is a bare 3-byte signal (id + control); China carried an
    // 8-byte reserved body that no longer fits.
    public static Packet Build(byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnCrItemInf, control).Build();

    public static void Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        reader.EnsureComplete();
    }
}

public static class OnMissionStandItemInfPacket
{
    // Korean OnMissionStandItemInf (0xC0) is 14 wire bytes = 11-byte body. The client
    // handler sub_436640 reads NONE of the body — it just clears pending state and
    // forwards the packet as a post-result completion signal — so the bytes are opaque.
    // (China's 19-byte capture overflowed the 14-byte packet and threw inside FinishPlay,
    // aborting it before OnPlayOverInf and freezing the client on the result screen.)
    public const int PayloadSize = 11;

    private static readonly byte[] CapturedPayload = Convert.FromHexString(
        "050000581B050000000000");

    public static Packet BuildCaptured(byte control = ProtocolPadding.Unused) =>
        Build(CapturedPayload, control);

    public static Packet Build(
        ReadOnlySpan<byte> payload,
        byte control = ProtocolPadding.Unused)
    {
        if (payload.Length != PayloadSize)
        {
            throw new ArgumentException(
                $"Mission-stand payload must be exactly {PayloadSize} bytes.");
        }
        return DjMaxPacketBuilder.Fixed(PacketMeta.OnMissionStandItemInf, control)
            .WriteBytes(payload)
            .Build();
    }

    public static byte[] Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        byte[] payload = reader.ReadBytes(PayloadSize);
        reader.EnsureComplete();
        return payload;
    }

    public static byte[] CapturedPayloadCopy() => CapturedPayload.ToArray();
}

public static class OnAlertCreditInfPacket
{
    public static Packet Build(
        uint status,
        byte control = ProtocolPadding.Unused) =>
        // Korean OnAlertCreditInf is 7 wire bytes: status u32@3, no reserved tail.
        DjMaxPacketBuilder.Fixed(PacketMeta.OnAlertCreditInf, control)
            .WriteUInt32(status)
            .Build();

    public static uint Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint status = reader.ReadUInt32();
        reader.EnsureComplete();
        return status;
    }
}

public static class UpdateUserIconReqPacket
{
    // Korean 0x23 is 7 bytes: control + iconId u32 (no China reserved tail).
    public static Packet Build(
        uint iconId,
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.sub_437900, control)
            .WriteUInt32(LocalPlayerProfile.EncodeIconId(iconId))
            .Build();

    public static uint Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint iconId = unchecked(reader.ReadUInt32() - 1);
        reader.EnsureComplete();
        return iconId;
    }
}
