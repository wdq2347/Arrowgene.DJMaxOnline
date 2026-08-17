using System.Text;
using Arrowgene.DJMaxOnline.Server.Japan400.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Japan400.Packets;

/// <summary>
/// One DJ메신저 conversation line. The client has already formatted the display text as
/// "&lt;sender nickname&gt; &gt; &lt;what was typed&gt;" before it sends, so the server relays the
/// bytes verbatim rather than reconstructing them.
/// </summary>
/// <param name="SenderUserId">
/// Request: the sender's own id (<c>net+794096</c>). Delivery: the id whose conversation
/// tab the line belongs to, which for the recipient is the SENDER - sub_48BD17 matches it
/// against dword_55E898, the ten open-tab user ids, and opens a new tab on no match.
/// </param>
/// <param name="TargetUserId">
/// Request: who the line is addressed to. Ignored on delivery - sub_48BD17 never reads
/// the field at +11.
/// </param>
/// <param name="EncodedText">
/// Raw message bytes, NOT terminated on the wire. Korean text is code page 949, so this
/// stays as bytes and is never transcoded.
/// </param>
public sealed record MessengerChatMessage(
    uint SenderUserId,
    uint TargetUserId,
    byte[] EncodedText);

/// <summary>
/// Shared framing for the messenger's two chat packets. Both are dynamic and identical in
/// shape - the request sub_431FC0 builds and the delivery sub_45EFCC decodes agree field
/// for field:
///   +3  u32  wire size, which is <c>15 + strlen(text)</c> and so EXCLUDES a terminator
///   +7  u32  user id
///   +11 u32  user id
///   +15 ...  text, unterminated
/// </summary>
public static class MessengerChatProtocol
{
    /// <summary>Bytes before the text: id, header, and the two user ids.</summary>
    public const int TextOffset = 15;

    /// <summary>
    /// The client's own send cap. sub_431FC0 copies the formatted line with
    /// <c>strncpy(buffer, text, 79)</c> and then forces <c>buffer[79] = 0</c>.
    /// </summary>
    public const int MaximumTextLength = 79;

    /// <summary>
    /// The hard ceiling the receive path imposes. sub_432830 memcpy's <c>size</c> bytes
    /// into a 100-byte stack buffer and writes its terminator at <c>packet + size</c>, so
    /// a longer line smashes the client's stack. Never emit past this.
    /// </summary>
    public const int MaximumWireSize = 100;

    internal static Packet Build(
        PacketMeta meta,
        MessengerChatMessage message,
        byte control)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(message.EncodedText);
        if (message.EncodedText.Length > MaximumTextLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(message),
                $"Messenger text cannot exceed {MaximumTextLength} bytes.");
        }
        if (message.EncodedText.Contains((byte)0))
        {
            throw new ArgumentException(
                "Encoded messenger text cannot contain an embedded terminator.",
                nameof(message));
        }

        int wireSize = checked(TextOffset + message.EncodedText.Length);
        return DjMaxPacketBuilder.Dynamic(meta, wireSize, control)
            .WriteUInt32(message.SenderUserId)
            .WriteUInt32(message.TargetUserId)
            .WriteBytes(message.EncodedText)
            .Build();
    }

    internal static MessengerChatMessage Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint declaredWireSize = reader.ReadUInt32();
        int actualWireSize = DjMaxPacketBuilder.PacketIdSize +
                             DjMaxPacketBuilder.HeaderSize + packet.Data.Length;
        if (declaredWireSize != actualWireSize ||
            reader.Remaining < sizeof(uint) * 2 ||
            actualWireSize > MaximumWireSize)
        {
            throw new InvalidDataException("Invalid messenger chat framing.");
        }

        uint senderUserId = reader.ReadUInt32();
        uint targetUserId = reader.ReadUInt32();
        byte[] text = reader.ReadBytes(reader.Remaining);

        // Nothing terminates the text on the wire, but trim defensively so a stray zero
        // never reaches the client's fixed buffer.
        int terminator = Array.IndexOf(text, (byte)0);
        if (terminator >= 0)
        {
            text = text[..terminator];
        }

        return new MessengerChatMessage(senderUserId, targetUserId, text);
    }
}

/// <summary>
/// MsgChatReq (0xFA), the line the DJ메신저 sends when the player presses enter in a
/// conversation window (sub_48C176 -> sub_431FC0). The client never receives this id - it
/// is absent from the registration table sub_42F440 - so the server must translate it into
/// <see cref="OnMsgChatInfPacket"/> for the recipient.
/// </summary>
public static class MsgChatReqPacket
{
    public static Packet Build(
        MessengerChatMessage message,
        byte control = ProtocolPadding.Unused) =>
        MessengerChatProtocol.Build(PacketMeta.MsgChatReq, message, control);

    public static MessengerChatMessage Parse(Packet packet) =>
        MessengerChatProtocol.Parse(packet);
}

/// <summary>
/// OnMsgChatInf (0xFB), the delivery half. sub_432830 receives it, runs the text through
/// the word filter sub_4A2AE0, and forwards the packet to the active scene, whose
/// dispatcher routes <c>case 251</c> to sub_48BD17 - which finds or opens the conversation
/// tab for the user id at +7 and appends the line.
/// </summary>
public static class OnMsgChatInfPacket
{
    public static Packet Build(
        MessengerChatMessage message,
        byte control = ProtocolPadding.Unused) =>
        MessengerChatProtocol.Build(PacketMeta.OnMsgChatInf, message, control);

    public static MessengerChatMessage Parse(Packet packet) =>
        MessengerChatProtocol.Parse(packet);

    /// <summary>
    /// Truncates a line to what the client can safely receive, without splitting a
    /// multi-byte code page 949 character.
    /// </summary>
    public static byte[] Clamp(byte[] encodedText)
    {
        ArgumentNullException.ThrowIfNull(encodedText);
        if (encodedText.Length <= MessengerChatProtocol.MaximumTextLength)
        {
            return encodedText;
        }

        int length = MessengerChatProtocol.MaximumTextLength;
        // Code page 949 lead bytes are >= 0x81; walk back over an odd run of them so the
        // cut never lands between a lead byte and its trail byte.
        int leadBytes = 0;
        for (int i = length - 1; i >= 0 && encodedText[i] >= 0x81; i--)
        {
            leadBytes++;
        }
        if (leadBytes % 2 != 0)
        {
            length--;
        }
        return encodedText[..length];
    }

    public static Encoding TextEncoding
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(949);
        }
    }
}
