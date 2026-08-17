using System.Text;
using Arrowgene.DJMaxOnline.Server.Japan400.Protocol;

namespace Arrowgene.DJMaxOnline.Server.Japan400.Packets;

/// <summary>The variable-length text sent by the retail client as ChatInf.</summary>
public sealed record LobbyChatRequest(byte[] EncodedText)
{
    public string AsciiText => Encoding.ASCII.GetString(EncodedText);
}

public static class ChatInfPacket
{
    public const int MaximumTextLength = 264;

    public static LobbyChatRequest Parse(Packet packet)
    {
        DjMaxPacketReader reader = new(packet);
        uint declaredWireSize = reader.ReadUInt32();
        int actualWireSize = DjMaxPacketBuilder.PacketIdSize +
                             DjMaxPacketBuilder.HeaderSize + packet.Data.Length;
        if (declaredWireSize != actualWireSize ||
            reader.Remaining > MaximumTextLength)
        {
            throw new InvalidDataException("Invalid client chat framing.");
        }

        byte[] text = reader.ReadBytes(reader.Remaining);
        int terminator = Array.IndexOf(text, (byte)0);
        if (terminator >= 0)
        {
            text = text[..terminator];
        }

        return new LobbyChatRequest(text);
    }
}
