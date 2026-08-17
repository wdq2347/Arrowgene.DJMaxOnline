using Arrowgene.DJMaxOnline.Server.Korea400;
using Arrowgene.DJMaxOnline.Server.Korea400.Packets;

namespace Arrowgene.DJMaxOnline.Test;

/// <summary>
/// The seven server->client packets the client dispatches (sub_42FB20) but the server had
/// never sent. These assertions fix the wire framing against each client handler; the
/// point of the packets is that they are byte-exact and cannot desync the stream, since
/// several of the handlers themselves do nothing.
/// </summary>
public class MiscServerPacketTest
{
    [Test]
    public void InvitationCarriesTheInviterNameAndRoomIndex()
    {
        // sub_44419D reads an 11-byte name at wire+3 and the room index at wire+14:
        //   sub_5019B0(this+10194, a2+3, 11)  /  *(this+10193) = *(__int16 *)(a2+14)
        // 3 + 11 + 2 = 16, which is the packet's fixed size.
        byte[] wire = new PacketFactory().Write(
            OnInviteReqPacket.Build("Blade", roomIndex: 0x1234));

        Assert.Multiple(() =>
        {
            Assert.That(wire, Has.Length.EqualTo(16));
            Assert.That(
                System.Text.Encoding.ASCII.GetString(wire, 3, 5), Is.EqualTo("Blade"));
            Assert.That(BitConverter.ToUInt16(wire, 14), Is.EqualTo(0x1234));
            // A name longer than the field must not overrun into the room index.
            byte[] longName = new PacketFactory().Write(
                OnInviteReqPacket.Build("ABCDEFGHIJKLMNOP", roomIndex: 7));
            Assert.That(longName, Has.Length.EqualTo(16));
            Assert.That(BitConverter.ToUInt16(longName, 14), Is.EqualTo(7));
        });
    }

    private static byte[] Wire(Packet packet) => new PacketFactory().Write(packet);

    [Test]
    public void FixedPacketsMatchTheirRegisteredSizes()
    {
        // A server->client fixed packet that is one byte off its sub_42F440 registration
        // desyncs everything after it, so the sizes are asserted directly.
        Assert.Multiple(() =>
        {
            Assert.That(Wire(OnReserved34InfPacket.Build()), Has.Length.EqualTo(4));
            Assert.That(Wire(OnSlotControlInfPacket.Build(0)), Has.Length.EqualTo(4));
            Assert.That(Wire(OnInviteInfPacket.Build(1, 2)), Has.Length.EqualTo(16));
            Assert.That(Wire(OnGoodLuckInfPacket.Build(1)), Has.Length.EqualTo(7));
            Assert.That(Wire(OnGoodLuckListInfPacket.Build()), Has.Length.EqualTo(103));
            Assert.That(
                Wire(OnBillingAuthInfPacket.Build(new uint[6])), Has.Length.EqualTo(27));
        });
    }

    [Test]
    public void BillingAuthLandsSixValuesWhereTheHandlerReadsThem()
    {
        // sub_4369D0 stores six consecutive u32 from raw+3..+23 into net+895136..+895156.
        uint[] values = [10, 20, 30, 40, 50, 60];
        byte[] wire = Wire(OnBillingAuthInfPacket.Build(values));

        Assert.Multiple(() =>
        {
            for (int i = 0; i < OnBillingAuthInfPacket.ValueCount; i++)
            {
                Assert.That(
                    BitConverter.ToUInt32(wire, 3 + i * 4),
                    Is.EqualTo(values[i]),
                    $"value {i} must land at raw+{3 + i * 4}");
            }
        });

        Assert.Throws<ArgumentException>(() => OnBillingAuthInfPacket.Build(new uint[5]));
    }

    [Test]
    public void CipherCommandFramesKeyThenPayload()
    {
        // sub_432CE0: size@3, key@7, payload@11 for size-11 bytes.
        byte[] payload = [1, 2, 3, 4, 5];
        byte[] wire = Wire(OnCipherCommandInfPacket.Build(0xAABBCCDD, payload));

        Assert.Multiple(() =>
        {
            Assert.That(BitConverter.ToUInt16(wire, 0), Is.EqualTo(0x10E));
            Assert.That(BitConverter.ToUInt32(wire, 3), Is.EqualTo((uint)wire.Length));
            Assert.That(BitConverter.ToUInt32(wire, 7), Is.EqualTo(0xAABBCCDDu), "key@7");
            Assert.That(wire.Length - 11, Is.EqualTo(payload.Length), "payload from +11");
            Assert.That(wire[11..], Is.EqualTo(payload));
        });
    }

    [Test]
    public void CipherCommandRoundTripsThroughReceiveFraming()
    {
        PacketFactory factory = new();
        byte[] wire = factory.Write(
            OnCipherCommandInfPacket.Build(7, [9, 8, 7]));
        factory.FillReadBuffer(wire);
        List<Packet> packets = factory.ReadPackets();

        Assert.That(packets, Has.Count.EqualTo(1));
        (uint key, byte[] payload) = OnCipherCommandInfPacket.Parse(packets[0]);
        Assert.Multiple(() =>
        {
            Assert.That(key, Is.EqualTo(7u));
            Assert.That(payload, Is.EqualTo(new byte[] { 9, 8, 7 }));
        });
    }

    [Test]
    public void InviteCarriesFromUserIdAndRoomId()
    {
        byte[] wire = Wire(OnInviteInfPacket.Build(0x11223344, 0x55667788));
        Assert.Multiple(() =>
        {
            Assert.That(BitConverter.ToUInt32(wire, 3), Is.EqualTo(0x11223344u));
            Assert.That(BitConverter.ToUInt32(wire, 7), Is.EqualTo(0x55667788u));
        });
    }

    /// <summary>
    /// The 초대거부 toggle is FOUR bytes with the requested state at wire+3 (sub_434170),
    /// and the ack echoes it (sub_4341F0). Getting either size wrong desyncs the stream;
    /// not sending the ack at all leaves the client's pending flag (net+895004) raised, so
    /// the checkbox can never be clicked a second time.
    /// </summary>
    [Test]
    public void InviteRejectIsAFourByteBoolInBothDirections()
    {
        byte[] request = Wire(InviteRejectReqPacket.Build(refused: true));
        byte[] ack = Wire(OnInviteRejectAckPacket.Build(refused: true));
        Assert.Multiple(() =>
        {
            Assert.That(request, Has.Length.EqualTo(4));
            Assert.That(request[3], Is.EqualTo(1));
            Assert.That(ack, Has.Length.EqualTo(4));
            Assert.That(ack[3], Is.EqualTo(1));
        });

        Assert.Multiple(() =>
        {
            Assert.That(
                InviteRejectReqPacket.Parse(InviteRejectReqPacket.Build(true)).Refused,
                Is.True);
            Assert.That(
                InviteRejectReqPacket.Parse(InviteRejectReqPacket.Build(false)).Refused,
                Is.False);
            Assert.That(
                OnInviteRejectAckPacket.Parse(OnInviteRejectAckPacket.Build(false)),
                Is.False);
        });
    }

    /// <summary>
    /// sub_435F90 copies this packet's tail into net+892907, which sub_4372E0 reads as
    /// 32 x {code:u16, value:u16} and answers "owned" with the VALUE. Sending plain u32
    /// item ids leaves every high half zero, i.e. nothing is ever owned.
    /// </summary>
    [Test]
    public void EventItemRefreshUsesCodeValuePairsNotItemIds()
    {
        byte[] wire = Wire(OnUpdateUserInventoryEventItemInfPacket.BuildKorean(
            userId: 0x11223344, [new CollectionEntry(0xF801, 3)]));
        Assert.Multiple(() =>
        {
            Assert.That(wire, Has.Length.EqualTo(135));
            Assert.That(BitConverter.ToUInt32(wire, 3), Is.EqualTo(0x11223344u));
            Assert.That(BitConverter.ToUInt16(wire, 7), Is.EqualTo(0xF801),
                "code first");
            Assert.That(BitConverter.ToUInt16(wire, 9), Is.EqualTo(3),
                "then the value sub_4372E0 returns as 'owned'");
            Assert.That(BitConverter.ToUInt16(wire, 11), Is.EqualTo(0xFFFF),
                "unused slots keep the empty sentinel");
        });

        (uint userId, IReadOnlyList<CollectionEntry> entries) =
            OnUpdateUserInventoryEventItemInfPacket.ParseKorean(
                OnUpdateUserInventoryEventItemInfPacket.BuildKorean(
                    9, [new CollectionEntry(0xF802, 1)]));
        Assert.Multiple(() =>
        {
            Assert.That(userId, Is.EqualTo(9u));
            Assert.That(entries, Is.EqualTo(new[] { new CollectionEntry(0xF802, 1) }));
        });
    }

    /// <summary>
    /// sub_435F30 copies the 192-byte 0x2A tail into the same 48-entry collection cache
    /// OnLogInAck fills. A live award therefore needs code/value pairs and 0xFFFF padding,
    /// not the old plain-u32 default-item interpretation.
    /// </summary>
    [Test]
    public void CollectionRefreshReplacesAllFortyEightCodeValueSlots()
    {
        CollectionEntry[] expected =
        [
            new CollectionEntry(0x0400, 2),
            new CollectionEntry(0x042D, 1)
        ];
        Packet packet = OnUpdateUserInventoryDefaultItemInfPacket.BuildKorean(
            0x11223344, expected);
        byte[] wire = Wire(packet);

        Assert.Multiple(() =>
        {
            Assert.That(wire, Has.Length.EqualTo(199));
            Assert.That(BitConverter.ToUInt32(wire, 3), Is.EqualTo(0x11223344u));
            Assert.That(BitConverter.ToUInt16(wire, 7), Is.EqualTo(0x0400));
            Assert.That(BitConverter.ToUInt16(wire, 9), Is.EqualTo(2));
            Assert.That(BitConverter.ToUInt16(wire, 11), Is.EqualTo(0x042D));
            Assert.That(BitConverter.ToUInt16(wire, 13), Is.EqualTo(1));
            Assert.That(BitConverter.ToUInt16(wire, 15), Is.EqualTo(0xFFFF));
            Assert.That(BitConverter.ToUInt16(wire, 17), Is.Zero);
        });

        (uint userId, IReadOnlyList<CollectionEntry> entries) =
            OnUpdateUserInventoryDefaultItemInfPacket.ParseKorean(packet);
        Assert.Multiple(() =>
        {
            Assert.That(userId, Is.EqualTo(0x11223344u));
            Assert.That(entries, Is.EqualTo(expected));
            Assert.That(
                () => OnUpdateUserInventoryDefaultItemInfPacket.BuildKorean(
                    1, Enumerable.Repeat(new CollectionEntry(1, 1), 49).ToArray()),
                Throws.ArgumentException);
        });
    }

    /// <summary>
    /// OnGameStartInf lands on net+895136..895156, the same storage the course module uses
    /// for its ChangeCourseReq (895136) and ContinueCourseReq (895140) gates - a non-zero
    /// value there blocks the matching request for the whole session.
    /// </summary>
    [Test]
    public void GameStartParametersAreZeroSoCourseRequestsStayUnblocked()
    {
        byte[] wire = Wire(OnGameStartInfPacket.Build(
            LocalLobbyBootstrap.GameStartParameters));
        Assert.That(wire.Skip(3), Is.All.Zero);
    }
}
