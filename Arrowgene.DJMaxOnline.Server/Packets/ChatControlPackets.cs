using Arrowgene.DJMaxOnline.Server.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Packets;

/// <summary>
/// What OnChatControlInf tells the client to do with its chat box. The client is the only
/// thing that enforces this - the server still has to drop or accept the traffic itself -
/// so these are a UI instruction, not a mute.
/// </summary>
public enum ChatControlState : byte
{
    /// <summary>Warn the player they are sending too fast; chat stays usable.</summary>
    Warn = 0,

    /// <summary>Disable the chat box.</summary>
    Disable = 1,

    /// <summary>Re-enable it. Nothing else does, so a Disable is permanent until sent.</summary>
    Enable = 2
}

/// <summary>
/// Chat flood control (0x9A), fixed 4 bytes: a single state byte at raw+3, read by
/// sub_4327E0 and applied by the lobby scene (sub_442787).
/// </summary>
public static class OnChatControlInfPacket
{
    public static Packet Build(
        ChatControlState state,
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnChatControlInf, control)
            .WriteByte((byte)state)
            .Build();

    public static ChatControlState Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        ChatControlState state = (ChatControlState)reader.ReadByte();
        reader.EnsureComplete();
        return state;
    }
}

/// <summary>
/// Event/room indicator (0x63), fixed 7 bytes. The room scene reads a command word at
/// raw+3 and a secondary value at raw+5 to toggle an indicator; observed commands are 1
/// and 2 with a value of 13.
///
/// Note the size is the client's own registration (7), which leaves room for exactly the
/// two words below. An older note in ProtocolReportWriter claimed reads as far as raw+15;
/// that cannot be this packet's own buffer at 7 bytes, so it is not reproduced here.
/// </summary>
public static class OnEventInfoInfPacket
{
    public static Packet Build(
        ushort command,
        ushort value,
        byte control = ProtocolPadding.Unused) =>
        DjMaxPacketBuilder.Fixed(PacketMeta.OnEventInfoInf, control)
            .WriteUInt16(command)
            .WriteUInt16(value)
            .Build();

    public static (ushort Command, ushort Value) Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        (ushort Command, ushort Value) parsed = (reader.ReadUInt16(), reader.ReadUInt16());
        reader.EnsureComplete();
        return parsed;
    }
}
