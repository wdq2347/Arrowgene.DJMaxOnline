using Arrowgene.DJMaxOnline.Server.Korea400;
using Arrowgene.DJMaxOnline.Server.Korea400.Packets;

namespace Arrowgene.DJMaxOnline.Test;

public class CourseRecordMaintenanceTest
{
    private string _directory = null!;
    private string _databasePath = null!;
    private CourseCatalog _catalog = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(
            Path.GetTempPath(), "djmax-course-maintenance-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "players.sqlite3");
        string coursePath = Path.Combine(_directory, "CourseSection.ini");
        File.WriteAllText(coursePath,
            """
            {
            [General]
            CourseNo = 1
            CourseName = "Evidence Course"
            ClubName = 1
            Song = 2
            Songname = 1,2
            Songdiff = 1,1
            [Prerequisite]
            Number = 1
            Switch = 1,1
            [Max]
            MaxPrice = 0
            [Clear]
            Correct = 90,1
            Score = 500,1
            Break = 5,0
            Combo = 100,1
            [ClearRes]
            Max = 0
            Exp = 0
            DiscNum = 0
            }
            """);
        _catalog = CourseCatalog.Load(coursePath);
    }

    [TearDown]
    public void TearDown()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Test]
    public void RemovesOnlyRankingRowsProvenInvalidByCompleteStageHistory()
    {
        SqlitePlayerRepository repository = new(_databasePath);
        LocalPlayerProfile invalid = repository.Create("INVALID", "InvalidClear");
        invalid.CourseRecords.Add(new CourseRecord
        {
            CourseId = 0,
            KeyMode = 5,
            Score = 450,
            Combo = 80,
            Clears = 1
        });
        repository.Save(invalid);
        repository.SaveWithStageScore(invalid,
            Stage(invalid.UserId, 0, 0, score: 200, bonus: 50, accuracy: 85, combo: 40));
        repository.SaveWithStageScore(invalid,
            Stage(invalid.UserId, 1, 1, score: 200, bonus: 0, accuracy: 85, combo: 80));

        LocalPlayerProfile valid = repository.Create("VALID", "ValidClear");
        valid.CourseRecords.Add(new CourseRecord
        {
            CourseId = 0,
            KeyMode = 5,
            Score = 650,
            Combo = 120,
            Clears = 1
        });
        repository.Save(valid);
        repository.SaveWithStageScore(valid,
            Stage(valid.UserId, 0, 0, score: 300, bonus: 50, accuracy: 95, combo: 60));
        repository.SaveWithStageScore(valid,
            Stage(valid.UserId, 1, 1, score: 300, bonus: 0, accuracy: 95, combo: 120));

        LocalPlayerProfile unknown = repository.Create("UNKNOWN", "UnknownClear");
        unknown.CourseRecords.Add(new CourseRecord
        {
            CourseId = 0,
            KeyMode = 5,
            Score = 999,
            Combo = 999,
            Clears = 1
        });
        repository.Save(unknown);

        InvalidCourseClearRemovalReport report =
            repository.RemoveInvalidCourseClears(_catalog);

        Assert.Multiple(() =>
        {
            Assert.That(report.Examined, Is.EqualTo(3));
            Assert.That(report.Removed, Is.EqualTo(1));
            Assert.That(report.Valid, Is.EqualTo(1));
            Assert.That(report.Unverifiable, Is.EqualTo(1));
            Assert.That(report.RemovedRecords.Single().UserId, Is.EqualTo(invalid.UserId));
            Assert.That(repository.TryLoad(invalid.UserId, out LocalPlayerProfile? removed),
                Is.True);
            Assert.That(removed!.CourseRecords, Is.Empty);
            Assert.That(repository.CourseRanking(0, 5).Select(row => row.UserId),
                Is.EquivalentTo(new[] { valid.UserId, unknown.UserId }));
        });
    }

    private static StageScoreRecord Stage(
        uint userId,
        int courseStage,
        uint songId,
        uint score,
        uint bonus,
        float accuracy,
        uint combo) => new()
    {
        UserId = userId,
        PlayedAt = DateTimeOffset.UtcNow.AddSeconds(courseStage),
        SongId = songId,
        CourseId = 0,
        CourseStage = courseStage,
        KeyMode = 5,
        Difficulty = 0,
        Judgments = new ushort[StageResult.JudgmentCount],
        Score = score,
        BonusScore = bonus,
        Accuracy = accuracy,
        MaxCombo = combo,
        Breaks = 0,
        ResultState = StageResultState.Cleared,
        Failed = false
    };
}
