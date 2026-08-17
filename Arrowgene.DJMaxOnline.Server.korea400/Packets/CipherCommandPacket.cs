using Arrowgene.DJMaxOnline.Server.Korea400.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Korea400.Packets;

/// <summary>
/// OnCipherCommandInf (0x10E), dynamic. This is the client's server-pushed diagnostic
/// channel, not a gameplay packet. sub_432CE0 framing:
///   +3   u32  wire size
///   +7   u32  key passed to the decryptor sub_496BC0
///   +11  ...  encrypted payload, <c>size - 11</c> bytes
///
/// The handler decrypts the payload with that key; on the failure branch it appends the
/// result to a dated file <c>cc-YYMMDD.csv</c> next to the client and shows
/// ">Cipher command executed". There is NO code execution - the word "command" is the
/// game's own naming; the handler only decrypts and writes a CSV line - but it does touch
/// the client's filesystem, so the server should only ever send this deliberately.
///
/// The payload here is opaque bytes; this builder does not synthesise the retail cipher
/// format, so a real client will take the failure (CSV) branch unless given a payload its
/// key actually decrypts. It exists so the id can be produced byte-exact.
/// </summary>
public static class OnCipherCommandInfPacket
{
    /// <summary>Bytes before the payload: id, header, and the u32 key.</summary>
    public const int PayloadOffset =
        DjMaxPacketBuilder.PacketIdSize + DjMaxPacketBuilder.HeaderSize + sizeof(uint);

    public static Packet Build(
        uint key,
        byte[] payload,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(payload);
        int wireSize = checked(PayloadOffset + payload.Length);
        return DjMaxPacketBuilder.Dynamic(PacketMeta.OnCipherCommandInf, wireSize, control)
            .WriteUInt32(key)
            .WriteBytes(payload)
            .Build();
    }

    public static (uint Key, byte[] Payload) Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint declaredWireSize = reader.ReadUInt32();
        int actualWireSize = DjMaxPacketBuilder.PacketIdSize +
                             DjMaxPacketBuilder.HeaderSize + packet.Data.Length;
        if (declaredWireSize != actualWireSize || reader.Remaining < sizeof(uint))
        {
            throw new InvalidDataException("Invalid cipher-command framing.");
        }

        uint key = reader.ReadUInt32();
        return (key, reader.ReadBytes(reader.Remaining));
    }
}
