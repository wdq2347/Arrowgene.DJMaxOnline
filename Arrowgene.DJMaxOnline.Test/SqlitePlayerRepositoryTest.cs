using System.Text;
using Arrowgene.DJMaxOnline.Server.Korea400;
using Arrowgene.DJMaxOnline.Server.Korea400.Packets;
using Microsoft.Data.Sqlite;

namespace Arrowgene.DJMaxOnline.Test;

public class SqlitePlayerRepositoryTest
{
    private string _directory = null!;
    private string _databasePath = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(
            Path.GetTempPath(), "djmax-sqlite-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "players.sqlite3");
    }

    [TearDown]
    public void TearDown()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Test]
    public void VersionOneDatabaseMigratesCredentialTableToVersionTwo()
    {
        using (SqliteConnection connection = new($"Data Source={_databasePath}"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 1;";
            command.ExecuteNonQuery();
        }

        _ = new SqlitePlayerRepository(_databasePath);
        using SqliteConnection migrated = new($"Data Source={_databasePath}");
        migrated.Open();
        using SqliteCommand verify = migrated.CreateCommand();
        verify.CommandText =
            "SELECT user_version FROM pragma_user_version; " +
            "SELECT COUNT(*) FROM sqlite_master " +
            "WHERE type='table' AND name='player_credentials';";
        using SqliteDataReader reader = verify.ExecuteReader();
        Assert.That(reader.Read(), Is.True);
        // Track the constant, not a literal, so a schema bump does not need this edited.
        Assert.That(
            reader.GetInt32(0), Is.EqualTo(SqlitePlayerRepository.SchemaVersion));
        Assert.That(reader.NextResult(), Is.True);
        Assert.That(reader.Read(), Is.True);
        Assert.That(reader.GetInt32(0), Is.EqualTo(1));
    }

    [Test]
    public void PasswordCredentialRoundTripsWithoutEnteringProfileJson()
    {
        SqlitePlayerRepository repository = new(_databasePath);
        LocalPlayerProfile player = repository.Create("LOGIN", "SecurePlayer");
        PlayerPasswordCredential expected =
            PasswordSecurity.Create(player.UserId, "a sufficiently long password");
        repository.SetPasswordCredential(expected);

        bool found = repository.TryGetPasswordCredential(
            "login", out PlayerPasswordCredential? actual);
        using SqliteConnection connection = new($"Data Source={_databasePath}");
        connection.Open();
        using SqliteCommand version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True);
            Assert.That(actual, Is.Not.Null);
            Assert.That(actual!.Algorithm, Is.EqualTo(PasswordSecurity.Algorithm));
            Assert.That(actual.Iterations, Is.EqualTo(PasswordSecurity.Iterations));
            Assert.That(actual.Salt, Is.EqualTo(expected.Salt));
            Assert.That(actual.Hash, Is.EqualTo(expected.Hash));
            Assert.That(
                Convert.ToInt32(version.ExecuteScalar()),
                Is.EqualTo(SqlitePlayerRepository.SchemaVersion));
            Assert.That(
                LocalPlayerProfileFile.Serialize(player),
                Does.Not.Contain(Convert.ToBase64String(expected.Hash)));
        });
    }

    [Test]
    public void CompleteProfileRoundTripsThroughNormalizedTables()
    {
        LocalPlayerProfile expected = CompleteProfile(42, "BLADE42", "Blade42");
        SqlitePlayerRepository repository = new(_databasePath);

        repository.Save(expected);
        bool found = repository.TryLoad(expected.UserId, out LocalPlayerProfile? actual);
        byte[] header = new byte[16];
        using (FileStream stream = new(
                   _databasePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            stream.ReadExactly(header);
        }

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True);
            Assert.That(actual, Is.Not.Null);
            Assert.That(
                LocalPlayerProfileFile.Serialize(actual!),
                Is.EqualTo(LocalPlayerProfileFile.Serialize(expected)));
            Assert.That(repository.UserCount, Is.EqualTo(1));
            Assert.That(File.Exists(_databasePath), Is.True);
            Assert.That(Encoding.ASCII.GetString(header), Is.EqualTo("SQLite format 3\0"));
        });
    }

    [Test]
    public void MultipleUsersCanBeSelectedIndependently()
    {
        SqlitePlayerRepository repository = new(_databasePath);
        LocalPlayerProfile blade = CompleteProfile(1, "BLADE", "Blade");
        LocalPlayerProfile rival = CompleteProfile(2, "RIVAL", "Rival");
        rival.Progress.Level = 37;
        repository.Save(blade);
        repository.Save(rival);

        Assert.Multiple(() =>
        {
            Assert.That(repository.UserCount, Is.EqualTo(2));
            Assert.That(repository.ListUsers().Select(user => user.UserId),
                Is.EqualTo(new uint[] { 1, 2 }));
            Assert.That(repository.TryLoad("rival", out LocalPlayerProfile? byAccount),
                Is.True);
            Assert.That(byAccount!.UserId, Is.EqualTo(2));
            Assert.That(repository.TryLoad("RIVAL", out LocalPlayerProfile? byNickname),
                Is.True);
            Assert.That(byNickname!.Progress.Level, Is.EqualTo(37));
        });

        blade.Progress.Money = 1234;
        repository.Save(blade);
        repository.TryLoad(2, out LocalPlayerProfile? unchangedRival);
        Assert.That(unchangedRival!.Progress.Money, Is.EqualTo(rival.Progress.Money));
    }

    [Test]
    public void CreateAllocatesUniqueUsersWithGenderAppropriateDefaults()
    {
        SqlitePlayerRepository repository = new(_databasePath);

        LocalPlayerProfile first = repository.Create("FIRST", "First", gender: 0);
        LocalPlayerProfile second = repository.Create("SECOND", "Second", gender: 1);

        Assert.Multiple(() =>
        {
            Assert.That(first.UserId, Is.EqualTo(1));
            Assert.That(second.UserId, Is.EqualTo(2));
            Assert.That(first.IconId, Is.EqualTo(0x2000));
            Assert.That(second.IconId,
                Is.EqualTo(LocalPlayerProfile.FirstUserIconId));
            Assert.That(repository.ListUsers(), Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void LegacyJsonSeedsOnlyAnEmptyDatabaseAndIsNeverRewritten()
    {
        string jsonPath = Path.Combine(_directory, "player.json");
        LocalPlayerProfile legacy = CompleteProfile(7, "LEGACY", "Imported");
        LocalPlayerProfileFile.Save(jsonPath, legacy);
        string originalJson = File.ReadAllText(jsonPath);
        SqlitePlayerRepository repository = new(_databasePath);

        PlayerDatabaseSelection first = PlayerDatabaseBootstrap.Select(
            repository, jsonPath);
        // Prove a later edit of the old file cannot overwrite the established database.
        LocalPlayerProfile replacement = CompleteProfile(99, "REPLACEMENT", "Replacement");
        LocalPlayerProfileFile.Save(jsonPath, replacement);
        PlayerDatabaseSelection second = PlayerDatabaseBootstrap.Select(
            repository, jsonPath, selector: "LEGACY");

        Assert.Multiple(() =>
        {
            Assert.That(first.ImportedLegacyJson, Is.True);
            Assert.That(first.CreatedDefault, Is.False);
            Assert.That(first.Profile.UserId, Is.EqualTo(7));
            Assert.That(second.ImportedLegacyJson, Is.False);
            Assert.That(second.Profile.UserId, Is.EqualTo(7));
            Assert.That(repository.UserCount, Is.EqualTo(1));
            Assert.That(originalJson, Does.Contain("Imported"));
            Assert.That(File.ReadAllText(jsonPath), Does.Contain("Replacement"),
                "SQLite bootstrap must not rewrite or restore the legacy file");
        });
    }

    [Test]
    public void ProfileAndDetailedStageScoreCommitTogether()
    {
        SqlitePlayerRepository repository = new(_databasePath);
        LocalPlayerProfile profile = CompleteProfile(9, "SCORER", "Scorer");
        profile.Progress.Money = 4567;
        StageScoreRecord expected = Score(profile.UserId);

        repository.SaveWithStageScore(profile, expected);
        repository.TryLoad(profile.UserId, out LocalPlayerProfile? loadedProfile);
        StageScoreRecord actual = repository.StageScores(profile.UserId).Single();

        Assert.Multiple(() =>
        {
            Assert.That(loadedProfile!.Progress.Money, Is.EqualTo(4567));
            Assert.That(actual.Id, Is.GreaterThan(0));
            Assert.That(actual.UserId, Is.EqualTo(expected.UserId));
            Assert.That(actual.SongId, Is.EqualTo(expected.SongId));
            Assert.That(actual.CourseId, Is.EqualTo(expected.CourseId));
            Assert.That(actual.CourseStage, Is.EqualTo(expected.CourseStage));
            Assert.That(actual.Judgments, Is.EqualTo(expected.Judgments));
            Assert.That(actual.Score, Is.EqualTo(expected.Score));
            Assert.That(actual.BonusScore, Is.EqualTo(expected.BonusScore));
            Assert.That(actual.Accuracy, Is.EqualTo(expected.Accuracy).Within(0.0001));
            Assert.That(actual.MoneyAwarded, Is.EqualTo(expected.MoneyAwarded));
            Assert.That(actual.ExperienceAwarded, Is.EqualTo(expected.ExperienceAwarded));
        });
    }

    [Test]
    public void FailedAtomicSaveLeavesNeitherUserNorScore()
    {
        SqlitePlayerRepository repository = new(_databasePath);
        repository.Save(CompleteProfile(1, "TAKEN", "Taken"));
        LocalPlayerProfile conflict = CompleteProfile(2, "TAKEN", "Other");

        Assert.Throws<SqliteException>(() =>
            repository.SaveWithStageScore(conflict, Score(conflict.UserId)));
        Assert.Multiple(() =>
        {
            Assert.That(repository.UserCount, Is.EqualTo(1));
            Assert.That(repository.TryLoad(2, out _), Is.False);
            Assert.That(repository.StageScores(1), Is.Empty);
        });
    }

    [Test]
    public void AccountClassFlagsEachGetTheirOwnColumn()
    {
        LocalPlayerProfile profile = CompleteProfile(7, "FLAGS7", "Flags7");
        profile.AccountClass =
            (uint)(AccountClassFlags.Normal | AccountClassFlags.Premium);
        SqlitePlayerRepository repository = new(_databasePath);
        repository.Save(profile);

        using SqliteConnection connection = new($"Data Source={_databasePath}");
        connection.Open();

        long Column(string name)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT {name} FROM players WHERE user_id = 7;";
            return Convert.ToInt64(command.ExecuteScalar());
        }

        void SetColumn(string name, int value)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"UPDATE players SET {name} = {value} WHERE user_id = 7;";
            command.ExecuteNonQuery();
        }

        Assert.Multiple(() =>
        {
            Assert.That(Column("class_normal"), Is.EqualTo(1));
            Assert.That(Column("class_premium"), Is.EqualTo(1));
            Assert.That(Column("class_admin"), Is.EqualTo(0));
        });

        // Granting a privilege by flipping a column to 1 is the whole point.
        SetColumn("class_game_master", 1);
        // Clearing one has to actually clear it, rather than being re-OR'd back out of
        // the stale packed account_class value.
        SetColumn("class_premium", 0);

        Assert.That(repository.TryLoad(7, out LocalPlayerProfile? loaded), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(
                loaded!.AccountClass & (uint)AccountClassFlags.GameMaster, Is.Not.Zero);
            Assert.That(
                loaded.AccountClass & (uint)AccountClassFlags.Premium, Is.Zero);
            Assert.That(
                loaded.AccountClass & (uint)AccountClassFlags.Normal, Is.Not.Zero);
        });
    }

    [Test]
    public void ExistingRowsKeepTheirPrivilegesWhenTheColumnsAreAdded()
    {
        // A version-2 database has account_class and no flag columns; the migration must
        // back-fill each one so nobody silently loses their class on upgrade.
        LocalPlayerProfile profile = CompleteProfile(9, "OLD9", "Old9");
        profile.AccountClass = (uint)AccountClassFlags.Premium;
        new SqlitePlayerRepository(_databasePath).Save(profile);

        using (SqliteConnection connection = new($"Data Source={_databasePath}"))
        {
            connection.Open();
            foreach ((string column, _) in AccountClassInfo.Columns)
            {
                using SqliteCommand drop = connection.CreateCommand();
                drop.CommandText = $"ALTER TABLE players DROP COLUMN {column};";
                drop.ExecuteNonQuery();
            }
            using SqliteCommand version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version = 2;";
            version.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        SqlitePlayerRepository migrated = new(_databasePath);
        Assert.That(migrated.TryLoad(9, out LocalPlayerProfile? loaded), Is.True);
        Assert.That(
            loaded!.AccountClass & (uint)AccountClassFlags.Premium, Is.Not.Zero);
    }

    [Test]
    public void CourseRankingIsSharedAndLimitedToTheTopFiftyPerCourse()
    {
        SqlitePlayerRepository repository = new(_databasePath);
        LocalPlayerProfile requester = null!;
        for (uint userId = 1; userId <= 55; userId++)
        {
            LocalPlayerProfile profile = new()
            {
                UserId = userId,
                AccountId = $"ACCOUNT{userId:00}",
                Nickname = $"Player{userId:00}",
                CourseRecords =
                [
                    new CourseRecord
                    {
                        CourseId = 3,
                        KeyMode = 5,
                        Score = userId * 1000,
                        Combo = userId * 10,
                        Clears = 1
                    }
                ]
            };
            if (userId == 55)
            {
                profile.CourseRecords.Add(new CourseRecord
                {
                    CourseId = 3,
                    KeyMode = 7,
                    Score = 999999,
                    Combo = 999,
                    Clears = 1
                });
                profile.CourseRecords.Add(new CourseRecord
                {
                    CourseId = 4,
                    KeyMode = 5,
                    Score = 888888,
                    Combo = 888,
                    Clears = 1
                });
                requester = profile;
            }
            repository.Save(profile);
        }

        IReadOnlyList<CourseRankEntry> ranking = repository.CourseRanking(3, 5);
        IReadOnlyList<CourseRankEntry> sevenKey = repository.CourseRanking(3, 7);
        IReadOnlyList<CourseRankEntry> otherCourse = repository.CourseRanking(4, 5);

        string dataDirectory = ShopCatalog.FindDataDirectory(
            Directory.GetCurrentDirectory(),
            TestContext.CurrentContext.TestDirectory) ??
            throw new DirectoryNotFoundException("Test shop DATA was not found.");
        requester.SessionUserId = 0x2345;
        LocalPlayerStore requesterStore = new(
            requester,
            ShopCatalog.Load(dataDirectory),
            repository: repository);
        IReadOnlyList<CourseRankEntry> requesterView =
            requesterStore.CourseRanking(3, 5);

        Assert.Multiple(() =>
        {
            Assert.That(ranking, Has.Count.EqualTo(OnCourseRankAckPacket.EntryCount));
            Assert.That(ranking[0], Is.EqualTo(
                new CourseRankEntry(55, "Player55", 55000, 550)));
            Assert.That(ranking[^1].UserId, Is.EqualTo(6),
                "players below rank 50 stay stored but are not sent in this board");
            Assert.That(ranking.Select(row => row.Score), Is.Ordered.Descending);
            Assert.That(sevenKey, Is.EqualTo(new[]
            {
                new CourseRankEntry(55, "Player55", 999999, 999)
            }), "the other channel has its own board");
            Assert.That(otherCourse, Is.EqualTo(new[]
            {
                new CourseRankEntry(55, "Player55", 888888, 888)
            }), "records are isolated per course");
            Assert.That(requesterView, Has.Count.EqualTo(ranking.Count));
            Assert.That(requesterView[0].UserId, Is.EqualTo(0x2345),
                "the requester must recognize and highlight their own shared row");
            Assert.That(requesterView.Skip(1), Is.EqualTo(ranking.Skip(1)),
                "all other leaderboard rows are identical for every requester");
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                repository.CourseRanking(3, 5, OnCourseRankAckPacket.EntryCount + 1));
        });
    }

    private static LocalPlayerProfile CompleteProfile(
        uint userId,
        string accountId,
        string nickname)
    {
        LocalPlayerProfile profile = new()
        {
            UserId = userId,
            AccountId = accountId,
            SecondaryId = $"S{userId}",
            Nickname = nickname,
            State = 3,
            ProfileCode = 123,
            Gender = 1,
            IconId = LocalPlayerProfile.FirstUserIconId,
            ProfileFlags = 0x11223344,
            AccountClass = (uint)(AccountClassFlags.GameMaster | AccountClassFlags.Premium),
            Reserved = 77,
            Collection = [new CollectionEntry(4, 5), new CollectionEntry(0x401, 6)],
            AvailableCourseIds = [1, 3, 8],
            CourseRecords =
            [
                new CourseRecord { CourseId = 1, Score = 123456, Combo = 400, Clears = 2 },
                new CourseRecord { CourseId = 8, Score = 654321, Combo = 800, Clears = 4 }
            ],
            Messenger = new MessengerBook
            {
                Contacts = [new MessengerContactEntry { UserId = userId + 100, GroupIndex = 2 }],
                BlockedUserIds = [userId + 101],
                GroupNames = ["Friends", "Rivals"]
            },
            Roster =
            [
                new RosterUser
                {
                    UserId = userId + 100,
                    AccountId = $"NPC{userId}",
                    Nickname = $"Npc{userId}",
                    Gender = 0,
                    IconId = 0x2000,
                    Level = 19,
                    AccountClass = (uint)AccountClassFlags.Premium,
                    Online = true
                }
            ],
            Progress = new LocalPlayerProgress
            {
                Level = 12,
                Experience = 345,
                Money = 6789,
                Cash = 9876,
                AwardPoints = 5432,
                Wins = 10,
                Losses = 3,
                Draws = 2,
                FreemodeBest5Key = 111111,
                FreemodeBest7Key = 222222,
                RankingBest5Key = 333333,
                RankingBest7Key = 444444,
                MaxCombo = 555,
                HighestAccuracy = 99.81,
                AverageAccuracy = 94.32,
                MiscStatistics = Enumerable.Range(1, LocalPlayerProgress.MiscStatisticCount)
                    .Select(value => (uint)value).ToArray()
            },
            Inventory = new LocalPlayerInventory
            {
                State = 0x12345678,
                DefaultItems = [new InventoryItemSlot(0, 101)],
                EventItems = [new InventoryItemSlot(1, 202)],
                ShopItems = [new TimedInventorySlot(2, 303, 1000)],
                PresentItems = [new PresentInventorySlot(3, 404, userId + 100, 2000)],
                MountItems = [new TimedInventorySlot(4, 505, 3000)],
                ItemBox = [0x00010301, 0x00020302],
                ItemBoxValues = [0, 4000]
            }
        };
        profile.Validate();
        return profile;
    }

    private static StageScoreRecord Score(uint userId) => new()
    {
        UserId = userId,
        PlayedAt = new DateTimeOffset(2026, 8, 2, 12, 34, 56, TimeSpan.Zero),
        SongId = 17,
        CourseId = 3,
        CourseStage = 1,
        RoomId = 4,
        KeyMode = 7,
        Difficulty = 3,
        MatchMode = LocalRoomProtocol.ScoreBattleMatchMode,
        SessionToken = 0x10203040,
        ClientFlags = 2,
        TotalNotes = 300,
        Judgments = Enumerable.Range(0, StageResult.JudgmentCount)
            .Select(value => (ushort)(value * 3)).ToArray(),
        Gauge = 87.5f,
        EncodedCombo = 0x4321,
        CurrentCombo = 123,
        MaxCombo = 280,
        AuxiliaryValue = 9,
        ResultState = StageResultState.Cleared,
        Tail = 6,
        Failed = false,
        FullCombo = false,
        NotesHit = 297,
        Breaks = 3,
        Score = 245678,
        BonusScore = 1000,
        Accuracy = 97.39f,
        Rank = StageResultRank.GoldenDisc,
        MoneyAwarded = 321,
        ExperienceAwarded = 654
    };

    /// <summary>
    /// Course records gained a key_mode column so SEOUL and TOKYO keep separate boards.
    /// A database written before the split must survive the upgrade with its records
    /// intact, attributed to 5-key - the channel the server always defaulted to.
    /// </summary>
    [Test]
    public void CourseRecordsFromBeforeTheChannelSplitAreKeptAsFiveKey()
    {
        using (SqliteConnection seed = new($"Data Source={_databasePath}"))
        {
            seed.Open();
            foreach (string sql in new[]
                     {
                         """
                         CREATE TABLE players (
                             user_id INTEGER PRIMARY KEY,
                             account_id TEXT NOT NULL,
                             account_class INTEGER NOT NULL DEFAULT 0,
                             profile TEXT NOT NULL
                         );
                         """,
                         """
                         CREATE TABLE course_records (
                             user_id   INTEGER NOT NULL,
                             course_id INTEGER NOT NULL,
                             score     INTEGER NOT NULL,
                             combo     INTEGER NOT NULL,
                             clears    INTEGER NOT NULL,
                             PRIMARY KEY(user_id, course_id)
                         );
                         """,
                         "INSERT INTO players VALUES (1, 'OLD', 0, '{}');",
                         "INSERT INTO course_records VALUES (1, 12, 4321, 77, 3);",
                         "PRAGMA user_version = 3;"
                     })
            {
                using SqliteCommand command = seed.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        // Opening the repository runs the migration.
        _ = new SqlitePlayerRepository(_databasePath);

        using SqliteConnection connection = new($"Data Source={_databasePath}");
        connection.Open();
        using SqliteCommand read = connection.CreateCommand();
        read.CommandText =
            "SELECT key_mode, score, combo, clears FROM course_records WHERE user_id = 1;";
        using SqliteDataReader reader = read.ExecuteReader();

        Assert.That(reader.Read(), Is.True, "the pre-split row must survive");
        Assert.Multiple(() =>
        {
            Assert.That(reader.GetInt32(0), Is.EqualTo(5), "attributed to SEOUL");
            Assert.That(reader.GetInt32(1), Is.EqualTo(4321));
            Assert.That(reader.GetInt32(2), Is.EqualTo(77));
            Assert.That(reader.GetInt32(3), Is.EqualTo(3));
            Assert.That(reader.Read(), Is.False, "exactly one row, not duplicated");
        });
    }
}
