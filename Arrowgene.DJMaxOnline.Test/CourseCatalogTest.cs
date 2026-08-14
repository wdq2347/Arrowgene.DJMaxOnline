using Arrowgene.DJMaxOnline.Server;
using Arrowgene.DJMaxOnline.Server.Packets;

namespace Arrowgene.DJMaxOnline.Test;

/// <summary>
/// Parses the client's own System\CourseClub\CourseSection.ini. The three decrements here
/// are the client's, not ours: sub_49039E does <c>--CourseNo</c>, <c>--ClubName</c>, and
/// decrements every Songname/Songdiff entry (clamped at zero) while filling its 244-byte
/// course records. Getting any of them wrong serves the wrong chart for a course stage.
/// </summary>
public class CourseCatalogTest
{
    private CourseCatalog _catalog = null!;

    [OneTimeSetUp]
    public void LoadCatalog()
    {
        string path = CourseCatalog.FindCourseScript(
            Directory.GetCurrentDirectory(),
            TestContext.CurrentContext.TestDirectory) ??
            throw new FileNotFoundException("CourseSection.ini was not found.");
        _catalog = CourseCatalog.Load(path);
    }

    [Test]
    public void ResolvesTheServersOwnCopyRatherThanTheClientInstall()
    {
        // DATA holds the server's copies of the client data files, so course play does not
        // depend on the client tree still being present or unpacked the same way.
        Assert.That(
            _catalog.SourcePath.Replace('\\', '/'),
            Does.Contain("/DATA/CourseSection.ini"));
    }

    [Test]
    public void ReadsEveryCourseInTheScript()
    {
        // The file declares SectionCount = 62 and has 62 brace-delimited blocks.
        Assert.That(_catalog.Count, Is.EqualTo(62));
        Assert.That(_catalog.Ids(), Is.Unique);
    }

    [Test]
    public void FirstCourseMatchesTheScriptAfterTheClientsDecrements()
    {
        // CourseNo = 1, ClubName = 1, Songname = 1,2,9, Songdiff = 1,1,1.
        Assert.That(_catalog.TryGet(0, out CourseDefinition? course), Is.True);
        Assert.That(course, Is.Not.Null);
        Assert.Multiple(() =>
        {
            // The apostrophe in the quoted name must not be treated as a comment.
            Assert.That(course!.Name, Is.EqualTo("Let's Begin"));
            Assert.That(course.ClubIndex, Is.EqualTo(0u));
            Assert.That(course.Premium, Is.False);
            Assert.That(course.MaxPrice, Is.EqualTo(200u));
            // [Clear] Correct = 80,1 -> at least 80% accuracy; the other three are 0,1.
            Assert.That(course.Objectives.Accuracy.Value, Is.EqualTo(80u));
            Assert.That(course.Objectives.Accuracy.AtLeast, Is.True);
            Assert.That(course.Objectives.Score.IsActive(), Is.False);
            // [ClearRes] Max = 50, Exp = 0. Both live in a section that reuses key names
            // found in [Max], so a section-blind parser reads the wrong values here.
            Assert.That(course.Rewards.MoneyPercent, Is.EqualTo(50u));
            Assert.That(course.Rewards.ExperiencePercent, Is.EqualTo(0u));
            Assert.That(
                course.Stages.Select(stage => stage.DiscId), Is.EqualTo(new uint[] { 0, 1, 8 }));
            Assert.That(
                course.Stages.Select(stage => stage.Difficulty),
                Is.EqualTo(new byte[] { 0, 0, 0 }));
        });
    }

    [Test]
    public void LastCourseKeepsItsSixStagesAndPremiumFlag()
    {
        // CourseNo = 62: Premium = 1, Song = 6, Songname = 91,15,19,26,170,61.
        Assert.That(_catalog.TryGet(61, out CourseDefinition? course), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(course!.Premium, Is.True);
            Assert.That(course.Stages, Has.Count.EqualTo(6));
            Assert.That(course.Stages[0].DiscId, Is.EqualTo(90u));
            Assert.That(course.Stages[5].DiscId, Is.EqualTo(60u));
            Assert.That(course.Stages[0].Difficulty, Is.EqualTo(2));
        });
    }

    [Test]
    public void EveryCourseHasPlayableStages()
    {
        // A stage with no song would make the server serve an arbitrary chart, so an
        // empty stage list must never survive parsing.
        Assert.That(
            _catalog.Courses.Where(course => course.Stages.Count == 0),
            Is.Empty);
        // Songdiff is 1..5 in the file, so after the client's decrement nothing may
        // exceed 4 - a higher value would ask the chart provider for a chart that the
        // client cannot select.
        Assert.That(
            _catalog.Courses.SelectMany(course => course.Stages)
                .Where(stage => stage.Difficulty > 4),
            Is.Empty);
    }

    [Test]
    public void CourseStageStampLandsWhereTheClientReadsIt()
    {
        // sub_4357F0 copies the payload from wire offset 7 into net+794279, so payload+12
        // is net+794291 (course id) and payload+14 is net+794293 (stage index). The
        // Course Club reads both from there and from nowhere else.
        byte[] header = new byte[132];
        // A recognisable chart body plus its declared size, so Parse accepts the blob.
        byte[] blob = [.. header, 1, 2, 3, 4];
        BitConverter.GetBytes(4u).CopyTo(blob, 128);
        // Mark the descramble key inputs and the scrambled config block so the stamp can
        // be shown not to touch them.
        for (int i = 4; i < 10; i++) { blob[i] = 0xAA; }
        for (int i = 16; i < 128; i++) { blob[i] = 0xBB; }

        GameInfoPayload stamped = new GameInfoPayload(0, 0, blob)
            .WithCourseStage(courseId: 20, stageIndex: 2);

        Assert.Multiple(() =>
        {
            Assert.That(BitConverter.ToUInt16(stamped.Bytes, 10), Is.EqualTo(1),
                "course-mode flag; without it sub_4074C0 never ends the course");
            Assert.That(BitConverter.ToUInt16(stamped.Bytes, 12), Is.EqualTo(20));
            Assert.That(BitConverter.ToUInt16(stamped.Bytes, 14), Is.EqualTo(2));
            Assert.That(stamped.Bytes.Skip(4).Take(6), Is.All.EqualTo(0xAA),
                "the descramble key inputs must be untouched");
            Assert.That(stamped.Bytes.Skip(16).Take(112), Is.All.EqualTo(0xBB),
                "the scrambled config block must be untouched");
            Assert.That(stamped.Bytes.Skip(132), Is.EqualTo(new byte[] { 1, 2, 3, 4 }),
                "the chart body must be untouched");
            Assert.That(blob[12], Is.EqualTo(0), "the source payload must not be mutated");
        });
    }

    [Test]
    public void RepeatedItemnumLinesAllSurviveTheParse()
    {
        // Itemnum is the one key that legitimately repeats inside [ClearRes] - course 1
        // awards two items - so a first-occurrence-wins parse silently drops the rest.
        Assert.That(_catalog.TryGet(0, out CourseDefinition? first), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(first!.Rewards.Items, Has.Count.EqualTo(2));
            // 0xF401 = HP booster LV1 at a 30% drop chance, 0xF501 = MAX booster LV1 at
            // 20% - the second field is a PERCENTAGE, not a quantity, so course 1 leaves
            // a 50% chance of awarding nothing at all.
            Assert.That(first.Rewards.Items[0].CatalogId, Is.EqualTo(0xF401));
            Assert.That(first.Rewards.Items[0].ChancePercent, Is.EqualTo(30));
            Assert.That(first.Rewards.Items[1].CatalogId, Is.EqualTo(0xF501));
            Assert.That(first.Rewards.Items[1].ChancePercent, Is.EqualTo(20));
        });

        // 237 award lines across the script; a regression that keeps only the first per
        // course would collapse this badly.
        Assert.That(
            _catalog.Courses.Sum(course => course.Rewards.Items.Count),
            Is.EqualTo(237));
    }

    [Test]
    public void ClearResDiscNumIsACollectionAwardCode()
    {
        // DiscNum is not a song index: every value in the script (1056+) lands in the
        // 0x400..0x43F event/disc band that sub_465AD1 reads out of the PRIZE/COLLECTION
        // block, which is what makes a "reward disc" a collection entry.
        Assert.That(_catalog.TryGet(0, out CourseDefinition? first), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(first!.Rewards.DiscCode, Is.EqualTo(1056));
            Assert.That(first.Rewards.HasDisc, Is.True);
        });

        CourseDefinition[] withDisc = _catalog.Courses
            .Where(course => course.Rewards.DiscCode != 0)
            .ToArray();
        Assert.That(withDisc, Is.Not.Empty);
        Assert.That(
            withDisc.Where(course => !course.Rewards.HasDisc), Is.Empty,
            "a DiscNum outside the award band would mean this is not a collection code");
    }

    [Test]
    public void PrerequisiteGatesUseTheSameOneBasedCourseNumbering()
    {
        // Every block declares Number = 1 and exactly one "Switch = kind,course", and the
        // course is one-based like CourseNo - so it takes the same decrement.
        Assert.That(
            _catalog.Courses.Where(course => course.RequiredCourseId == null), Is.Empty,
            "the script gates all 62 courses");

        Assert.That(_catalog.TryGet(0, out CourseDefinition? first), Is.True);
        Assert.Multiple(() =>
        {
            // Course 1's gate points at itself, which must not lock the whole mode.
            Assert.That(first!.RequiredCourseId, Is.EqualTo((ushort)0));
            Assert.That(first.IsUnlocked(new HashSet<ushort>()), Is.True);
        });

        CourseDefinition gated = _catalog.Courses
            .First(course => course.RequiredCourseId != course.Id);
        Assert.Multiple(() =>
        {
            Assert.That(gated.IsUnlocked(new HashSet<ushort>()), Is.False);
            Assert.That(
                gated.IsUnlocked(new HashSet<ushort> { gated.RequiredCourseId!.Value }),
                Is.True);
        });
    }

    [Test]
    public void MissingScriptYieldsAnEmptyCatalogRatherThanThrowing()
    {
        // Without the client's script the Course Club still lists and browses; only
        // starting a course is unavailable.
        Assert.That(CourseCatalog.Empty.Count, Is.EqualTo(0));
        Assert.That(CourseCatalog.Empty.TryGet(0, out _), Is.False);
    }

    [Test]
    public void ItemRewardChancesAreNeverQuantities()
    {
        // Decisive across the whole script: if the second field were a count, per-course
        // totals would run past 100 constantly. They never do - and the shortfall to 100
        // is the documented "no item acquisition" outcome.
        foreach (CourseDefinition course in _catalog.Courses)
        {
            int total = course.Rewards.Items.Sum(item => item.ChancePercent);
            Assert.That(total, Is.LessThanOrEqualTo(100),
                $"Course \"{course.Name}\" drop chances sum to {total}%.");
            Assert.That(
                course.Rewards.Items.Select(item => (int)item.ChancePercent),
                Is.All.InRange(0, 100));
        }
    }
}
