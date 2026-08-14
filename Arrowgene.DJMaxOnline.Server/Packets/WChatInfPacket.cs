using System.Text;
using Arrowgene.DJMaxOnline.Server.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Packets;

/// <summary>
/// The retail client's variable-length whisper request (0x35). The packet
/// constructor at sub_433610 writes a 25-byte target nickname followed by up
/// to 239 message bytes, without a message terminator.
/// </summary>
public sealed record WhisperRequest(string TargetNickname, byte[] EncodedText)
{
    public string AsciiText => Encoding.ASCII.GetString(EncodedText);
}

/// <summary>
/// Which side of a whisper a line is, from the byte the client reads at raw+7. It picks
/// the caption from this and nothing else: 8 takes the "received" branch, everything else
/// takes the "sent" branch.
/// </summary>
public enum WhisperDirection : byte
{
    /// <summary>Echo to the author: rendered from WCHAT2, "[Whisper]%s".</summary>
    Sent = 0,

    /// <summary>
    /// Delivery to the addressee: rendered from WCHAT1, "[Whisper]%s%s". This branch
    /// splits the text at its first space and runs the leading nickname through
    /// sub_42A24C, so an incoming whisper is the one form that shows a GM decoration.
    /// </summary>
    Received = 8
}

/// <summary>
/// OnWChatInf (0x36), the client's own whisper display packet - dynamic:
///   +3  u32  wire size
///   +7  u8   direction (see <see cref="WhisperDirection"/>)
///   +8  ...  text, <c>size - 8</c> bytes
///
/// sub_4324D0 copies the text into a 256-byte buffer that it zeroes first, so the text is
/// not terminated on the wire and must stay under that.
///
/// The text is ALWAYS "nickname&lt;space&gt;message": the handler locates the first space with
/// sub_438530 and renders nothing at all when there is none. Which nickname belongs there
/// depends on the direction - the addressee for a Sent echo, the author for a Received
/// delivery.
/// </summary>
public static class OnWChatInfPacket
{
    /// <summary>Bytes before the text: id, header, and the direction byte.</summary>
    public const int TextOffset = 8;

    /// <summary>
    /// The receive buffer is 256 bytes and pre-zeroed, so this keeps one byte spare for
    /// the terminator the client relies on.
    /// </summary>
    public const int MaximumTextLength = 255;

    public static Packet Build(
        WhisperDirection direction,
        byte[] encodedText,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(encodedText);
        if (encodedText.Length > MaximumTextLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(encodedText),
                $"Whisper display text cannot exceed {MaximumTextLength} bytes.");
        }
        if (Array.IndexOf(encodedText, (byte)' ') < 0)
        {
            throw new ArgumentException(
                "Whisper display text must contain a space; the client splits the " +
                "nickname from the message there and draws nothing without one.",
                nameof(encodedText));
        }

        int wireSize = checked(TextOffset + encodedText.Length);
        return DjMaxPacketBuilder.Dynamic(PacketMeta.OnWChatInf, wireSize, control)
            .WriteByte((byte)direction)
            .WriteBytes(encodedText)
            .Build();
    }

    public static (WhisperDirection Direction, byte[] EncodedText) Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint declaredWireSize = reader.ReadUInt32();
        int actualWireSize = DjMaxPacketBuilder.PacketIdSize +
                             DjMaxPacketBuilder.HeaderSize + packet.Data.Length;
        if (declaredWireSize != actualWireSize || reader.Remaining < 1)
        {
            throw new InvalidDataException("Invalid whisper display framing.");
        }

        WhisperDirection direction = (WhisperDirection)reader.ReadByte();
        return (direction, reader.ReadBytes(reader.Remaining));
    }
}

public static class WChatReqPacket
{
    public const int TargetNicknameSize = 25;
    public const int MaximumTextLength = 239;

    public static Packet Build(
        WhisperRequest request,
        byte control = ProtocolPadding.Unused)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.EncodedText);
        if (request.EncodedText.Length > MaximumTextLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request), $"Whisper text cannot exceed {MaximumTextLength} bytes.");
        }
        if (request.EncodedText.Contains((byte)0))
        {
            throw new ArgumentException(
                "Encoded whisper text cannot contain an embedded terminator.",
                nameof(request));
        }

        int wireSize = checked(
            DjMaxPacketBuilder.PacketIdSize + DjMaxPacketBuilder.HeaderSize +
            TargetNicknameSize + request.EncodedText.Length);
        return DjMaxPacketBuilder
            .Dynamic(PacketMeta.WChatReq, wireSize, control)
            .WriteFixedAscii(request.TargetNickname, TargetNicknameSize)
            .WriteBytes(request.EncodedText)
            .Build();
    }

    public static WhisperRequest Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint declaredWireSize = reader.ReadUInt32();
        int actualWireSize = DjMaxPacketBuilder.PacketIdSize +
                             DjMaxPacketBuilder.HeaderSize + packet.Data.Length;
        if (declaredWireSize != actualWireSize ||
            reader.Remaining < TargetNicknameSize ||
            reader.Remaining > TargetNicknameSize + MaximumTextLength)
        {
            throw new InvalidDataException("Invalid client whisper framing.");
        }

        string targetNickname = reader.ReadFixedAscii(TargetNicknameSize);
        byte[] text = reader.ReadBytes(reader.Remaining);
        int terminator = Array.IndexOf(text, (byte)0);
        if (terminator >= 0)
        {
            text = text[..terminator];
        }

        return new WhisperRequest(targetNickname, text);
    }
}
