using Arrowgene.DJMaxOnline.Server;
using Arrowgene.DJMaxOnline.Server.Packets;

namespace Arrowgene.DJMaxOnline.Test;

public class EffectorSetPacketTest
{
    [Test]
    public void KoreanRequestParsesFourSignedSixteenBitFields()
    {
        // id C5, control CC, descriptor index 0x1234, parameter A 0x5678,
        // parameter B -2, set index 0x1357.
        PacketFactory factory = new();
        factory.FillReadBuffer(Convert.FromHexString("C500CC34127856FEFF5713"));
        Packet packet = factory.ReadPacket()!;

        Assert.Multiple(() =>
        {
            Assert.That(packet.Id, Is.EqualTo(PacketId.UseEffectorSetInf));
            Assert.That(UseEffectorSetInfPacket.Parse(packet), Is.EqualTo(
                new EffectorSetSelection(0x1234, 0x5678, -2, 0x1357)));
            Assert.That(factory.BufferedByteCount, Is.Zero);
            Assert.That(factory.HasPendingPacket, Is.False);
        });
    }

    [Test]
    public void KoreanBroadcastEchoesTheSelectionAndAppendsTheSenderSlot()
    {
        EffectorSetSelection selection = new(0x1234, 0x5678, -2, 0x1357);
        Packet packet = OnUseEffectorSetInfPacket.Build(selection, playerSlot: 3);
        byte[] wire = new PacketFactory().Write(packet);

        Assert.Multiple(() =>
        {
            Assert.That(wire, Is.EqualTo(
                Convert.FromHexString("C600CC34127856FEFF571303")));
            Assert.That(wire, Has.Length.EqualTo(PacketMeta.OnUseEffectorSetInf.Size));
            Assert.That(OnUseEffectorSetInfPacket.Parse(packet), Is.EqualTo(
                new EffectorSetUpdate(selection, 3)));
        });
    }
}
