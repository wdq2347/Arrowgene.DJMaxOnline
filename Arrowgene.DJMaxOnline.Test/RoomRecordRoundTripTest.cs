using Arrowgene.DJMaxOnline.Server.Korea400;
using Arrowgene.DJMaxOnline.Server.Korea400.Packets;

namespace Arrowgene.DJMaxOnline.Test;

[TestFixture]
public class RoomRecordRoundTripTest
{
    private static RoomListEntry Entry(byte memberCount)
    {
        byte[] title = new byte[CreateRoomReqPacket.TitleSize];
        System.Text.Encoding.ASCII.GetBytes("ROOM").CopyTo(title.AsSpan());
        return new RoomListEntry(
            RoomIndex: 101,
            TitleField: title,
            TitlePadding: 0,
            Capacity: 8,
            MemberCount: memberCount,
            Unlocked: 1,
            Category: 1,
            LevelRestriction: 0,
            MatchMode: 4,          // the maximum the row renderer accepts
            EffectorFlag: 0,
            Premium: 1,
            State: RoomListState.Playing,
            DiscId: 102,
            Reserved46: 0,
            Difficulty: 1);
    }

    [Test]
    public void ToRecordMatchesTheWireAndSurvivesBuildRaw()
    {
        RoomListEntry entry = Entry(3);
        byte[] record = OnRoomInfoInfPacket.ToRecord(entry);

        // The record must be exactly the 48 bytes the client copies from wire offset 3.
        byte[] wire = new PacketFactory().Write(OnRoomInfoInfPacket.Build(entry));
        Assert.Multiple(() =>
        {
            Assert.That(record, Has.Length.EqualTo(OnRoomInfoInfPacket.RecordSize));
            Assert.That(record, Is.EqualTo(wire[3..(3 + OnRoomInfoInfPacket.RecordSize)]));
            // Known-good anchors: index@0, title@2, member count and state must survive.
            Assert.That(record[0] | (record[1] << 8), Is.EqualTo(101));
            Assert.That(System.Text.Encoding.ASCII.GetString(record, 2, 4), Is.EqualTo("ROOM"));
            RoomListEntry parsed = OnRoomInfoInfPacket.Parse(
                OnRoomInfoInfPacket.BuildRaw(record));
            Assert.That(parsed.MemberCount, Is.EqualTo(3));
            // The confirmed tail fields must land on their own offsets.
            Assert.That(record[37], Is.EqualTo(1));   // unlocked (0 draws the padlock)
            Assert.That(record[40], Is.EqualTo(4));   // sprite set, max legal value
            Assert.That(record[38], Is.EqualTo(1));   // normal/joinable category
            Assert.That(record[42], Is.EqualTo(1));   // premium skin
            Assert.That(record[44] | (record[45] << 8), Is.EqualTo(102)); // disc id -> song
            Assert.That(record[47], Is.EqualTo(1));   // difficulty badge
            Assert.That(parsed.DiscId, Is.EqualTo(102));
            Assert.That(parsed.Difficulty, Is.EqualTo(1));
        });
    }

    [TestCase((byte)0, (byte)0, TestName = "Free is solo")]
    [TestCase((byte)1, (byte)0, TestName = "Ranking is solo")]
    [TestCase((byte)2, (byte)1, TestName = "Score Battle is multiplayer")]
    [TestCase((byte)3, (byte)1, TestName = "Item Battle is multiplayer")]
    [TestCase((byte)4, (byte)0, TestName = "Course is solo")]
    public void BattleModesAdvertiseTheJoinableCategory(byte matchMode, byte expected)
    {
        // record+38 is what makes the client colour the row blue, list it under the battle
        // filter tab and allow a join. It must track the mode: only Score/Item Battle are
        // multiplayer, so a solo stage has to report 0 or the grid lies about it.
        byte[] title = new byte[CreateRoomReqPacket.TitleSize];
        System.Text.Encoding.ASCII.GetBytes("ROOM").CopyTo(title.AsSpan());
        RoomCreateRequest settings = new(
            TitleField: title,
            RoomType: 0,
            Capacity: LocalRoomProtocol.MaximumSlots,
            LevelRestriction: 0,
            GameType: 0,
            KeyMode: 7,
            MatchMode: matchMode,
            EffectorFlag: 0,
            PublicFlag: 1,
            PasswordField: new byte[CreateRoomReqPacket.PasswordTextSize]);

        byte[] record = OnRoomInfoInfPacket.ToRecord(
            RoomListEntry.Create(1, settings, memberCount: 0));

        Assert.Multiple(() =>
        {
            Assert.That(record[38], Is.EqualTo(expected));
            Assert.That(record[40], Is.EqualTo(matchMode));
            Assert.That(
                OnRoomInfoInfPacket.IsBattleMode(matchMode),
                Is.EqualTo(expected == 1));
        });
    }

    [Test]
    public void ChangeDiscAnnouncementEchoesTheDifficulty()
    {
        // Captured request: disc u32@3 = 7, settings u16@7 = 3 (MX). The announcement must
        // send that difficulty back - it is where the room UI and the other clients read
        // the chart's difficulty from. Replying 0 pinned every room to the EZ chart.
        // Body as received: control byte, then the structured payload from wire 3.
        Packet request = new(PacketMeta.ChangeDiscReq,
            [0x11, 0x07, 0x00, 0x00, 0x00, 0x03, 0x00]);
        ChangeDiscRequest parsed = ChangeDiscReqPacket.Parse(request);

        Assert.Multiple(() =>
        {
            Assert.That(parsed.DiscId, Is.EqualTo(7u));
            Assert.That(parsed.Difficulty, Is.EqualTo(3));
        });

        byte[] wire = new PacketFactory().Write(
            OnChangeDiscInfPacket.Build(parsed.DiscId, parsed.Difficulty));

        Assert.That(wire, Has.Length.EqualTo(9));
        Assert.Multiple(() =>
        {
            Assert.That(BitConverter.ToUInt32(wire, 3), Is.EqualTo(7u), "disc @3");
            Assert.That(wire[7], Is.EqualTo(3), "difficulty @7 must be echoed, not zeroed");
        });
    }

    [Test]
    public void ChangeDiscTreatsAnOutOfRangeDifficultyAsTheDefault()
    {
        // The client leaves the settings byte as uninitialised stack until the host
        // actually toggles difficulty, so anything past SC must not reach the chart
        // provider as a real difficulty.
        Packet request = new(PacketMeta.ChangeDiscReq,
            [0x11, 0x07, 0x00, 0x00, 0x00, 0x7E, 0x00]);
        Assert.That(ChangeDiscReqPacket.Parse(request).Difficulty, Is.EqualTo(0));
    }

    [Test]
    public void ChangeDiscAnnouncementPreservesTheRandomSentinelForRoomPeers()
    {
        const uint randomDiscIndex = 226;

        byte[] wire = new PacketFactory().Write(
            OnChangeDiscInfPacket.Build(randomDiscIndex, difficulty: 2));

        Assert.Multiple(() =>
        {
            Assert.That(wire, Has.Length.EqualTo(9));
            Assert.That(BitConverter.ToUInt32(wire, 3), Is.EqualTo(randomDiscIndex));
            Assert.That(wire[7], Is.EqualTo(2));
        });
    }

    [Test]
    public void RoomDescriptorSettingsLandOnTheBytesTheClientMaps()
    {
        // sub_434260 copies raw[36..41] to net+794268..794273, and the descriptor's title
        // field is 33 bytes (raw 3..35) - one more than CreateRoomReq's 32. That extra byte
        // is proven by sub_434570 (0x9D), which copies 33 title bytes from raw+3 and then
        // reads raw[36] into the SAME net+794268.
        //
        // Getting this wrong shifts every setting by one, which is exactly what happened:
        // GameType landed on the difficulty field (forcing EASY, since FREE is 0) and the
        // real GameType byte went out as zero, so item battle never enabled its features.
        // net+794270 is GameType and nothing else: OnGameTypeInf (0x5B, sub_434E40) writes
        // that same address, and Player::CreatePanel (sub_4226B0) reads it for item slots.
        RoomDescriptor room = new(
            TitleField: new byte[32],
            LevelRestriction: 0,   // FREE
            GameType: 1,
            KeyMode: 1,
            MatchMode: 3,          // item battle
            EffectorFlag: 0,
            PublicFlag: 1,
            State: 0,
            ServerState: new byte[OnRoomDescInfPacket.ServerStateSize],
            DifficultyRestriction: 3);

        byte[] wire = new PacketFactory().Write(OnRoomDescInfPacket.Build(room));

        Assert.That(wire, Has.Length.EqualTo(48));
        Assert.Multiple(() =>
        {
            Assert.That(wire[36], Is.EqualTo(0), "difficulty -> net+794268 (0 = FREE)");
            Assert.That(wire[37], Is.EqualTo(1), "key mode -> net+794269");
            Assert.That(wire[38], Is.EqualTo(1), "GAME TYPE -> net+794270");
            Assert.That(wire[39], Is.EqualTo(3), "match mode -> net+794271");
            Assert.That(wire[40], Is.EqualTo(0), "team mode -> net+794272");
            // net+794273 is the room FEE TYPE (FEETYPENAME: normal/premium/event).
            Assert.That(wire[41], Is.EqualTo(1), "room fee type -> net+794273");
        });
    }

    [Test]
    public void RoomDescriptorRoundTripsThroughItsOwnParser()
    {
        RoomDescriptor room = new(
            TitleField: new byte[32],
            LevelRestriction: 2,
            GameType: 1,
            KeyMode: 1,
            MatchMode: 3,
            EffectorFlag: 1,
            PublicFlag: 1,
            State: 0,
            ServerState: new byte[OnRoomDescInfPacket.ServerStateSize],
            DifficultyRestriction: 5);

        RoomDescriptor parsed = OnRoomDescInfPacket.Parse(OnRoomDescInfPacket.Build(room));

        Assert.Multiple(() =>
        {
            Assert.That(parsed.LevelRestriction, Is.EqualTo(2));
            Assert.That(parsed.GameType, Is.EqualTo(1));
            Assert.That(parsed.KeyMode, Is.EqualTo(1));
            Assert.That(parsed.MatchMode, Is.EqualTo(3));
            Assert.That(parsed.EffectorFlag, Is.EqualTo(1));
            Assert.That(parsed.PublicFlag, Is.EqualTo(1));
        });
    }

    [Test]
    public void ARoomIsOpenUnlessAPasswordWasActuallyTyped()
    {
        // The client writes the password separately from the flag bytes (sub_433E10 puts
        // up to 10 chars + terminator at wire 43). Deriving "public" from the room-type
        // flag instead made a room created without premium report itself locked: the lobby
        // drew a padlock and JoinRoom demanded a password the joiner could not supply.
        static RoomCreateRequest Room(byte publicFlag, string password) => new(
            new byte[CreateRoomReqPacket.TitleSize],
            RoomType: 0,
            Capacity: 6,
            LevelRestriction: 0,
            GameType: 1,
            KeyMode: 1,
            MatchMode: LocalRoomProtocol.ItemBattleMatchMode,
            EffectorFlag: 0,
            PublicFlag: publicFlag,
            PasswordField: Pad(password));

        Assert.Multiple(() =>
        {
            // No password: open, whatever the room-type flag says.
            Assert.That(Room(0, "").IsPublic, Is.True, "non-premium room must not lock");
            Assert.That(Room(1, "").IsPublic, Is.True);
            // A real password locks it, again regardless of the flag.
            Assert.That(Room(1, "hunter2").IsPublic, Is.False);
            Assert.That(Room(0, "hunter2").HasPassword, Is.True);
        });

        static byte[] Pad(string value)
        {
            byte[] field = new byte[CreateRoomReqPacket.PasswordTextSize];
            System.Text.Encoding.ASCII.GetBytes(value).CopyTo(field, 0);
            return field;
        }
    }

    [Test]
    public void CreateAckSettingsLandOnTheAddressesTheHostMemcpysThemTo()
    {
        // sub_433F00 is nothing but `qmemcpy(net+794231, raw+4, 0x30)`, so the HOST's whole
        // room state is decided by absolute wire offsets:
        //   41 -> 794268 level   42 -> 794269 key   43 -> 794270 GAME TYPE
        //   44 -> 794271 match   45 -> 794272 team  46 -> 794273 public
        // The block used to start a byte early behind a 32-byte title, so the host alone
        // got levelRestriction <- gameType and gameType <- 0. Joiners were fine because
        // they read the same fields from OnRoomDescInf - which is exactly why item battle
        // mistreated only the host: sub_4226B0 enables the item slots on net+794270 == 1.
        RoomCreateRequest room = new(
            new byte[CreateRoomReqPacket.TitleSize],
            RoomType: 0,
            Capacity: 6,
            LevelRestriction: 3,
            GameType: 1,
            KeyMode: 1,
            MatchMode: LocalRoomProtocol.ItemBattleMatchMode,
            EffectorFlag: LocalRoomProtocol.EffectsAllowed,
            PublicFlag: 1,
            PasswordField: new byte[CreateRoomReqPacket.PasswordTextSize],
            DifficultyRestriction: 4);

        byte[] wire = new PacketFactory().Write(OnCreateRoomAckPacket.Build(
            new CreateRoomResponse(CreateRoomResult.Success, 1, room)));

        Assert.Multiple(() =>
        {
            Assert.That(wire, Has.Length.EqualTo(52), "the memcpy runs to wire 51");
            Assert.That(wire[41], Is.EqualTo(3), "level restriction -> 794268");
            Assert.That(wire[42], Is.EqualTo(1), "key mode -> 794269");
            Assert.That(wire[43], Is.EqualTo(1), "GAME TYPE -> 794270");
            Assert.That(wire[44], Is.EqualTo(LocalRoomProtocol.ItemBattleMatchMode),
                "match mode -> 794271");
            Assert.That(wire[45], Is.EqualTo(LocalRoomProtocol.EffectsAllowed),
                "effector use -> 794272");
            // net+794273 is the room FEE TYPE (FEETYPENAME: normal/premium/event).
            Assert.That(wire[46], Is.EqualTo(1), "room fee type -> 794273");
        });
    }

    [Test]
    public void ClosedSlotsLowerTheRowsCapacityDenominator()
    {
        // sub_441D56 at 0x441E28 pushes record+35 then record+36 then the LOBBYMSG28
        // format ("%u/%u"); cdecl pushes right-to-left, so the varargs arrive as
        // (record+36, record+35) - the headcount over the CAPACITY. Four slots closed in a
        // six-slot room must therefore read "x/2", not "x/6".
        byte[] title = new byte[CreateRoomReqPacket.TitleSize];
        System.Text.Encoding.ASCII.GetBytes("ROOM").CopyTo(title.AsSpan());
        RoomCreateRequest settings = new(
            TitleField: title,
            RoomType: 0,
            Capacity: LocalRoomProtocol.MaximumSlots,
            LevelRestriction: 0,
            GameType: 1,
            KeyMode: 1,
            MatchMode: LocalRoomProtocol.ScoreBattleMatchMode,
            EffectorFlag: 0,
            PublicFlag: 1,
            PasswordField: new byte[CreateRoomReqPacket.PasswordTextSize]);

        byte[] closed = OnRoomInfoInfPacket.ToRecord(
            RoomListEntry.Create(1, settings, memberCount: 1, openSlots: 2));
        byte[] untouched = OnRoomInfoInfPacket.ToRecord(
            RoomListEntry.Create(1, settings, memberCount: 1));

        Assert.Multiple(() =>
        {
            Assert.That(closed[35], Is.EqualTo(2), "denominator tracks the OPEN slots");
            Assert.That(closed[36], Is.EqualTo(1), "numerator is the headcount");
            // Without an explicit count the row still advertises the created capacity.
            Assert.That(untouched[35], Is.EqualTo(LocalRoomProtocol.MaximumSlots));
        });
    }

    [Test]
    public void PasswordMatchesComparesTextNotPaddedBytes()
    {
        // The create request stores a 16-byte field; JoinRoomReq's credential is 12. A raw
        // (even truncated) byte compare therefore rejected a CORRECT password over the
        // size difference and trailing junk, so a passworded room could never be entered.
        static RoomCreateRequest Room(string password)
        {
            byte[] field = new byte[CreateRoomReqPacket.PasswordTextSize];
            System.Text.Encoding.ASCII.GetBytes(password).CopyTo(field, 0);
            return new RoomCreateRequest(
                new byte[CreateRoomReqPacket.TitleSize],
                RoomType: 0,
                Capacity: 6,
                LevelRestriction: 0,
                GameType: 1,
                KeyMode: 1,
                MatchMode: LocalRoomProtocol.ScoreBattleMatchMode,
                EffectorFlag: 0,
                PublicFlag: 1,
                PasswordField: field);
        }

        static byte[] Credential(string password)
        {
            byte[] field = new byte[JoinRoomReqPacket.CredentialSize];
            System.Text.Encoding.ASCII.GetBytes(password).CopyTo(field, 0);
            return field;
        }

        Assert.Multiple(() =>
        {
            Assert.That(Room("hunter2").PasswordMatches(Credential("hunter2")), Is.True,
                "the shorter credential must still match");
            Assert.That(Room("hunter2").PasswordMatches(Credential("wrong")), Is.False);
            // An open room admits anyone, credential or not.
            Assert.That(Room("").PasswordMatches(Credential("")), Is.True);
            Assert.That(Room("").PasswordMatches(Credential("anything")), Is.True);
            // Trailing junk after the terminator must not matter.
            byte[] noisy = Credential("hunter2");
            noisy[^1] = 0xCC;
            Assert.That(Room("hunter2").PasswordMatches(noisy), Is.True);
        });
    }

    [Test]
    public void GameTypeDecidesTheResultLayoutAsWellAsTheItemSlots()
    {
        // sub_4285BF() == (net+794270 == 1). sub_44F48D calls it at 0x45076d and
        // `jz loc_4508B9` takes the PLACEMENT path (frame = struct+46 + 1, 1st..6th);
        // non-zero falls through to the win/lose clip. net+794270 is descriptor byte 38,
        // and it is ALSO what Player::CreatePanel tests to enable the item slots - so
        // item battle needs 1 and score battle must not have it.
        static byte[] Descriptor(byte gameType)
        {
            RoomDescriptor room = new(
                TitleField: new byte[32],
                LevelRestriction: 0,
                GameType: gameType,
                KeyMode: 0,
                MatchMode: LocalRoomProtocol.ScoreBattleMatchMode,
                EffectorFlag: 0,
                PublicFlag: 1,
                State: 0,
                ServerState: new byte[OnRoomDescInfPacket.ServerStateSize]);
            return new PacketFactory().Write(OnRoomDescInfPacket.Build(room));
        }

        Assert.Multiple(() =>
        {
            // Byte 38 is the one sub_4285BF reads.
            Assert.That(Descriptor(0)[38], Is.Zero, "no sides -> placement ladder");
            Assert.That(Descriptor(1)[38], Is.EqualTo(1), "sides -> win/lose");
            // 0x5B is the only live writer of net+794270, so a team change must be able
            // to move it after the room already exists.
            byte[] announce = new PacketFactory().Write(OnGameTypeInfPacket.Build(1));
            Assert.That(announce, Has.Length.EqualTo(4));
            Assert.That(announce[3], Is.EqualTo(1));
        });
    }

    [Test]
    public void JoinRefusalCodesMapToTheMessagesTheClientShows()
    {
        // sub_441523 switches on the result byte at wire+5 and shows the matching
        // TextStock line, so the code IS the explanation the player reads. Anything the
        // switch does not name falls through to a notice built from an UNINITIALISED
        // stack buffer, i.e. garbage text - so every refusal must use a listed code.
        Assert.Multiple(() =>
        {
            Assert.That((byte)JoinRoomResult.Success, Is.EqualTo(122));
            Assert.That((byte)JoinRoomResult.RoomFull, Is.EqualTo(123));       // LOBBYMSG13
            Assert.That((byte)JoinRoomResult.GameInProgress, Is.EqualTo(124)); // LOBBYMSG14
            Assert.That((byte)JoinRoomResult.WrongPassword, Is.EqualTo(125));  // LOBBYMSG12
            Assert.That((byte)JoinRoomResult.SoloStage, Is.EqualTo(126));      // LOBBYMSG15
            Assert.That((byte)JoinRoomResult.RoomGone, Is.EqualTo(130));       // LOBBYMSG16
            Assert.That((byte)JoinRoomResult.PremiumOnly, Is.EqualTo(131));    // LOBBYMSG17
            // The old default was 0, which the switch does not name.
            Assert.That((byte)JoinRoomResult.Rejected, Is.Not.Zero);
        });

        byte[] wire = new PacketFactory().Write(OnJoinRoomAckPacket.Build(
            new JoinRoomResponse(7, JoinRoomResult.RoomFull, Slot: 0)));
        Assert.Multiple(() =>
        {
            Assert.That(wire, Has.Length.EqualTo(7));
            Assert.That(BitConverter.ToUInt16(wire, 3), Is.EqualTo(7), "room index @3");
            Assert.That(wire[5], Is.EqualTo(123), "the reason the client reads");
        });
    }

    [Test]
    public void TheEffectorChoiceReachesTheClientUnchanged()
    {
        // CreateRoomReq wire 41 is the create dialog's effector checkbox (dword_55D25C):
        // every sub_433E10 caller passes `!(matchMode == 3 || dword_55D25C == 1)`. It must
        // be forwarded verbatim - pinning it to a constant made every room NO EFFECT no
        // matter which way the dialog was set.
        static RoomCreateRequest Room(byte effectorFlag) => new(
            new byte[CreateRoomReqPacket.TitleSize],
            RoomType: 0,
            Capacity: 6,
            LevelRestriction: 0,
            GameType: 1,
            KeyMode: 1,
            MatchMode: LocalRoomProtocol.ScoreBattleMatchMode,
            EffectorFlag: effectorFlag,
            PublicFlag: 1,
            PasswordField: new byte[CreateRoomReqPacket.PasswordTextSize]);

        Assert.Multiple(() =>
        {
            foreach (byte flag in new byte[] { 0, 1 })
            {
                byte[] descriptor = new PacketFactory().Write(
                    OnRoomDescInfPacket.Build(RoomDescriptor.FromCreate(Room(flag))));
                byte[] ack = new PacketFactory().Write(OnCreateRoomAckPacket.Build(
                    new CreateRoomResponse(CreateRoomResult.Success, 1, Room(flag))));
                Assert.That(descriptor[40], Is.EqualTo(flag), "descriptor +40 -> 794272");
                Assert.That(ack[45], Is.EqualTo(flag), "create ack +45 -> 794272");
            }

            // 0 is the ALLOWED case: sub_454EAB greys the modifier panel via sub_453277(0)
            // when raw[49] != 0, and sub_453277's a1==0 branch resets all four modifier
            // slots to entry 0 before disabling controls 6..13.
            Assert.That(
                LocalRoomProtocol.EffectsEnabled(LocalRoomProtocol.EffectsAllowed), Is.True);
            Assert.That(
                LocalRoomProtocol.EffectsEnabled(LocalRoomProtocol.EffectsDisabled),
                Is.False);
        });
    }

    [Test]
    public void TheHostsSideDecidesEveryOtherSlot()
    {
        // Nothing on the wire carries a room-level team mode (0x9C is title, level,
        // password, match mode and effector), so a room becomes a team room exactly when
        // its host picks a side. Everyone else is then seated alternately FROM the host -
        // pinning the host to a fixed side put it on the same team as slot 1.
        Assert.Multiple(() =>
        {
            // Host on A in slot 0.
            Assert.That(LocalRoomProtocol.TeamForSlot(0, 0, 1), Is.EqualTo(1));
            Assert.That(LocalRoomProtocol.TeamForSlot(0, 0, 2), Is.EqualTo(0));
            Assert.That(LocalRoomProtocol.TeamForSlot(0, 0, 3), Is.EqualTo(1));
            // Host on B: the slots opposite it flip with it.
            Assert.That(LocalRoomProtocol.TeamForSlot(1, 0, 1), Is.EqualTo(0));
            Assert.That(LocalRoomProtocol.TeamForSlot(1, 0, 2), Is.EqualTo(1));
            // A host in a later slot still opposes the slot next to it.
            Assert.That(LocalRoomProtocol.TeamForSlot(0, 3, 4), Is.EqualTo(1));
            Assert.That(LocalRoomProtocol.TeamForSlot(0, 3, 2), Is.EqualTo(1));
            // Until someone picks, nobody has a side.
            Assert.That(LocalRoomProtocol.InitialTeam(slot: 0, isHost: true),
                Is.EqualTo(LocalRoomProtocol.SingleTeam));
            Assert.That(LocalRoomProtocol.InitialTeam(slot: 2, isHost: false),
                Is.EqualTo(LocalRoomProtocol.SingleTeam));
        });
    }

    [Test]
    public void TeamPanelValuesAreAbcAndSingleIsNotZero()
    {
        // Captured: `58 00 FB FF` - TeamControlReq, control 0xFB, team 0xFF for SINGLE.
        // Rejecting 0xFF as out-of-range meant pressing SINGLE was dropped and NOTHING was
        // broadcast, so the player who pressed it never saw their own change.
        Assert.Multiple(() =>
        {
            Assert.That(LocalRoomProtocol.SingleTeam, Is.EqualTo(0xFF));
            Assert.That(LocalRoomProtocol.IsValidTeam(LocalRoomProtocol.SingleTeam), Is.True);
            // A, B and C.
            Assert.That(LocalRoomProtocol.IsValidTeam(0), Is.True);
            Assert.That(LocalRoomProtocol.IsValidTeam(1), Is.True);
            Assert.That(LocalRoomProtocol.IsValidTeam(2), Is.True);
            Assert.That(LocalRoomProtocol.IsValidTeam(3), Is.False);
            // Zero is team A, not "no team" - treating it as neutral put everyone in a
            // singles room on team A.
            Assert.That(LocalRoomProtocol.FirstTeam, Is.EqualTo(0));
        });
    }

    [Test]
    public void RoomChangeTailExposesTheConfirmedMutableSettings()
    {
        // The client's own sender (sub_4344E0) writes: title[33] at wire 3, level at 36,
        // password[11] at 37, match mode at 48, team mode at 49 - 50 bytes total. Settings
        // start at wire 35, so settings[1]=36, settings[13]=48, settings[14]=49. There is
        // NO game-type field; reading settings[1] as one fed the level byte into
        // net+794270 and pinned the room owner to singles.
        byte[] settings = new byte[RoomChangeInfoReqPacket.SettingsSize];
        settings[0] = 0;  // title terminator
        settings[1] = 3;  // level restriction
        settings[13] = LocalRoomProtocol.ItemBattleMatchMode;
        settings[14] = 1; // teams
        RoomChangeRequest request = new(
            new byte[CreateRoomReqPacket.TitleSize], settings);

        Assert.Multiple(() =>
        {
            Assert.That(request.LevelRestriction, Is.EqualTo(3));
            Assert.That(request.MatchMode, Is.EqualTo(LocalRoomProtocol.ItemBattleMatchMode));
            Assert.That(request.EffectorFlag, Is.EqualTo(1));
        });
    }
}


