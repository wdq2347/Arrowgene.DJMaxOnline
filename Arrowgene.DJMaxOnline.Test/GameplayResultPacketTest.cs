using System.Buffers.Binary;
using Arrowgene.DJMaxOnline.Server;
using Arrowgene.DJMaxOnline.Server.Packets;
using Arrowgene.DJMaxOnline.Server.Protocol;

namespace Arrowgene.DJMaxOnline.Test;

public class GameplayResultPacketTest
{
    [Test]
    public void GameInfoModeByteStaysZeroForOrdinaryPlay()
    {
        // payload+0 reaches net+794279 and is the GAME MODE. Writing 1 was TESTED in game
        // and started DJ MISSION MATCH (survival) - it is not a result-screen switch, and
        // the "mission clear" result component followed from the mode, not the other way
        // round. A normal battle must leave it at 0.
        byte[] bytes = new byte[GameplayProtocol.GameInfoHeaderSize];
        GameInfoPayload payload = new(DiscId: 1, ChartType: 0, Bytes: bytes);

        Assert.Multiple(() =>
        {
            Assert.That(GameInfoPayload.GameModeOffset, Is.Zero);
            Assert.That(GameInfoPayload.MissionMatchMode, Is.EqualTo(1));
            Assert.That(payload.Bytes[GameInfoPayload.GameModeOffset], Is.Zero,
                "ordinary play must not request a mission");
        });
    }

    [Test]
    public void ServerAuthoredResultFieldsLandWhereTheScreenReadsThem()
    {
        // sub_46E94C copies these into the result scene: +39 gates the level-up notice
        // (via scene+56 -> sub_46CF43), +42 is the gained-MAX figure (scene+164), and +44
        // raises the NEW RECORD banner (scene+68 -> sub_46D099). None of the three can
        // come from the client's own report, so a regression here silently removes a
        // display rather than breaking anything loudly.
        StageResult result = MinimalResult();
        byte[] wire = new PacketFactory().Write(OnStageResultExInfPacket.Build(
            slot: 0, result, new StageAward(Money: 421, LeveledUp: true, NewRecord: true)));

        // payload[0] is the slot byte, so the record itself starts one byte later.
        const int record = 3 + 1;
        Assert.Multiple(() =>
        {
            Assert.That(wire, Has.Length.EqualTo(51));
            Assert.That(wire[record + 39], Is.EqualTo(1), "level-up notice");
            Assert.That(
                BitConverter.ToUInt16(wire, record + 42), Is.EqualTo(421), "gained MAX");
            Assert.That(
                BitConverter.ToUInt16(wire, record + 44), Is.EqualTo(1), "NEW RECORD");
        });
    }

    [Test]
    public void NoAwardLeavesEveryServerAuthoredFieldClear()
    {
        byte[] wire = new PacketFactory().Write(
            OnStageResultExInfPacket.Build(slot: 0, MinimalResult()));
        const int record = 3 + 1;
        Assert.Multiple(() =>
        {
            Assert.That(wire[record + 39], Is.EqualTo(0));
            Assert.That(BitConverter.ToUInt16(wire, record + 42), Is.EqualTo(0));
            Assert.That(BitConverter.ToUInt16(wire, record + 44), Is.EqualTo(0));
        });
    }

    [Test]
    public void MiscPropertyPushOverwritesTheLoginStatsBlockExactly()
    {
        // sub_435D10 does memcpy(net+794185, packet+9, 40) - straight over login-block
        // +89..+128. So this packet's payload has to be the same ten words OnLogInAck
        // writes there, in the same order, or it corrupts the SCORE tab.
        LocalPlayerProgress progress = new()
        {
            FreemodeBest5Key = 111, RankingBest5Key = 222, MaxCombo = 333,
            HighestAccuracy = 98.7f, AverageAccuracy = 65.4321f,
            RankingBest7Key = 444, Cash = 555, FreemodeBest7Key = 666
        };

        uint[] block = UserStatisticsBlock.Build(progress);
        byte[] wire = new PacketFactory().Write(
            OnUpdateUserPropertyMiscInfPacket.Build(userId: 7, block));

        Assert.That(wire, Has.Length.EqualTo(49));
        Assert.Multiple(() =>
        {
            Assert.That(block, Has.Length.EqualTo(UserStatisticsBlock.Length));
            Assert.That(BitConverter.ToUInt32(wire, 3), Is.EqualTo(7u), "user id");
            // The 40-byte block starts at wire+9.
            Assert.That(BitConverter.ToUInt32(wire, 9), Is.EqualTo(111u), "+89 best 5key");
            Assert.That(BitConverter.ToUInt32(wire, 17), Is.EqualTo(333u), "+97 max combo");
            Assert.That(BitConverter.ToUInt32(wire, 21), Is.EqualTo(9870u), "+101 acc x100");
            Assert.That(BitConverter.ToUInt32(wire, 25), Is.EqualTo(6543u), "+105 acc x100");
            Assert.That(BitConverter.ToUInt32(wire, 33), Is.EqualTo(555u), "+113 cash");
            Assert.That(BitConverter.ToUInt32(wire, 41), Is.EqualTo(666u), "+121 best 7key");
        });
    }

    [Test]
    public void CourseTotalsReplaceTheStageFiguresInTheFinalRecord()
    {
        // The client keeps a 3-slot rolling buffer of stage records (sub_44EDCE advances
        // the index as (n+1)%3) and its total-result scene sub_4963E3 reads exactly one of
        // them - so a course's totals cannot be computed client-side and must arrive
        // pre-summed in the record that closes the course.
        StageResult stage = MinimalResult() with { ResultState = 2 };
        StageTotals totals = new(
            NotesHit: 900, Breaks: 7, MaxCombo: 480,
            Score: 600000, BonusScore: 21000, Accuracy: 91.5f);

        byte[] wire = new PacketFactory().Write(OnStageResultExInfPacket.Build(
            slot: 0, stage, StageAward.None, totals));

        const int record = 3 + 1;   // payload[0] is the slot byte
        Assert.That(wire, Has.Length.EqualTo(51));
        Assert.Multiple(() =>
        {
            Assert.That(BitConverter.ToUInt16(wire, record + 7), Is.EqualTo(900), "MAX");
            Assert.That(BitConverter.ToUInt16(wire, record + 9), Is.EqualTo(7), "BREAK");
            Assert.That(BitConverter.ToUInt16(wire, record + 11), Is.EqualTo(480), "combo");
            Assert.That(BitConverter.ToUInt32(wire, record + 17), Is.EqualTo(480u));
            Assert.That(BitConverter.ToUInt32(wire, record + 26), Is.EqualTo(600000u));
            Assert.That(BitConverter.ToUInt32(wire, record + 30), Is.EqualTo(21000u));
            Assert.That(
                BitConverter.ToSingle(wire, record + 34), Is.EqualTo(91.5f).Within(0.001f));
            Assert.That(
                (StageResultRank)wire[record + 38],
                Is.EqualTo(StageResultAward.Evaluate(91.5f, failed: false).Rank),
                "the medal must use the accumulated course accuracy too");
        });
    }

    [Test]
    public void WithoutTotalsTheRecordStillDescribesItsOwnStage()
    {
        StageResult stage = MinimalResult();
        byte[] wire = new PacketFactory().Write(
            OnStageResultExInfPacket.Build(slot: 0, stage));
        const int record = 3 + 1;
        Assert.That(
            BitConverter.ToUInt16(wire, record + 7), Is.EqualTo(stage.NotesHit));
        Assert.That(
            BitConverter.ToUInt32(wire, record + 17), Is.EqualTo(stage.MaxCombo));
    }

    private static StageResult MinimalResult() => new(
        SessionToken: 0,
        ClientFlags: 0,
        TotalNotes: 100,
        Judgments: new ushort[StageResult.JudgmentCount],
        Gauge: 50f,
        EncodedCombo: 0,
        CurrentCombo: 0,
        MaxCombo: 10,
        AuxiliaryValue: 0,
        ResultState: 1,
        // The client's own tail byte must no longer reach struct+39.
        Tail: 1);

    [Test]
    public void PlayStateStripsRequestWrapperBeforeBuildingRelay()
    {
        byte[] state = Convert.FromHexString("00009643B50ADD4760600B");
        Packet request = DjMaxPacketBuilder.Fixed(
                PacketMeta.PlayStateInf, ProtocolPadding.Unused)
            .WritePadding(GameplayProtocol.ReservedSize, 0xA5)
            .WriteBytes(state)
            .Build();

        PlayState parsed = PlayStateInfPacket.Parse(request);
        byte[] relay = new PacketFactory().Write(
            OnPlayStateInfPacket.Build(2, parsed));

        Assert.Multiple(() =>
        {
            Assert.That(parsed.Data, Is.EqualTo(state));
            Assert.That(relay, Has.Length.EqualTo(15));
            Assert.That(relay[3], Is.EqualTo(2));
            Assert.That(relay.AsSpan(4).ToArray(), Is.EqualTo(state));
        });
    }

    [Test]
    public void ResultPacketWritesPlacementAndTranslatesClientFailState()
    {
        StageResult failedClientReport = MinimalResult();
        StageResult clearedClientReport = MinimalResult() with { ResultState = 2 };

        byte[] failed = new PacketFactory().Write(OnStageResultExInfPacket.Build(
            slot: 0, failedClientReport, placement: 1));
        byte[] cleared = new PacketFactory().Write(OnStageResultExInfPacket.Build(
            slot: 1, clearedClientReport, placement: 0));
        const int record = 3 + 1;

        Assert.Multiple(() =>
        {
            Assert.That(failed[record + 6], Is.EqualTo(1), "second place");
            Assert.That(failed[record + 46], Is.EqualTo(StageResultState.Failed));
            Assert.That(cleared[record + 6], Is.EqualTo(0), "first place");
            Assert.That(cleared[record + 46], Is.EqualTo(StageResultState.Cleared));
        });
    }

    [Test]
    public void BattleResultUsesBattleStateAndUniqueFailureAwarePlacements()
    {
        // Failure dominates ordering: even an artificially enormous combo must not let
        // a dead player beat somebody who survived the song.
        StageResult survivor = MinimalResult() with
        {
            ResultState = 2,
            MaxCombo = 10
        };
        StageResult failed = MinimalResult() with
        {
            ResultState = 1,
            MaxCombo = 10_000
        };
        IReadOnlyDictionary<byte, byte> placements = StagePlacementPolicy.Assign(
            [(Slot: (byte)4, Result: failed), (Slot: (byte)2, Result: survivor)]);

        byte[] wire = new PacketFactory().Write(OnStageResultExInfPacket.Build(
            slot: 4,
            failed,
            placement: placements[4],
            resultStateOverride: StageResultState.Battle));
        const int record = 3 + 1;

        Assert.Multiple(() =>
        {
            Assert.That(placements[2], Is.EqualTo(0));
            Assert.That(placements[4], Is.EqualTo(1));
            Assert.That(placements.Values, Is.EquivalentTo(new byte[] { 0, 1 }));
            Assert.That(wire[record + 6], Is.EqualTo(1));
            Assert.That(wire[record + 46], Is.EqualTo(StageResultState.Battle));
        });
    }

    [Test]
    public void PlayStateDecodesSongLocalMaximumComboWithLoginKey()
    {
        // Exact 11-byte state from blade_stream_05. Its session key is 0x72F8;
        // encoded word 0x72E8 therefore reports a song-local maximum combo of 16.
        const ushort loginKey = 0x72F8;
        byte[] state = Convert.FromHexString("00001143ABB5D545E8725C");

        PlayState playState = new(state);

        Assert.Multiple(() =>
        {
            Assert.That(playState.LifeGauge, Is.EqualTo(145.0f));
            Assert.That(playState.BaseScore, Is.EqualTo(6_838.7085f).Within(0.001f));
            Assert.That(playState.EncodedMaxCombo, Is.EqualTo(0x72E8));
            Assert.That(playState.DecodeMaxCombo(loginKey), Is.EqualTo(16));
        });
    }

    [Test]
    public void JudgmentKeyUsesLoginSessionSeedTail()
    {
        // Exact session-seed tail from blade_stream_05: sub_430A10 returns
        // low16(0x1C1E0212 + 0x70E6) = 0x72F8, matching every encrypted
        // judgment word in that capture's 0x6F result packet.
        byte[] sessionSeed = new byte[OnConnectAckPacket.SeedSize];
        BinaryPrimitives.WriteUInt32LittleEndian(sessionSeed.AsSpan(24), 0x1C1E0212);
        BinaryPrimitives.WriteUInt16LittleEndian(sessionSeed.AsSpan(28), 0x70E6);

        ushort key = StageResultInfPacket.DeriveJudgmentKey(sessionSeed);

        Assert.That(key, Is.EqualTo(0x72F8));
    }

    [Test]
    public void JudgmentKeyAcceptsJapaneseThirtyByteSessionSeed()
    {
        // Exact seed from the 08:56:32 JP OnConnectAck in log.txt. Its tail gives
        // low16(0x1E29BF6F + 0x067A) = 0xC5E9, matching both PlayState and 0x6F.
        byte[] sessionSeed = Convert.FromHexString(
            "0603BBE0D91D686C3835E831216A20DAF270C940AEACE5CB6FBF291E7A06");

        Assert.Multiple(() =>
        {
            Assert.That(sessionSeed, Has.Length.EqualTo(LogInReqPacket.SeedSize));
            Assert.That(StageResultInfPacket.DeriveJudgmentKey(sessionSeed),
                Is.EqualTo(0xC5E9));
        });
    }

    [Test]
    public void OneBreakSurvivesClientMaskAndResultEcho()
    {
        const ushort key = 0x72F8;
        ushort[] judgments = [0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 124];
        DjMaxPacketBuilder request = DjMaxPacketBuilder.Fixed(
                PacketMeta.Test00, ProtocolPadding.Unused)
            .WriteUInt32(0x40)
            .WriteByte(0)
            .WriteByte(0)
            .WriteUInt16(125);
        foreach (ushort judgment in judgments)
        {
            request.WriteUInt16((ushort)(judgment ^ key));
        }

        Packet inbound = request
            .WriteUInt32(BitConverter.SingleToUInt32Bits(99f))
            .WriteUInt16((ushort)(124 ^ key))
            .WriteUInt32(0)
            .WriteUInt32(124)
            .WriteUInt16(0)
            .WriteByte(2)
            .WriteByte(0)
            .WriteUInt32(0)
            .Build();

        StageResult result = StageResultInfPacket.Parse(inbound, key);
        ushort recoveredKey = StageResultInfPacket.RecoverJudgmentKey(inbound);
        byte[] wire = new PacketFactory().Write(
            OnStageResultExInfPacket.Build(0, result));

        Assert.Multiple(() =>
        {
            Assert.That(recoveredKey, Is.EqualTo(key));
            Assert.That(result.Breaks, Is.EqualTo(1));
            Assert.That(result.NotesHit, Is.EqualTo(124));
            Assert.That(result.MaxCombo, Is.EqualTo(124));
            Assert.That(result.Accuracy, Is.EqualTo(99.2f).Within(0.0001f));
            Assert.That(result.Rank, Is.EqualTo(StageResultRank.BronzeMax));
            Assert.That(result.BonusScore, Is.EqualTo(10_000));
            Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(11)),
                Is.EqualTo(124));
            Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(13)),
                Is.EqualTo(1));
            Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(15)),
                Is.EqualTo(124));
        });
    }

    [Test]
    public void StageResultDecodesJudgmentsAndBuildsLocalDisplayRecord()
    {
        const ushort key = 0x72F8;
        ushort[] judgments = [0, 4, 3, 2, 0, 0, 1, 0, 1, 2, 5, 16, 245];
        DjMaxPacketBuilder request = DjMaxPacketBuilder.Fixed(
                PacketMeta.Test00, ProtocolPadding.Unused)
            .WriteUInt32(0x40)
            .WriteByte(0)
            .WriteByte(0)
            .WriteUInt16(279);
        foreach (ushort judgment in judgments)
        {
            request.WriteUInt16((ushort)(judgment ^ key));
        }
        Packet inbound = request
            .WriteUInt32(BitConverter.SingleToUInt32Bits(94.4827576f))
            .WriteUInt16(0x726D)
            .WriteUInt32(9)
            .WriteUInt32(149)
            .WriteUInt16(15)
            .WriteByte(2)
            .WriteByte(0)
            .WriteUInt32(0xDEADBEEF)
            .Build();

        StageResult result = StageResultInfPacket.Parse(inbound, key);
        Packet outbound = OnStageResultExInfPacket.Build(0, result);
        byte[] wire = new PacketFactory().Write(outbound);

        Assert.Multiple(() =>
        {
            Assert.That(result.Judgments, Is.EqualTo(judgments));
            Assert.That(result.Breaks, Is.EqualTo(4));
            Assert.That(result.NotesHit, Is.EqualTo(275));
            Assert.That(result.CurrentCombo, Is.EqualTo(9));
            Assert.That(result.MaxCombo, Is.EqualTo(149));
            Assert.That(result.Accuracy, Is.EqualTo(95.35125f).Within(0.0001f));
            Assert.That(result.Failed, Is.False);
            // 95.35% is below Korea's bronze-disc floor of 96.01, so it grades rather
            // than medals. On China's table this same play was a silver disc.
            Assert.That(result.Rank, Is.EqualTo(StageResultRank.BPlus));
            Assert.That(result.BonusScore, Is.EqualTo(0));

            Assert.That(wire, Has.Length.EqualTo(51));
            Assert.That(wire[3], Is.EqualTo(0));
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(4)),
                Is.EqualTo(0x40));
            Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(11)),
                Is.EqualTo(275));
            Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(13)),
                Is.EqualTo(4));
            Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(wire.AsSpan(15)),
                Is.EqualTo(149));
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(17)),
                Is.EqualTo(9));
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(21)),
                Is.EqualTo(149));
            Assert.That(BitConverter.UInt32BitsToSingle(
                    BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(25))),
                Is.EqualTo(result.Gauge));
            Assert.That(wire[29], Is.EqualTo(0));
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(30)),
                Is.EqualTo(result.Score));
            Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(34)),
                Is.EqualTo(result.BonusScore));
            Assert.That(BitConverter.UInt32BitsToSingle(
                    BinaryPrimitives.ReadUInt32LittleEndian(wire.AsSpan(38))),
                Is.EqualTo(result.Accuracy));
            Assert.That(wire[42], Is.EqualTo((byte)StageResultRank.BPlus));
            Assert.That(wire[50], Is.EqualTo(StageResultState.Cleared));
        });
    }

    [TestCase(100.00f, StageResultRank.SteelMax, 30_000u)]
    [TestCase(99.90f, StageResultRank.GoldenMax, 20_000u)]
    [TestCase(99.70f, StageResultRank.SilverMax, 15_000u)]
    [TestCase(98.65f, StageResultRank.BronzeMax, 10_000u)]
    // KOREAN disc bands: golden 98.01-98.40, silver 97.01-98.00, bronze 96.01-97.00.
    // China's were much wider (96.01/93.01/90.01) - this client is the Korean one.
    [TestCase(98.20f, StageResultRank.GoldenDisc, 5_000u)]
    [TestCase(97.39f, StageResultRank.SilverDisc, 3_000u)]
    [TestCase(96.50f, StageResultRank.BronzeDisc, 1_000u)]
    [TestCase(92.91f, StageResultRank.BPlus, 0u)]
    [TestCase(85.45f, StageResultRank.BPlus, 0u)]
    [TestCase(84.73f, StageResultRank.B, 0u)]
    [TestCase(71.58f, StageResultRank.C, 0u)]
    [TestCase(66.27f, StageResultRank.DPlus, 0u)]
    public void ResultAwardUsesRetailMedalAndGradeCodes(
        float accuracy,
        StageResultRank expectedRank,
        uint expectedBonus)
    {
        StageResultAward award = StageResultAward.Evaluate(accuracy, failed: false);

        Assert.Multiple(() =>
        {
            Assert.That(award.Rank, Is.EqualTo(expectedRank));
            Assert.That(award.RankBonus, Is.EqualTo(expectedBonus));
        });
    }

    [TestCase(1.00f, StageResultRank.SapphireDisc, 150_000u)]
    [TestCase(10.00f, StageResultRank.RubyDisc, 200_000u)]
    [TestCase(77.70f, StageResultRank.RainbowMax, 100_000u)]
    [TestCase(66.60f, StageResultRank.DevilDisc, 100_000u)]
    public void ResultAwardRecognizesExactSpecialDiscs(
        float accuracy,
        StageResultRank expectedRank,
        uint expectedBonus)
    {
        StageResultAward award = StageResultAward.Evaluate(accuracy, failed: false);

        Assert.Multiple(() =>
        {
            Assert.That(award.Rank, Is.EqualTo(expectedRank));
            Assert.That(award.RankBonus, Is.EqualTo(expectedBonus));
        });
    }

    [TestCase(StageResultRank.SapphireDisc, 0x409)]
    [TestCase(StageResultRank.RubyDisc, 0x400)]
    [TestCase(StageResultRank.RainbowMax, 0x401)]
    [TestCase(StageResultRank.DevilDisc, 0x40A)]
    [TestCase(StageResultRank.SteelMax, 0x402)]
    [TestCase(StageResultRank.GoldenMax, 0x403)]
    [TestCase(StageResultRank.SilverMax, 0x404)]
    [TestCase(StageResultRank.BronzeMax, 0x405)]
    [TestCase(StageResultRank.GoldenDisc, 0x406)]
    [TestCase(StageResultRank.SilverDisc, 0x407)]
    [TestCase(StageResultRank.BronzeDisc, 0x408)]
    public void EveryResultDiscMapsToItsCollectionArtwork(
        StageResultRank rank,
        int expectedCode)
    {
        StageResultAward award = new(rank, 0);

        Assert.That(award.CollectionCode, Is.EqualTo((ushort)expectedCode));
    }

    [Test]
    public void LetterGradesAreNotCollectionDiscs()
    {
        Assert.That(
            new StageResultAward(StageResultRank.BPlus, 0).CollectionCode,
            Is.Null);
    }

    [Test]
    public void FullComboAddsRetailAllComboBonus()
    {
        ushort[] judgments = [0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 19];
        StageResult result = new(
            0,
            0,
            20,
            judgments,
            100f,
            0,
            20,
            20,
            0,
            2,
            0);

        Assert.Multiple(() =>
        {
            Assert.That(result.Accuracy, Is.EqualTo(97.5f));
            Assert.That(result.FullCombo, Is.True);
            // 97.5% is a SILVER disc on the Korean table (97.01-98.00).
            Assert.That(result.Rank, Is.EqualTo(StageResultRank.SilverDisc));
            Assert.That(result.RankBonus, Is.EqualTo(3_000));
            Assert.That(result.BonusScore, Is.EqualTo(13_000));
        });
    }

    [Test]
    public void FailedResultAlwaysUsesFWithoutRankBonus()
    {
        StageResultAward award = StageResultAward.Evaluate(99.99f, failed: true);

        Assert.Multiple(() =>
        {
            Assert.That(award.Rank, Is.EqualTo(StageResultRank.F));
            Assert.That(award.RankBonus, Is.Zero);
        });
    }
}
