using System.Text;
using Arrowgene.DJMaxOnline.Server;
using Arrowgene.DJMaxOnline.Server.Packets;

namespace Arrowgene.DJMaxOnline.Test;

/// <summary>
/// OnWChatInf (0x36) is the client's own whisper display packet. Everything asserted here
/// comes from sub_4324D0: it reads a direction byte at raw+7, copies <c>size - 8</c> bytes
/// of text from raw+8 into a pre-zeroed 256-byte buffer, and picks its caption from the
/// direction alone.
/// </summary>
public class WhisperPacketTest
{
    private static byte[] Wire(Packet packet) => new PacketFactory().Write(packet);

    [Test]
    public void DisplayPacketPutsDirectionAndTextWhereTheClientReadsThem()
    {
        byte[] text = Encoding.ASCII.GetBytes("Blade hello");
        byte[] wire = Wire(OnWChatInfPacket.Build(WhisperDirection.Received, text));

        Assert.That(wire, Has.Length.EqualTo(OnWChatInfPacket.TextOffset + text.Length));
        Assert.Multiple(() =>
        {
            Assert.That(BitConverter.ToUInt16(wire, 0), Is.EqualTo(0x36));
            Assert.That(BitConverter.ToUInt32(wire, 3), Is.EqualTo((uint)wire.Length),
                "declared size at +3 drives the client's copy length");
            Assert.That(wire[7], Is.EqualTo(8), "direction at +7");
            Assert.That(
                Encoding.ASCII.GetString(wire, 8, text.Length), Is.EqualTo("Blade hello"));
            Assert.That(wire[^1], Is.Not.EqualTo(0),
                "the text is unterminated; the client's buffer is pre-zeroed instead");
        });
    }

    [Test]
    public void OnlyDirectionEightTakesTheReceivedBranch()
    {
        // sub_4324D0 tests the byte for equality with 8 and nothing else, so the sent
        // form must not accidentally land on it.
        Assert.That((byte)WhisperDirection.Received, Is.EqualTo(8));
        Assert.That((byte)WhisperDirection.Sent, Is.Not.EqualTo(8));

        byte[] sent = Wire(OnWChatInfPacket.Build(
            WhisperDirection.Sent, Encoding.ASCII.GetBytes("Blade hi")));
        Assert.That(sent[7], Is.EqualTo(0));
    }

    [Test]
    public void TextWithoutASpaceIsRejectedRatherThanSilentlyDropped()
    {
        // The handler locates the first space with sub_438530 and renders NOTHING when
        // there is none, so a caller that forgets the nickname prefix would produce a
        // whisper that simply never appears.
        Assert.Throws<ArgumentException>(() => OnWChatInfPacket.Build(
            WhisperDirection.Received, Encoding.ASCII.GetBytes("nospacehere")));
    }

    [Test]
    public void TextCannotOverrunTheClientsBuffer()
    {
        // The receive buffer is 256 bytes and zeroed before the copy, so 255 is the most
        // that can be sent while leaving the terminator the display path relies on.
        byte[] tooLong = Encoding.ASCII.GetBytes(
            "N " + new string('x', OnWChatInfPacket.MaximumTextLength));
        Assert.Throws<ArgumentOutOfRangeException>(() => OnWChatInfPacket.Build(
            WhisperDirection.Received, tooLong));

        byte[] longest = Encoding.ASCII.GetBytes(
            "N " + new string('x', OnWChatInfPacket.MaximumTextLength - 2));
        Assert.That(longest, Has.Length.EqualTo(OnWChatInfPacket.MaximumTextLength));
        Assert.DoesNotThrow(() => OnWChatInfPacket.Build(
            WhisperDirection.Received, longest));
    }

    [Test]
    public void DisplayPacketRoundTripsThroughTheReceiveFraming()
    {
        byte[] text = Encoding.ASCII.GetBytes("Rival good luck");
        PacketFactory factory = new();
        byte[] wire = factory.Write(
            OnWChatInfPacket.Build(WhisperDirection.Received, text));

        factory.FillReadBuffer(wire);
        List<Packet> packets = factory.ReadPackets();

        Assert.That(packets, Has.Count.EqualTo(1));
        (WhisperDirection direction, byte[] parsed) = OnWChatInfPacket.Parse(packets[0]);
        Assert.Multiple(() =>
        {
            Assert.That(direction, Is.EqualTo(WhisperDirection.Received));
            Assert.That(parsed, Is.EqualTo(text));
        });
    }
}
