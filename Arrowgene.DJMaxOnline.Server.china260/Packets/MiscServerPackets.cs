using Arrowgene.DJMaxOnline.Server.China260.Protocol;

namespace Arrowgene.DJMaxOnline.Server.China260.Packets;

// The seven server->client packets the client's dispatch switch (sub_42FB20) routes but the
// server never sent. Each builder is documented against its client handler, because several
// of these handlers do little or nothing - the value of implementing them is a byte-exact,
// non-desyncing packet plus a clear record of exactly what it can and cannot make happen.

/// <summary>
/// Room slot open/close broadcast (0x55). sub_434650 clears net+895056 and forwards the
/// packet to the room scene; it reads no field of its own, so the effect lives entirely in
/// the room scene's forwarded handler. Outside a room this does nothing visible.
/// </summary>
public static class OnSlotControlInfPacket
{
    public static Packet Build(byte slot, byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnSlotControlInf, control)
            .WriteByte(slot)
            .Build();
}

/// <summary>
/// Room invitation (0xA2), fixed 16 - so a 14-byte body after the id and header.
/// sub_434420 is a pure forwarder to the active scene: it reads NO field of its own, so the
/// exact contents are up to whichever room scene consumes it. The two ids plus a padding
/// tail fill the field; only the size is load-bearing for the stream.
/// </summary>
public static class OnInviteInfPacket
{
    // Structured content is written from wire offset 3, so a fixed packet carries
    // size - 3 bytes there, not size - id - header.
    public const int ContentSize = 16 - DjMaxPacketBuilder.StructuredPayloadOffset;

    public static Packet Build(
        uint fromUserId,
        uint roomId,
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnInviteReq, control)
            .WriteUInt32(fromUserId)
            .WriteUInt32(roomId)
            .WritePadding(ContentSize - 2 * sizeof(uint))
            .Build();
}

/// <summary>
/// Pre-match "good luck" ping (0xBD), fixed 7. The client handler sub_436620 is EMPTY - it
/// reads nothing and does nothing - so this is provably a no-op on the client. It exists
/// only so the id can be produced byte-exact without desyncing the stream.
/// </summary>
public static class OnGoodLuckInfPacket
{
    public static Packet Build(uint userId, byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnGoodLuckInf, control)
            .WriteUInt32(userId)
            .Build();
}

/// <summary>
/// Pre-match "good luck" list (0xBE), fixed 103. Its handler sub_436630 is also EMPTY, so
/// like <see cref="OnGoodLuckInfPacket"/> it has no client-side effect; the 100-byte body
/// is sent zeroed.
/// </summary>
public static class OnGoodLuckListInfPacket
{
    // Content is written from wire offset 3, so this is size - 3 zero bytes.
    public const int ContentSize = 103 - DjMaxPacketBuilder.StructuredPayloadOffset;

    public static Packet Build(byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnGoodLuckListInf, control)
            .WritePadding(ContentSize)
            .Build();
}

/// <summary>
/// Billing/session auth result (0xD2), fixed 27. Unlike the forwarders, sub_4369D0 actually
/// stores the body: six consecutive u32 from raw+3..+23 into net+895136..+895156. Their
/// meanings are not established, so the builder takes them positionally.
/// </summary>
public static class OnBillingAuthInfPacket
{
    public const int ValueCount = 6;

    public static Packet Build(
        IReadOnlyList<uint> values,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count != ValueCount)
        {
            throw new ArgumentException(
                $"OnBillingAuthInf stores exactly {ValueCount} u32 values.",
                nameof(values));
        }

        DjMaxPacketBuilder builder =
            DjMaxPacketBuilder.Fixed(PacketMeta.OnBillingAuthInf, control);
        foreach (uint value in values)
        {
            builder.WriteUInt32(value);
        }
        return builder.Build();
    }

    public static uint[] Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint[] values = new uint[ValueCount];
        for (int i = 0; i < ValueCount; i++)
        {
            values[i] = reader.ReadUInt32();
        }
        reader.EnsureComplete();
        return values;
    }
}

/// <summary>
/// Reserved scene ack (0x35), fixed 12. sub_433360 forwards a one-byte body to the active
/// scene and no discovered scene reads it. Kept for completeness / stream fidelity only.
/// </summary>
public static class OnReserved34InfPacket
{
    public static Packet Build(byte value = 0, byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnReserved34Inf, control)
            .WriteByte(value)
            .Build();
}
