using Arrowgene.DJMaxOnline.Server.Korea400;
using Arrowgene.DJMaxOnline.Server.Korea400.Packets;
using Arrowgene.DJMaxOnline.Server.Korea400.Protocol;

namespace Arrowgene.DJMaxOnline.Test;

public class EasyServerPacketTest
{
    [Test]
    public void RecoveredFixedPacketsUseRetailWireSizes()
    {
        RoomMemberInfo member = RoomMemberInfo.CreateLocal(
            LocalPlayerProfile.Default,
            slot: 0,
            connectionId: 1,
            team: 1,
            isHost: true);
        PacketFactory factory = new();
        Packet scheduledDisconnect = OnDisconnectPeerInfPacket.Build();

        Assert.Multiple(() =>
        {
            Assert.That(factory.Write(OnAliveReqPacket.Build()), Has.Length.EqualTo(3));
            Assert.That(
                factory.Write(OnDisconnectPeerInfPacket.Build(0)),
                Has.Length.EqualTo(5));
            Assert.That(
                OnDisconnectPeerInfPacket.Parse(scheduledDisconnect).Reason,
                Is.EqualTo((short)DisconnectPeerReason.Scheduled));
            Assert.That(
                factory.Write(scheduledDisconnect),
                Is.EqualTo(new byte[] { 0x08, 0x00, 0xCC, 0x00, 0x00 }));
            Assert.That(
                factory.Write(OnUserInfoResNotFoundPacket.Build()),
                Has.Length.EqualTo(3));
            Assert.That(
                factory.Write(JoinerListPacket.BuildStart()),
                Has.Length.EqualTo(3));
            Assert.That(
                factory.Write(JoinerListPacket.BuildEntry(member)),
                Has.Length.EqualTo(135));
            Assert.That(
                factory.Write(JoinerListPacket.BuildEnd()),
                Has.Length.EqualTo(3));
            Assert.That(
                factory.Write(OnQuickInviteAckPacket.Build(
                    QuickInviteResult.NoUsersAvailable)),
                Has.Length.EqualTo(4));
            Assert.That(
                factory.Write(ItemFailurePackets.BuildGetItemFail()),
                Has.Length.EqualTo(3));
            Assert.That(
                factory.Write(ItemFailurePackets.BuildItemLevelUpFail()),
                Has.Length.EqualTo(3));
            Assert.That(
                factory.Write(ItemFailurePackets.BuildUseItemFail()),
                Has.Length.EqualTo(3));
            Assert.That(
                factory.Write(OnAlertCreditInfPacket.Build(0)),
                Has.Length.EqualTo(7));
        });
    }

    [Test]
    public void DynamicPeerListRoundTripsTenByteEntries()
    {
        PeerCountEntry[] expected =
        [
            new(0x1122, 0x3344, 5, 6, 7),
            new(0x8899, 0xAABB, 8, 9, 10)
        ];
        Packet packet = OnPeerCountInfPacket.Build(expected);
        byte[] wire = new PacketFactory().Write(packet);

        Assert.Multiple(() =>
        {
            Assert.That(wire, Has.Length.EqualTo(7 + expected.Length * 10));
            Assert.That(OnPeerCountInfPacket.Parse(packet), Is.EqualTo(expected));
            Assert.That(PacketMeta.OnPeerCountInf.IsDynamicSize, Is.True);
        });
    }

    [Test]
    public void QuickInviteResultsSelectTheCorrectRetailMessages()
    {
        PacketFactory factory = new();

        Assert.Multiple(() =>
        {
            Assert.That(factory.Write(OnQuickInviteAckPacket.Build(
                QuickInviteResult.Sent))[3], Is.EqualTo(0x86), "QUICKINVITEMSG1");
            Assert.That(factory.Write(OnQuickInviteAckPacket.Build(
                QuickInviteResult.NoUsersAvailable))[3], Is.EqualTo(0x87),
                "QUICKINVITEMSG2");
            Assert.That(factory.Write(OnQuickInviteAckPacket.Build(
                QuickInviteResult.UnableToInvite))[3], Is.EqualTo(0x88),
                "QUICKINVITEMSG3");
            Assert.That(factory.Write(OnQuickInviteAckPacket.Build(
                QuickInviteResult.MaximumInvitationsExceeded))[3], Is.EqualTo(0x89),
                "QUICKINVITEMSG4");
        });
    }

    [Test]
    public void StartResultsSelectTheCorrectRetailMessages()
    {
        PacketFactory factory = new();

        Assert.Multiple(() =>
        {
            Assert.That(factory.Write(OnStartInfPacket.Build(
                StartResult.Success))[3], Is.EqualTo(0x98), "start game");
            Assert.That(factory.Write(OnStartInfPacket.Build(
                StartResult.NotAllPlayersReady))[3], Is.EqualTo(0x99), "STARTMSG1");
            Assert.That(factory.Write(OnStartInfPacket.Build(
                StartResult.TeamPlayerCountMismatch))[3], Is.EqualTo(0x9A), "STARTMSG2");
            Assert.That(factory.Write(OnStartInfPacket.Build(
                StartResult.TeamBattleRequiresTwoTeams))[3], Is.EqualTo(0x9B),
                "STARTMSG3");
            Assert.That(factory.Write(OnStartInfPacket.Build(
                StartResult.TeamHasInsufficientPlayers))[3], Is.EqualTo(0x9C),
                "STARTMSG4");
            Assert.That(factory.Write(OnStartInfPacket.Build(
                StartResult.NotEnoughPlayers))[3], Is.EqualTo(0x9D), "STARTMSG5");
            Assert.That(factory.Write(OnStartInfPacket.Build(
                StartResult.InsufficientMax))[3], Is.EqualTo(0x9E), "STARTMSG6");
        });
    }

    [Test]
    public void CourseListUsesExactClientWireLayout()
    {
        ushort[] expected = [0, 1];
        Packet packet = OnCourseListInfPacket.Build(expected);
        byte[] wire = new PacketFactory().Write(packet);

        Assert.Multiple(() =>
        {
            Assert.That(wire, Is.EqualTo(new byte[]
            {
                0x82, 0x00, 0xCC, 0x0B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00
            }));
            Assert.That(OnCourseListInfPacket.Parse(packet), Is.EqualTo(expected));
            Assert.That(PacketMeta.OnCourseListInf.IsDynamicSize, Is.True);
        });
    }

    [Test]
    public void NewlyHandledClientRequestsRoundTripRecoveredLayouts()
    {
        byte[] mount = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        Packet mountPacket = UseMountItemInfPacket.Build(mount);
        Packet useItemPacket = UseItemReqPacket.Build(slot: 8);
        Packet iconPacket = UpdateUserIconReqPacket.Build(0x8800);
        Packet quickPacket = QuickInviteReqPacketForTest();
        PacketFactory factory = new();

        Assert.Multiple(() =>
        {
            Assert.That(factory.Write(mountPacket), Has.Length.EqualTo(67));
            Assert.That(UseMountItemInfPacket.Parse(mountPacket), Is.EqualTo(mount));
            // Korean UseItemReq is four wire bytes carrying only the item-box slot
            // (sub_436510), not China's twelve-byte {itemId, value, slot}.
            Assert.That(factory.Write(useItemPacket), Has.Length.EqualTo(4));
            Assert.That(
                UseItemReqPacket.Parse(useItemPacket), Is.EqualTo(new UseItemRequest(8)));
            Assert.That(factory.Write(iconPacket), Has.Length.EqualTo(7));
            Assert.That(UpdateUserIconReqPacket.Parse(iconPacket), Is.EqualTo(0x8800u));
            // 빠른초대 is a bare 3-byte signal on this client (observed: A0 00 AD).
            // Registering China's 11 made the framer eat 8 bytes out of the next packet.
            Assert.That(factory.Write(quickPacket), Has.Length.EqualTo(3));
            Assert.DoesNotThrow(() => QuickInviteReqPacket.Parse(quickPacket));
        });
    }

    [Test]
    public void CaptureDerivedItemAndMissionPacketsRoundTrip()
    {
        Packet crItem = OnCrItemInfPacket.Build();
        Packet mission = OnMissionStandItemInfPacket.BuildCaptured();
        Packet credit = OnAlertCreditInfPacket.Build(10);
        Packet getItem = OnGetItemAckPacket.Build(
            playerSlot: 1, itemId: 4, level: 0);
        Packet levelUp = OnItemLevelUpAckPacket.Build(
            playerSlot: 1, queueIndex: 0, level: 2);
        BattleItemUseEffect useEffect = new(
            SourceSlot: 1,
            TargetSlot: 2,
            EffectId: 0,
            Parameter0: 2,
            Parameter1: 10,
            Parameter2: 20,
            Parameter3: 30,
            Parameter4: 40);
        Packet useItem = OnUseItemAckPacket.Build(useEffect);
        PacketFactory factory = new();

        Assert.Multiple(() =>
        {
            // GetItemReq/ItemLevelUpReq are bare 3-byte signals in the Korean client
            // (sub_4362A0 / sub_436400); their sizes are asserted below.
            Assert.That(PacketMeta.GetItemReq.Size, Is.EqualTo(3));
            Assert.That(PacketMeta.ItemLevelUpReq.Size, Is.EqualTo(3));
            Assert.That(factory.Write(crItem), Has.Length.EqualTo(3));
            // Korean OnCrItemInf carries no body, so parsing only verifies the framing.
            Assert.DoesNotThrow(() => OnCrItemInfPacket.Parse(crItem));
            Assert.That(factory.Write(mission), Has.Length.EqualTo(14));
            Assert.That(
                OnMissionStandItemInfPacket.Parse(mission),
                Is.EqualTo(OnMissionStandItemInfPacket.CapturedPayloadCopy()));
            Assert.That(OnAlertCreditInfPacket.Parse(credit), Is.EqualTo(10));
            Assert.That(
                OnGetItemAckPacket.Parse(getItem),
                Is.EqualTo(new GetItemResponse(1, 4, 0)));
            Assert.That(
                factory.Write(getItem),
                Is.EqualTo(new byte[] { 0xB6, 0x00, 0xCC, 0x01, 0x04, 0x00, 0x00 }));
            Assert.That(
                OnItemLevelUpAckPacket.Parse(levelUp),
                Is.EqualTo(new ItemLevelUpResponse(1, 0, 2)));
            Assert.That(
                factory.Write(levelUp),
                Is.EqualTo(new byte[] { 0xB9, 0x00, 0xCC, 0x01, 0x00, 0x02 }));
            Assert.That(factory.Write(useItem), Has.Length.EqualTo(16));
            Assert.That(OnUseItemAckPacket.Parse(useItem), Is.EqualTo(useEffect));
            Assert.That(
                factory.Write(useItem),
                Is.EqualTo(new byte[]
                {
                    0xBC, 0x00, 0xCC, 0x01, 0x02, 0x00, 0x00, 0x02,
                    0x0A, 0x00, 0x14, 0x00, 0x1E, 0x00, 0x28, 0x00
                }));
        });
    }

    private static Packet QuickInviteReqPacketForTest() =>
        Arrowgene.DJMaxOnline.Server.Korea400.Protocol.DjMaxPacketBuilder
            .Fixed(PacketMeta.QuickInviteReq, ProtocolPadding.Unused)
            .Build();

    private static Packet BuildCapturedRequest(
        PacketMeta meta,
        byte[] payload) =>
        Arrowgene.DJMaxOnline.Server.Korea400.Protocol.DjMaxPacketBuilder
            .Fixed(meta, ProtocolPadding.Unused)
            .WriteBytes(payload)
            .Build();
}
