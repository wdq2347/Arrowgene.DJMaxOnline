using Arrowgene.DJMaxOnline.Server.Korea400;
using Arrowgene.DJMaxOnline.Server.Korea400.Packets;

namespace Arrowgene.DJMaxOnline.Test;

/// <summary>
/// Course-mode wire layouts. Every constant here comes from the Korean client: the sizes
/// from its registration table sub_42F440, the ranking board's geometry from the copy-in
/// at sub_488524 and the render loop in sub_487FC1. A wrong size desyncs the stream, so
/// these assertions guard the framing as much as the field placement.
/// </summary>
public class CoursePacketTest
{
    private const byte FiveKey = (byte)SongKeyMode.FiveKey;
    private const byte SevenKey = (byte)SongKeyMode.SevenKey;

    [Test]
    public void RequestSizesMatchTheClientSenders()
    {
        // The China table had these as 13/13/13/11; the client's own senders
        // (sub_432990, sub_432A40, sub_432AF0, sub_432BE0) prove otherwise.
        Assert.Multiple(() =>
        {
            Assert.That(PacketMeta.sub_434390.Size, Is.EqualTo(5));
            Assert.That(PacketMeta.sub_434450.Size, Is.EqualTo(5));
            Assert.That(PacketMeta.sub_434510.Size, Is.EqualTo(5));
            Assert.That(PacketMeta.sub_434620.Size, Is.EqualTo(3));
        });
    }

    [Test]
    public void AckSizesMatchTheClientRegistrationTable()
    {
        Assert.Multiple(() =>
        {
            Assert.That(PacketMeta.OnCourseRankAck.Size, Is.EqualTo(2155));
            Assert.That(PacketMeta.OnChangeCourseAck.Size, Is.EqualTo(5));
            Assert.That(PacketMeta.OnContinueCourseAck.Size, Is.EqualTo(5));
            Assert.That(PacketMeta.OnPostCourseItemReq.Size, Is.EqualTo(11));
            Assert.That(PacketMeta.OnAwardItemInf.Size, Is.EqualTo(7));
        });
    }

    [Test]
    public void RankingTableFillsTheBodyExactly()
    {
        // The client memcpy's 0x866 bytes from wire offset 5, so 2 ignored bytes plus
        // 50 rows of 43 must be precisely the body the meta allocates.
        Assert.That(
            OnCourseRankAckPacket.BodySize,
            Is.EqualTo(PacketMeta.OnCourseRankAck.Size - 3));
        Assert.That(
            OnCourseRankAckPacket.EntryCount * OnCourseRankAckPacket.EntrySize,
            Is.EqualTo(0x866));
    }

    [Test]
    public void RankingRowLandsOnTheOffsetsTheClientReads()
    {
        byte[] wire = Wire(OnCourseRankAckPacket.Build(
            courseId: 7,
            [new CourseRankEntry(0x11223344, "Blade", 987654, 12)]));

        Assert.That(wire, Has.Length.EqualTo(PacketMeta.OnCourseRankAck.Size));

        // The client's cache copy starts here; row 0 begins at the same byte.
        const int table = 5;
        Assert.Multiple(() =>
        {
            Assert.That(BitConverter.ToUInt32(wire, table), Is.EqualTo(0x11223344u));
            Assert.That(
                System.Text.Encoding.ASCII.GetString(wire, table + 4, 5),
                Is.EqualTo("Blade"));
            Assert.That(wire[table + 9], Is.EqualTo(0), "nickname terminator");
            Assert.That(BitConverter.ToUInt32(wire, table + 31), Is.EqualTo(987654u));
            Assert.That(BitConverter.ToUInt32(wire, table + 35), Is.EqualTo(12u));

            // Row 1 onward stays zeroed, which the client renders as dashes.
            Assert.That(
                BitConverter.ToUInt32(wire, table + OnCourseRankAckPacket.EntrySize),
                Is.EqualTo(0u));
        });
    }

    [Test]
    public void RankingRejectsMoreRowsThanTheClientWalks()
    {
        CourseRankEntry[] tooMany = Enumerable
            .Repeat(CourseRankEntry.Empty, OnCourseRankAckPacket.EntryCount + 1)
            .ToArray();
        Assert.Throws<ArgumentException>(
            () => OnCourseRankAckPacket.Build(0, tooMany));
    }

    [Test]
    public void ChangeCourseAckEchoesTheCourseAtOffsetThree()
    {
        // sub_4914A3 reads this word into the scene's current-course field; a wrong value
        // leaves the scene re-sending ChangeCourseReq forever.
        byte[] wire = Wire(OnChangeCourseAckPacket.Build(0x1234));
        Assert.That(wire, Has.Length.EqualTo(5));
        Assert.That(BitConverter.ToUInt16(wire, 3), Is.EqualTo(0x1234));
    }

    [Test]
    public void CourseRewardAnnouncementCarriesTheItemTheResultScreenReads()
    {
        // sub_432B90 copies these dwords from wire+3/+7 to net+0xDA8F4/+0xDA8F8.
        // sub_494FFE reads the low word of the first value to choose the award icon.
        byte[] wire = Wire(OnPostCourseItemReqPacket.Build(
            itemId: 0x0001F401,
            expiration: 0x12345678));

        Assert.That(wire, Has.Length.EqualTo(11));
        Assert.Multiple(() =>
        {
            Assert.That(BitConverter.ToUInt32(wire, 3), Is.EqualTo(0x0001F401u));
            Assert.That(BitConverter.ToUInt16(wire, 3), Is.EqualTo(0xF401));
            Assert.That(BitConverter.ToUInt32(wire, 7), Is.EqualTo(0x12345678u));
        });
    }

    [Test]
    public void ParsesARequestFramedTheWayTheClientSendsIt()
    {
        // A received 5-byte packet has no header at all: PacketFactory only splits off a
        // header once the body reaches five bytes, so the whole body arrives in Data.
        // Parsing has to work on that shape, not just on the builder's Header/Data split.
        Packet received = new(PacketMeta.sub_434390, [0x87, 0x34, 0x12]);
        Assert.That(received.Header, Is.Null);
        Assert.That(CourseSelectionReqPacket.Parse(received).CourseId, Is.EqualTo(0x1234));
    }

    /// <summary>Reassembles the bytes as they go out: id, clear header, then data.</summary>
    private static byte[] Wire(Packet packet) =>
    [
        .. BitConverter.GetBytes((ushort)packet.Id),
        .. packet.Header ?? [],
        .. packet.Data
    ];

    [Test]
    public void StartParameterCarriesTheComboIntoTheNextStage()
    {
        // THE course combo carry. sub_436960 stores this packet's two u32 into
        // net+894940[slot] (gauge) and net+894964[slot] (combo); the play reset sub_422250
        // then seeds the live combo with `player+212 = net[894964 + 4*slot]` instead of
        // zeroing it, which is how a combo survives from one chart to the next. Sending 0
        // in the combo field is exactly what made the on-screen combo restart every stage.
        byte[] wire = Wire(OnStartParameterInfPacket.Build(
            StartParameter.Continuing(slot: 2, carriedCombo: 431)));

        Assert.That(wire, Has.Length.EqualTo(12));
        Assert.Multiple(() =>
        {
            Assert.That(wire[3], Is.EqualTo(2), "slot@3 indexes both client arrays");
            Assert.That(
                BitConverter.ToSingle(wire, 4), Is.EqualTo(100f), "gauge@4");
            Assert.That(
                BitConverter.ToUInt32(wire, 8), Is.EqualTo(431u),
                "combo@8 -> net+894964[slot] -> the next stage's starting combo");
        });
    }

    [Test]
    public void AFreshSongStartsWithNoCarriedCombo()
    {
        // Only a course stage continues a combo; anything else must send zero or the
        // client would start a normal song mid-run.
        byte[] wire = Wire(OnStartParameterInfPacket.Build(StartParameter.Default(0)));
        Assert.That(BitConverter.ToUInt32(wire, 8), Is.EqualTo(0u));
    }

    [Test]
    public void CourseClearKeepsTheBestScoreAndCountsClears()
    {
        string directory = ShopCatalog.FindDataDirectory(
            Directory.GetCurrentDirectory(),
            TestContext.CurrentContext.TestDirectory) ??
            throw new DirectoryNotFoundException("Test shop DATA was not found.");
        LocalPlayerStore store = new(
            new LocalPlayerProfile
            {
                Nickname = "Blade",
                UserId = 9,
                SessionUserId = 0x2345
            },
            ShopCatalog.Load(directory));

        Assert.That(store.CourseRanking(3, FiveKey), Is.Empty);

        store.RecordCourseClear(3, FiveKey, score: 500, combo: 260);
        store.RecordCourseClear(3, FiveKey, score: 100, combo: 99);

        IReadOnlyList<CourseRankEntry> ranking = store.CourseRanking(3, FiveKey);
        Assert.That(ranking, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(ranking[0].UserId, Is.EqualTo(0x2345u),
                "the row must match the player's current wire/session identity");
            Assert.That(ranking[0].Nickname, Is.EqualTo("Blade"));
            Assert.That(ranking[0].Score, Is.EqualTo(500u), "a worse run must not overwrite");
            Assert.That(ranking[0].Combo, Is.EqualTo(260u),
                "the board's second column is best combo, not the clear count");
            Assert.That(store.CourseRanking(4, FiveKey), Is.Empty, "records are per course");
        });
    }

    [Test]
    public void AFailedCourseRecordsNothingAtAll()
    {
        string directory = ShopCatalog.FindDataDirectory(
            Directory.GetCurrentDirectory(),
            TestContext.CurrentContext.TestDirectory) ??
            throw new DirectoryNotFoundException("Test shop DATA was not found.");
        LocalPlayerStore store = new(
            new LocalPlayerProfile { Nickname = "Blade", UserId = 9 },
            ShopCatalog.Load(directory));

        // A run that misses the [Clear] objectives must leave no trace. Score and combo
        // are the two columns the ranking board draws, so recording them for a failed
        // attempt put failures on the leaderboard - reaching the final stage was enough
        // to rank, which is the whole thing the conditions exist to stop.
        CourseRecord missed = store.RecordCourseClear(3, FiveKey, 700, 300, cleared: false);
        Assert.Multiple(() =>
        {
            Assert.That(missed.Score, Is.EqualTo(0u), "a failed run must not rank");
            Assert.That(missed.Combo, Is.EqualTo(0u), "a failed run must not rank");
            Assert.That(missed.Clears, Is.EqualTo(0u),
                "reaching the last stage is not clearing the course");
        });
        Assert.That(store.CourseRanking(3, FiveKey), Is.Empty,
            "an unheld course must not appear on the board at all");

        CourseRecord cleared = store.RecordCourseClear(3, FiveKey, 400, 120, cleared: true);
        Assert.Multiple(() =>
        {
            Assert.That(cleared.Score, Is.EqualTo(400u),
                "the first real clear sets the record; the failed 700 is gone");
            Assert.That(cleared.Combo, Is.EqualTo(120u));
            Assert.That(cleared.Clears, Is.EqualTo(1u));
        });

        // A later failure must not damage a record already earned.
        CourseRecord after = store.RecordCourseClear(3, FiveKey, 999, 999, cleared: false);
        Assert.Multiple(() =>
        {
            Assert.That(after.Score, Is.EqualTo(400u), "a failed run cannot raise the best");
            Assert.That(after.Clears, Is.EqualTo(1u), "nor add a clear");
        });
    }

    /// <summary>
    /// SEOUL (5-key) and TOKYO (7-key) serve entirely different charts for the same
    /// course, so their records and boards must not touch. Before this, one record per
    /// course meant a 7-key run overwrote the 5-key best and both appeared on one board.
    /// </summary>
    [Test]
    public void EachChannelKeepsItsOwnCourseRecordAndBoard()
    {
        string directory = ShopCatalog.FindDataDirectory(
            Directory.GetCurrentDirectory(),
            TestContext.CurrentContext.TestDirectory) ??
            throw new DirectoryNotFoundException("Test shop DATA was not found.");
        LocalPlayerProfile profile = new() { Nickname = "Blade", UserId = 9 };
        LocalPlayerStore store = new(profile, ShopCatalog.Load(directory));

        store.RecordCourseClear(3, FiveKey, score: 500, combo: 260);
        store.RecordCourseClear(3, SevenKey, score: 120, combo: 40);

        IReadOnlyList<CourseRankEntry> seoul = store.CourseRanking(3, FiveKey);
        IReadOnlyList<CourseRankEntry> tokyo = store.CourseRanking(3, SevenKey);

        Assert.Multiple(() =>
        {
            Assert.That(profile.CourseRecords, Has.Count.EqualTo(2),
                "one record per channel, not one per course");
            Assert.That(seoul[0].Score, Is.EqualTo(500u));
            Assert.That(seoul[0].Combo, Is.EqualTo(260u));
            // The lower 7-key score must survive: it is a different chart, not a worse run.
            Assert.That(tokyo[0].Score, Is.EqualTo(120u));
            Assert.That(tokyo[0].Combo, Is.EqualTo(40u));
        });
    }

    /// <summary>A clear on one channel must not unlock the other channel's course.</summary>
    [Test]
    public void ClearCountsDoNotLeakBetweenChannels()
    {
        string directory = ShopCatalog.FindDataDirectory(
            Directory.GetCurrentDirectory(),
            TestContext.CurrentContext.TestDirectory) ??
            throw new DirectoryNotFoundException("Test shop DATA was not found.");
        LocalPlayerProfile profile = new() { Nickname = "Blade", UserId = 9 };
        LocalPlayerStore store = new(profile, ShopCatalog.Load(directory));

        store.RecordCourseClear(3, FiveKey, 500, 260, cleared: true);
        store.RecordCourseClear(3, SevenKey, 500, 260, cleared: false);

        Assert.Multiple(() =>
        {
            Assert.That(
                profile.CourseRecords.Single(r => r.KeyMode == FiveKey).Clears,
                Is.EqualTo(1u));
            // The 7-key attempt failed, so it leaves no row at all - a stronger form of
            // the same guarantee than a row reading zero clears. What matters is that
            // clearing it in SEOUL did not mark it cleared in TOKYO.
            Assert.That(profile.CourseRecords.Any(r => r.KeyMode == SevenKey), Is.False,
                "a failed run must not create a record for the other channel");
        });

        // And a real 7-key clear gets its own separate row rather than touching the 5-key one.
        store.RecordCourseClear(3, SevenKey, 400, 100, cleared: true);
        Assert.Multiple(() =>
        {
            Assert.That(
                profile.CourseRecords.Single(r => r.KeyMode == FiveKey).Score,
                Is.EqualTo(500u));
            Assert.That(
                profile.CourseRecords.Single(r => r.KeyMode == SevenKey).Score,
                Is.EqualTo(400u));
        });
    }
}
