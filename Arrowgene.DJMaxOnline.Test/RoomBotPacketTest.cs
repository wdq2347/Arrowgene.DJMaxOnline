using Arrowgene.DJMaxOnline.Server;
using Arrowgene.DJMaxOnline.Server.Packets;

namespace Arrowgene.DJMaxOnline.Test;

/// <summary>
/// The joiner record for a roster stand-in placed in a room slot. A bot renders as a
/// second occupant so the multiplayer UI can be tested on one account; these assertions
/// fix the record it produces and the removal state that withdraws it.
/// </summary>
public class RoomBotPacketTest
{
    private static RosterUser Rival() => new()
    {
        UserId = 2,
        AccountId = "NPC2",
        Nickname = "Rival",
        Gender = 1,
        IconId = 0x2000,
        Level = 27,
        AccountClass = 0
    };

    [Test]
    public void BotJoinerRecordCarriesTheRosterIdentity()
    {
        RoomMemberInfo record = RoomMemberInfo.CreateBot(
            Rival(), slot: 1, connectionId: 0xF001, team: 2);

        Assert.Multiple(() =>
        {
            Assert.That(record.Slot, Is.EqualTo(1));
            Assert.That(record.UserId, Is.EqualTo(2u));
            Assert.That(record.Nickname, Is.EqualTo("Rival"));
            Assert.That(record.ConnectionId, Is.EqualTo(0xF001));
            Assert.That(record.Team, Is.EqualTo(2));
            Assert.That(record.Level, Is.EqualTo(27u));
            // A bot is always a guest; only a real client can host a room.
            Assert.That(record.State, Is.EqualTo(RoomMemberState.Guest));
            // Icon goes out one-based, like every other icon field.
            Assert.That(record.IconWireValue, Is.EqualTo(0x2000u + 1));
            // No play history.
            Assert.That(record.Wins, Is.EqualTo(0u));
            Assert.That(record.MiscStatistics, Has.Count.EqualTo(
                LocalPlayerProgress.MiscStatisticCount));
        });
    }

    [Test]
    public void BotRecordBuildsAValidJoinerPacket()
    {
        // The record must satisfy the same fixed builder a real member uses, so a bot and
        // a real joiner are indistinguishable on the wire.
        RoomMemberInfo record = RoomMemberInfo.CreateBot(
            Rival(), slot: 1, connectionId: 0xF001, team: 2);
        byte[] wire = new PacketFactory().Write(
            OnUpdateJoinerInfoInfPacket.Build(record));

        Assert.That(wire, Has.Length.EqualTo(PacketMeta.OnUpdateJoinerInfoInf.Size));
    }

    [Test]
    public void JoinerRecordCarriesGenderWhereTheInGamePanelReadsIt()
    {
        // sub_4288A9 copies this whole 132-byte record to Player+44 when the room scene
        // handles OnStartInf, so wire+62 lands at Player+103 - the byte
        // Panel::LoadNoteVGI (sub_41ED1E @0x41F0AD) tests to pick the PanelGender<slot>
        // frame, and wire+63 lands at Player+104, the icon sub_461F03 resolves against
        // IconSet.csv. The room's key mode used to occupy the gender byte.
        RosterUser female = Rival();
        female.Gender = 0;
        byte[] male = new PacketFactory().Write(OnUpdateJoinerInfoInfPacket.Build(
            RoomMemberInfo.CreateBot(Rival(), slot: 1, connectionId: 0xF001, team: 2)));
        byte[] none = new PacketFactory().Write(OnUpdateJoinerInfoInfPacket.Build(
            RoomMemberInfo.CreateBot(female, slot: 1, connectionId: 0xF001, team: 2)));

        Assert.Multiple(() =>
        {
            Assert.That(male[61], Is.EqualTo(2), "wire+61 is the team");
            Assert.That(male[62], Is.EqualTo(1), "wire+62 is the gender");
            Assert.That(none[62], Is.EqualTo(0), "wire+62 is the gender");
            // The icon immediately follows it and is sent one-based; sub_4348B0
            // decrements it in place before storing the record.
            Assert.That(
                BitConverter.ToUInt32(male, 63), Is.EqualTo(0x2000u + 1),
                "wire+63 is the one-based icon");
        });
    }

    /// <summary>
    /// The bot outcome feature drives its result purely through judgment counters and
    /// combo (it keeps the host's clear/fail byte untouched). These construct the same
    /// shapes the synthesizer builds and pin the invariant it relies on: a win must
    /// out-score a lose, and both must build a valid 0x70.
    /// </summary>
    [Test]
    public void WinResultOutscoresLoseResult()
    {
        const int notes = 900;
        StageResult win = Result(
            perfect: notes, breaks: 0, weak: 0, maxCombo: (uint)notes);
        StageResult lose = Result(
            perfect: 0, breaks: notes / 3, weak: notes - notes / 3, maxCombo: (uint)notes / 8);

        Assert.Multiple(() =>
        {
            Assert.That(win.Accuracy, Is.EqualTo(100f), "all-perfect is 100% accuracy");
            Assert.That(lose.Accuracy, Is.LessThan(10f), "weak hits weigh almost nothing");
            Assert.That(win.Score, Is.GreaterThan(lose.Score));
            Assert.That(win.FullCombo, Is.True, "no breaks and not failed");
            Assert.That(lose.FullCombo, Is.False);
        });
    }

    [Test]
    public void SynthesizedResultsBuildValidStageResultPackets()
    {
        StageResult win = Result(900, 0, 0, 900);
        Assert.DoesNotThrow(() =>
            new PacketFactory().Write(OnStageResultExInfPacket.Build(slot: 1, win)));
    }

    /// <summary>Builds a 13-counter result the way SynthesizeBotResult does.</summary>
    private static StageResult Result(int perfect, int breaks, int weak, uint maxCombo)
    {
        ushort[] judgments = new ushort[StageResult.JudgmentCount];
        judgments[1] = (ushort)breaks;   // combo-breaking miss
        judgments[2] = (ushort)weak;     // weight-1 hit
        judgments[12] = (ushort)perfect; // weight-100 hit
        return new StageResult(
            SessionToken: 0,
            ClientFlags: 0,
            TotalNotes: (ushort)(perfect + breaks + weak),
            Judgments: judgments,
            Gauge: 1f,
            EncodedCombo: 0,
            CurrentCombo: maxCombo,
            MaxCombo: maxCombo,
            AuxiliaryValue: 0,
            ResultState: 0,
            Tail: 0);
    }

    [Test]
    public void SlotLockKeepsTheIndexAndInvertsTheStateByte()
    {
        // The slot index goes in and comes back out as the same number - rebasing it by
        // one made locks whose converted index landed on an occupied slot fail the
        // occupancy guard silently, so locking stopped working entirely.
        //
        // raw+4 is "is the X drawn", so it is the OPPOSITE of enabled: sub_45284E logs
        // "X-ON:%d" for 1 and "X-OFF:%d" for 0, and the X is the closed marker. Passing
        // the enabled flag straight through inverted every lock.
        Packet request = new(PacketMeta.SlotControlReq, [0x7E, 0x03]);
        byte slot = SlotControlReqPacket.Parse(request).Slot;

        byte[] closed = new PacketFactory().Write(OnSlotControlAckPacket.Build(
            new SlotControlResponse(slot, Enabled: false)));
        byte[] open = new PacketFactory().Write(OnSlotControlAckPacket.Build(
            new SlotControlResponse(slot, Enabled: true)));

        Assert.Multiple(() =>
        {
            Assert.That(slot, Is.EqualTo(3));
            Assert.That(closed, Has.Length.EqualTo(5));
            Assert.That(closed[3], Is.EqualTo(3), "slot echoed unchanged");
            Assert.That(closed[4], Is.EqualTo(1), "a CLOSED slot turns the X on");
            Assert.That(open[4], Is.EqualTo(0), "an OPEN slot clears it");
            // ...and the reader has to undo the inversion.
            Assert.That(
                OnSlotControlAckPacket.Parse(OnSlotControlAckPacket.Build(
                    new SlotControlResponse(slot, Enabled: true))).Enabled, Is.True);
            Assert.That(
                OnSlotControlAckPacket.Parse(OnSlotControlAckPacket.Build(
                    new SlotControlResponse(slot, Enabled: false))).Enabled, Is.False);
        });
    }

    [Test]
    public void TeamBattlePlacesSidesTogetherAndSinglesAsALadder()
    {
        // Sides win or lose together, so a team battle must not rank team-mates against
        // each other; a room with no sides keeps the 1st/2nd/3rd ladder.
        StageResult strong = Result(900, 0, 0, 900);
        StageResult weak = Result(100, 200, 600, 40);

        var placements = StagePlacementPolicy.AssignTeams(
        [
            ((byte)0, (byte)0, strong),
            ((byte)1, (byte)1, weak),
            ((byte)2, (byte)0, weak),
            ((byte)3, (byte)1, weak)
        ]);

        var ladder = StagePlacementPolicy.Assign(
        [
            ((byte)0, strong),
            ((byte)1, weak),
            ((byte)2, weak)
        ]);

        Assert.Multiple(() =>
        {
            // Team A carries the higher total, so both of its slots share the win.
            Assert.That(placements[0], Is.EqualTo(0));
            Assert.That(placements[2], Is.EqualTo(0), "team-mate wins with the side");
            Assert.That(placements[1], Is.EqualTo(1));
            Assert.That(placements[3], Is.EqualTo(1));
            // Without sides every player gets a distinct rank.
            Assert.That(ladder[0], Is.EqualTo(0));
            Assert.That(ladder.Values.Distinct().Count(), Is.EqualTo(3));
        });
    }

    [Test]
    public void SlotControlRequestIsFourBytesNotTwelve()
    {
        // Captured `53 00 7E 02`: id, a per-request control byte, slot 2. The China size of
        // 12 made the server over-read by 8 bytes and desync on every slot lock.
        Packet received = new(PacketMeta.SlotControlReq, [0x7E, 0x02]);
        SlotControlRequest request = SlotControlReqPacket.Parse(received);
        Assert.Multiple(() =>
        {
            Assert.That(PacketMeta.SlotControlReq.Size, Is.EqualTo(4));
            Assert.That(request.Slot, Is.EqualTo(2));
        });
    }

    [TestCase(0x9A, (byte)1)]
    [TestCase(0x8E, (byte)0)]
    public void TeamControlRequestIsTheCapturedFourByteRecord(
        byte control,
        byte expectedTeam)
    {
        // Live Korean captures: 58 00 9A 01 and 58 00 8E 00. The last byte is the
        // requested team; there is no eight-byte China tail.
        PacketFactory factory = new();
        factory.FillReadBuffer([0x58, 0x00, control, expectedTeam]);

        Packet packet = factory.ReadPackets().Single();
        TeamControlRequest request = TeamControlReqPacket.Parse(packet);

        Assert.Multiple(() =>
        {
            Assert.That(PacketMeta.TeamControlReq.Size, Is.EqualTo(4));
            Assert.That(request.Team, Is.EqualTo(expectedTeam));
        });
    }

    [Test]
    public void TeamControlAnnouncementIsFiveBytes()
    {
        RoomTeamUpdate expected = new(Slot: 3, Team: 0);
        Packet packet = OnTeamControlInfPacket.Build(expected);
        byte[] wire = new PacketFactory().Write(packet);

        Assert.Multiple(() =>
        {
            Assert.That(wire, Has.Length.EqualTo(5));
            Assert.That(wire[3], Is.EqualTo(3), "slot @3");
            Assert.That(wire[4], Is.EqualTo(0), "team @4");
            Assert.That(OnTeamControlInfPacket.Parse(packet), Is.EqualTo(expected));
        });
    }

    [Test]
    public void GameTypeAnnouncementIsFourBytes()
    {
        Packet packet = OnGameTypeInfPacket.Build(1);
        byte[] wire = new PacketFactory().Write(packet);

        Assert.Multiple(() =>
        {
            Assert.That(wire, Has.Length.EqualTo(4));
            Assert.That(wire[3], Is.EqualTo(1));
            Assert.That(OnGameTypeInfPacket.Parse(packet), Is.EqualTo(1));
        });
    }

    // The sides are A=0, B=1, C=2 and SINGLE=0xFF - proven by the capture `58 00 FB FF`,
    // where the 팀선택 panel's SINGLE button sends 0xFF. Zero is NOT neutral, which is why
    // a singles room used to put everyone on team A. In a team room the side comes from
    // the SLOT for everyone, host included: pinning the host to a fixed team put it on the
    // same side as slot 1, so a two-player room had both players on one team.
    //
    // There is no room-level team mode on the wire, so nobody starts on a side: the room
    // becomes a team room only once its host picks one.
    [TestCase((byte)0, true)]
    [TestCase((byte)1, false)]
    [TestCase((byte)2, false)]
    [TestCase((byte)3, false)]
    public void EveryOccupantStartsWithoutASide(byte slot, bool isHost)
    {
        Assert.That(
            LocalRoomProtocol.InitialTeam(slot, isHost),
            Is.EqualTo(LocalRoomProtocol.SingleTeam));
    }

    [Test]
    public void KickRequestIsSevenBytesCarryingAUserId()
    {
        // Captured `1D 00 8F 02 00 00 00`: id 0x1D, one uninitialised byte, then the target
        // user id as a u32 (Rival's roster id 2). The China size of 15 desynced by 8.
        Packet received = new(PacketMeta.UserInfoReq, [0x8F, 0x02, 0x00, 0x00, 0x00]);
        Assert.Multiple(() =>
        {
            Assert.That(PacketMeta.UserInfoReq.Size, Is.EqualTo(7));
            Assert.That(UserInfoReqPacket.Parse(received), Is.EqualTo(2u));
        });
    }

    [Test]
    public void LeavingStateWithdrawsTheBot()
    {
        // sub_4348B0 removes a joiner keyed by the connection value when the state byte is
        // 0x8E; a bot is cleared by re-sending its record in that state.
        Assert.That((byte)RoomMemberState.Leaving, Is.EqualTo(0x8E));

        RoomMemberInfo leave = RoomMemberInfo.CreateBot(
            Rival(), 1, 0xF001, 2) with { State = RoomMemberState.Leaving };
        Assert.DoesNotThrow(() =>
            new PacketFactory().Write(OnUpdateJoinerInfoInfPacket.Build(leave)));
    }

    [Test]
    public void HostAndGuestUseDistinctClientStates()
    {
        // sub_4348B0 sets the local "room owner" flag only when this byte is 0x8D.
        // A normal member is the preceding occupied state, 0x8C; using 0x8D for both
        // makes every connected player an owner and removes the guest ready button.
        Assert.Multiple(() =>
        {
            Assert.That((byte)RoomMemberState.Guest, Is.EqualTo(0x8C));
            Assert.That((byte)RoomMemberState.Host, Is.EqualTo(0x8D));
            Assert.That(RoomMemberState.Guest, Is.Not.EqualTo(RoomMemberState.Host));
        });
    }

    [Test]
    public void ReadyUpdateMatchesTheTenByteKoreanRecord()
    {
        RoomReadyUpdate expected = new(true, 0x014E, 2);
        Packet packet = OnReadyInfPacket.Build(expected);
        byte[] wire = new PacketFactory().Write(packet);

        Assert.Multiple(() =>
        {
            Assert.That(wire, Has.Length.EqualTo(10));
            Assert.That(wire[3], Is.EqualTo(1), "ready flag @3");
            Assert.That(BitConverter.ToUInt16(wire, 4), Is.EqualTo(0x014E),
                "connection id @4");
            Assert.That(BitConverter.ToUInt32(wire, 6), Is.EqualTo(2u), "user id @6");
            Assert.That(OnReadyInfPacket.Parse(packet), Is.EqualTo(expected));
        });
    }

    [Test]
    public void CapturedThreeByteReadyRequestFramesImmediately()
    {
        // Live Korean capture: 5D 00 B5. B5 is only the opaque control byte; there is no
        // China-style eight-byte tail. Advertising 11 bytes leaves this signal buffered
        // forever, so ReadyReqHandler is never reached.
        PacketFactory factory = new();
        factory.FillReadBuffer([0x5D, 0x00, 0xB5]);

        List<Packet> packets = factory.ReadPackets();

        Assert.Multiple(() =>
        {
            Assert.That(PacketMeta.ReadyReq.Size, Is.EqualTo(3));
            Assert.That(packets, Has.Count.EqualTo(1));
            Assert.That(packets[0].Id, Is.EqualTo(PacketId.ReadyReq));
            Assert.That(packets[0].Data, Is.EqualTo(new byte[] { 0xB5 }));
            Assert.DoesNotThrow(() => ReadyReqPacket.Parse(packets[0]));
        });
    }
}
