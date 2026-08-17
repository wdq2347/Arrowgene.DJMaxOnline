using Arrowgene.DJMaxOnline.Server.Korea400.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Korea400.Packets;

/// <summary>
/// Server responses for the session-lifecycle, reward-item and effector-set
/// requests. Layouts are taken byte-for-byte from decrypted retail captures
/// (Files/blade_stream_*.yaml, replayed through <see cref="DjMaxCrypto"/>).
/// </summary>
public static class OnLogOutAckPacket
{
    // Korean retail sends a constant u16 status (0x0036) that sub_431720 stores at
    // net+895300 before cleaning up the channel session. It is a five-byte packet:
    // id + control + status, with no trailing China padding.
    //
    // A KICK REASON DOES NOT BELONG HERE, and this is settled - it was tried three times.
    // sub_431720 also sets dword_6846E8 = 1, which only a SUCCESSFUL login clears
    // (sub_4314F0). Until then the lobby update sub_444C5E leaves for the server list every
    // time it is entered, so the client cycles lobby -> server list -> lobby forever. And
    // the reason cannot outlive the fix for that either: closing the socket runs the
    // client's sub_434120, which zeroes net+895300 outright.
    //
    // A banned player is dropped (see ServerAdministrationService) and told why by the
    // launcher, which refuses to issue them a ticket.
    private const ushort Status = 0x0036;

    public static Packet Build(byte control = ProtocolPadding.Unused)
    {
        return DjMaxPacketBuilder.Fixed(PacketMeta.OnLogOutAck, control)
            .WriteUInt16(Status)
            .Build();
    }
}

/// <summary>
/// The retail client sends the same opaque eight-byte body in every captured
/// reward-item request. The server only needs to validate its exact framing;
/// assigning meanings to the pointer-like values would be speculative.
/// </summary>
public static class GetItemReqPacket
{
    /// <summary>
    /// A bare three-byte signal. The Korean sender sub_4362A0 writes only the packet
    /// id and sends size 3, so there is no body to decode - the China capture's
    /// eight opaque bytes belong to a different client. It raises a pending flag
    /// that only its ack or fail reply clears, so it must always be answered.
    /// </summary>
    public static void Parse(Packet packet) => ArgumentNullException.ThrowIfNull(packet);
}

/// <summary>
/// Captured level-up requests likewise carry an opaque eight-byte client body.
/// Keep it framed and logged without pretending the stable process-address-like
/// values are portable item identifiers.
/// </summary>
public static class ItemLevelUpReqPacket
{
    /// <summary>
    /// A bare three-byte signal. The Korean sender sub_436400 writes only the packet
    /// id and sends size 3, so there is no body to decode - the China capture's
    /// eight opaque bytes belong to a different client. It raises a pending flag
    /// that only its ack or fail reply clears, so it must always be answered.
    /// </summary>
    public static void Parse(Packet packet) => ArgumentNullException.ThrowIfNull(packet);
}

/// <summary>
/// Broadcasts an item-battle pickup (0xB4 -&gt; 0xB6). The gameplay callback
/// sub_40CD50 resolves <c>playerSlot</c>, then appends the item id and level to
/// that player's four-entry battle-item queue (sub_426120/sub_426BC0).
/// </summary>
public static class OnGetItemAckPacket
{
    public static Packet Build(
        byte playerSlot = 0,
        short itemId = 0,
        byte level = 0,
        byte control = ProtocolPadding.Unused)
    {
        // Korean wire layout: slot@3, signed item id@4, level@6.
        return DjMaxPacketBuilder.Fixed(PacketMeta.OnGetItemAck, control)
            .WriteByte(playerSlot)
            .WriteInt16(itemId)
            .WriteByte(level)
            .Build();
    }

    public static GetItemResponse Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        GetItemResponse response = new(
            reader.ReadByte(),
            reader.ReadInt16(),
            reader.ReadByte());
        reader.EnsureComplete();
        return response;
    }
}

public sealed record GetItemResponse(
    byte PlayerSlot,
    short ItemId,
    byte Level);

/// <summary>
/// Broadcasts an item-battle level change (0xB7 -&gt; 0xB9). The gameplay callback
/// sub_40CDB0 resolves <c>playerSlot</c> and updates the indexed queue entry via
/// sub_426190/sub_426CF0.
/// </summary>
public static class OnItemLevelUpAckPacket
{
    public static Packet Build(
        byte playerSlot = 0,
        byte queueIndex = 0,
        byte level = 1,
        byte control = ProtocolPadding.Unused)
    {
        // Korean wire layout: slot@3, zero-based queue index@4, new level@5.
        return DjMaxPacketBuilder.Fixed(PacketMeta.OnItemLevelUpAck, control)
            .WriteByte(playerSlot)
            .WriteByte(queueIndex)
            .WriteByte(level)
            .Build();
    }

    public static ItemLevelUpResponse Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        ItemLevelUpResponse response = new(
            reader.ReadByte(),
            reader.ReadByte(),
            reader.ReadByte());
        reader.EnsureComplete();
        return response;
    }
}

public sealed record ItemLevelUpResponse(
    byte PlayerSlot,
    byte QueueIndex,
    byte Level);

/// <summary>
/// Compact mid-song effector-set choice sent by sub_436740. The client selects one
/// descriptor for <see cref="SetIndex"/>, then sends its descriptor index and two
/// signed descriptor parameters followed by the set index itself.
/// </summary>
public sealed record EffectorSetSelection(
    short DescriptorIndex,
    short ParameterA,
    short ParameterB,
    short SetIndex);

public static class UseEffectorSetInfPacket
{
    public static EffectorSetSelection Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        EffectorSetSelection selection = new(
            reader.ReadInt16(),
            reader.ReadInt16(),
            reader.ReadInt16(),
            reader.ReadInt16());
        reader.EnsureComplete();
        return selection;
    }
}

public sealed record EffectorSetUpdate(
    EffectorSetSelection Selection,
    byte PlayerSlot);

/// <summary>
/// Broadcasts a member's compact selection as Korean 0xC6. sub_436830 passes the
/// first six bytes to sub_4291E7 as the descriptor, reads the set index at wire +9,
/// and reads the sender's room slot at wire +11.
/// </summary>
public static class OnUseEffectorSetInfPacket
{
    public static Packet Build(
        EffectorSetSelection selection,
        byte playerSlot,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return DjMaxPacketBuilder.Fixed(PacketMeta.OnUseEffectorSetInf, control)
            .WriteInt16(selection.DescriptorIndex)
            .WriteInt16(selection.ParameterA)
            .WriteInt16(selection.ParameterB)
            .WriteInt16(selection.SetIndex)
            .WriteByte(playerSlot)
            .Build();
    }

    public static EffectorSetUpdate Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        EffectorSetSelection selection = new(
            reader.ReadInt16(),
            reader.ReadInt16(),
            reader.ReadInt16(),
            reader.ReadInt16());
        byte playerSlot = reader.ReadByte();
        reader.EnsureComplete();
        return new EffectorSetUpdate(selection, playerSlot);
    }
}
