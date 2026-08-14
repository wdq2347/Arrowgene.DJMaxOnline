using Arrowgene.DJMaxOnline.Server;
using Arrowgene.DJMaxOnline.Server.Packets;
using Arrowgene.DJMaxOnline.Server.Protocol;

namespace Arrowgene.DJMaxOnline.Test;

public class ReceivePacketCatalogTest
{
    [Test]
    public void RegistrationAndDispatcherTablesMatchTheClient()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                ReceivePacketCatalog.All,
                Has.Count.EqualTo(ReceivePacketCatalog.ExpectedCaseCount));
            Assert.That(
                ReceivePacketCatalog.Registrations,
                Has.Count.EqualTo(ReceivePacketCatalog.ExpectedRegistrationCount));
            Assert.That(ReceivePacketCatalog.RegisteredButIgnored, Has.Count.EqualTo(16));
            Assert.That(
                ReceivePacketCatalog.DispatchedButUnregistered
                    .Select(value => value.Id),
                Is.EqualTo(new[] { PacketId.OnPlaySkipInf }));
        });
    }

    [Test]
    public void CorrectedReceiveFramingIsAuthoritative()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                ReceivePacketCatalog.GetRegistration(PacketId.OnUseItemAck).WireSize,
                Is.EqualTo(16));
            Assert.That(
                ReceivePacketCatalog.GetRegistration(
                    PacketId.OnUpdateUserAccountClassInf).WireSize,
                Is.EqualTo(11));
            Assert.That(
                ReceivePacketCatalog.GetRegistration(PacketId.OnWChatInf).IsDynamic,
                Is.True);
            Assert.That(
                ReceivePacketCatalog.TryGetRegistration(PacketId.WChatReq, out _),
                Is.False);
            Assert.That(
                ReceivePacketCatalog.TryGetRegistration(PacketId.OnPlaySkipInf, out _),
                Is.False);
            Assert.That(
                ReceivePacketCatalog.TryGet(PacketId.OnUserInfoInf, out _),
                Is.False,
                "0x43 is a synthetic capture-verifier split, not a receive case");
            Assert.That(
                ReceivePacketCatalog.TryGet(PacketId.OnUbsAwardInfoInf, out _),
                Is.False);
            Assert.That(PacketMeta.sub_436120.Size, Is.EqualTo(3));
            Assert.That(PacketMeta.sub_4362D0.Size, Is.EqualTo(4));
        });
    }

    [TestCase("A300CC6A0070", PacketId.sub_436120)]
    [TestCase("5600CC016A0070", PacketId.sub_4362D0)]
    public void AlternateShortRequestsDoNotConsumeTheFollowingPacket(
        string hexadecimalWire,
        PacketId firstId)
    {
        PacketFactory factory = new();
        factory.FillReadBuffer(Convert.FromHexString(hexadecimalWire));

        List<Packet> packets = factory.ReadPackets();

        Assert.Multiple(() =>
        {
            Assert.That(packets, Has.Count.EqualTo(2));
            Assert.That(packets[0].Id, Is.EqualTo(firstId));
            Assert.That(packets[1].Id, Is.EqualTo(PacketId.PlayOverReq));
            Assert.That(factory.BufferedByteCount, Is.Zero);
            Assert.That(factory.HasPendingPacket, Is.False);
        });

        if (firstId == PacketId.sub_436120)
        {
            Assert.DoesNotThrow(() => UnknownA3ReqPacket.Parse(packets[0]));
        }
        else
        {
            Assert.That(PeerStateReqPacket.Parse(packets[0]), Is.EqualTo(1));
        }
    }

    [Test]
    public void CorrectedPacketsHaveSafeWireShapes()
    {
        PacketFactory factory = new();
        Packet accountClassPacket = OnUpdateUserAccountClassInfPacket.Build(
            0x10203040, 0x89ABCDEF);

        Assert.Multiple(() =>
        {
            Assert.That(factory.Write(OnUseItemAckPacket.Build()), Has.Length.EqualTo(16));
            Assert.That(factory.Write(OnPlaySkipInfPacket.Build()), Has.Length.EqualTo(3));
            Assert.That(factory.Write(accountClassPacket), Has.Length.EqualTo(11));
            Assert.That(
                OnUpdateUserAccountClassInfPacket.Parse(accountClassPacket),
                Is.EqualTo(new UserAccountClassUpdate(0x10203040, 0x89ABCDEF)));
        });
    }

    [Test]
    public void ThreeBytePlaySkipDoesNotConsumeFollowingPlayStatePacket()
    {
        // Exact coalesced receive reported by the retail client: a bare 0x67
        // immediately followed by a 22-byte 0x6C packet.
        byte[] wire = Convert.FromHexString(
            "6700B96C00A9683F6B6AA7F1A63BFA005F6C69C85F2F8F4B2F");
        PacketFactory factory = new();
        factory.FillReadBuffer(wire);

        List<Packet> packets = factory.ReadPackets();

        Assert.Multiple(() =>
        {
            Assert.That(packets, Has.Count.EqualTo(2));
            Assert.That(packets[0].Id, Is.EqualTo(PacketId.PlaySkipReq));
            Assert.That(packets[1].Id, Is.EqualTo(PacketId.PlayStateInf));
            Assert.That(factory.BufferedByteCount, Is.Zero);
            Assert.That(factory.HasPendingPacket, Is.False);
        });
    }

    [Test]
    public void LeaveRoomAcknowledgementMatchesFourByteClientRegistration()
    {
        Packet packet = OnLeaveRoomAckPacket.Build(LeaveRoomResult.Success);
        byte[] wire = new PacketFactory().Write(packet);

        Assert.Multiple(() =>
        {
            Assert.That(wire, Has.Length.EqualTo(4));
            Assert.That(OnLeaveRoomAckPacket.Parse(packet),
                Is.EqualTo(LeaveRoomResult.Success));
        });
    }

    [Test]
    public void ReturnToServerListUsesFiveByteLogoutPackets()
    {
        byte[] requestWire = Convert.FromHexString("1900BA0000");
        PacketFactory reader = new();
        reader.FillReadBuffer(requestWire);
        Packet request = reader.ReadPacket()!;

        byte[] acknowledgement = new PacketFactory().Write(
            OnLogOutAckPacket.Build());

        Assert.Multiple(() =>
        {
            Assert.That(request.Id, Is.EqualTo(PacketId.LogOutReq));
            Assert.That(reader.BufferedByteCount, Is.Zero);
            Assert.That(reader.HasPendingPacket, Is.False);
            Assert.That(acknowledgement, Has.Length.EqualTo(5));
            Assert.That(acknowledgement.AsSpan(3, 2).ToArray(),
                Is.EqualTo(new byte[] { 0x36, 0x00 }));
        });
    }
}
