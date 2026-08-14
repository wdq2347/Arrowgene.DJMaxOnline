using Arrowgene.DJMaxOnline.Server;
using Arrowgene.DJMaxOnline.Server.Packets;
using NUnit.Framework;

namespace Arrowgene.DJMaxOnline.Test;

/// <summary>
/// The client's video mode auto-plays the chart so the BGA can be watched. It is signalled
/// by one byte in StartReq; the bytes below are the two real captures of the same disc and
/// difficulty, which differed in nothing else.
/// </summary>
[TestFixture]
public class VideoModeTest
{
    private static Packet StartReq(params byte[] wire) =>
        new(new PacketMeta(PacketId.StartReq, 5, PacketSource.Client), wire);

    [Test]
    public void NormalStartIsNotVideoMode()
    {
        // 5F 00 E7 00 00 - Data holds the control byte and the two parameters.
        Assert.That(StartReqPacket.Parse(StartReq(0xE7, 0x00, 0x00)).VideoMode, Is.False);
    }

    [Test]
    public void VideoStartIsDetected()
    {
        // 5F 00 9E 01 00 - identical apart from this byte.
        Assert.That(StartReqPacket.Parse(StartReq(0x9E, 0x01, 0x00)).VideoMode, Is.True);
    }

    [Test]
    public void TheControlByteDoesNotDecideTheMode()
    {
        // The control byte differs on every packet, so it must not be read as the flag.
        Assert.That(StartReqPacket.Parse(StartReq(0x9E, 0x00, 0x00)).VideoMode, Is.False);
        Assert.That(StartReqPacket.Parse(StartReq(0xE7, 0x01, 0x00)).VideoMode, Is.True);
    }

    [Test]
    public void AShortPacketIsTreatedAsANormalStart()
    {
        Assert.That(StartReqPacket.Parse(StartReq(0xE7)).VideoMode, Is.False);
        Assert.That(StartReqPacket.Parse(StartReq()).VideoMode, Is.False);
    }
}
