using System.Globalization;
using Arrowgene.DJMaxOnline.Server.Korea400.Packets;
using Microsoft.Data.Sqlite;

namespace Arrowgene.DJMaxOnline.Server.Korea400;

/// <summary>
/// Normalized SQLite persistence for every mutable player-profile section and detailed
/// stage score history. Each profile save replaces its child collections in one
/// transaction, making the in-memory aggregate and database snapshot atomic.
/// </summary>
public sealed class SqlitePlayerRepository : IPlayerRepository
{
    /// <summary>Bumped whenever InitializeSchema has to change an existing database.</summary>
    public const int SchemaVersion = 4;
    private readonly string _connectionString;

    public SqlitePlayerRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        string? directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString();
        InitializeSchema();
    }

    public string DatabasePath { get; }

    public int UserCount
    {
        get
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM players;";
            return checked((int)(long)(command.ExecuteScalar() ?? 0L));
        }
    }

    public IReadOnlyList<PlayerAccountSummary> ListUsers()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.user_id, p.account_id, p.nickname, g.level
            FROM players AS p
            JOIN player_progress AS g ON g.user_id = p.user_id
            ORDER BY p.user_id;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        List<PlayerAccountSummary> users = [];
        while (reader.Read())
        {
            users.Add(new PlayerAccountSummary(
                ReadUInt32(reader, "user_id"),
                reader.GetString(reader.GetOrdinal("account_id")),
                reader.GetString(reader.GetOrdinal("nickname")),
                ReadUInt32(reader, "level")));
        }
        return users;
    }

    public LocalPlayerProfile Create(string accountId, string nickname, byte gender = 1)
    {
        if (gender > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(gender), "Gender must be 0 or 1.");
        }
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        uint userId;
        using (SqliteCommand command = CreateCommand(connection, transaction,
                   "SELECT COALESCE(MAX(user_id), 0) + 1 FROM players;"))
        {
            long next = (long)(command.ExecuteScalar() ?? 1L);
            if (next <= 0 || next > uint.MaxValue)
            {
                throw new InvalidOperationException("No SQLite player user ids remain.");
            }
            userId = (uint)next;
        }

        LocalPlayerProfile profile = LocalPlayerProfile.Default;
        profile.UserId = userId;
        profile.AccountId = accountId;
        profile.Nickname = nickname;
        profile.Gender = gender;
        profile.IconId = gender == 0
            ? 0x2000u
            : LocalPlayerProfile.FirstUserIconId;
        profile.Validate();
        Save(connection, transaction, profile);
        transaction.Commit();
        return profile;
    }

    public bool TryLoad(uint userId, out LocalPlayerProfile? profile)
    {
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        profile = Load(connection, transaction, userId);
        transaction.Commit();
        return profile != null;
    }

    public bool TryLoad(string accountIdOrNickname, out LocalPlayerProfile? profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountIdOrNickname);
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = CreateCommand(connection, transaction,
            """
            SELECT user_id
            FROM players
            WHERE account_id = $selector COLLATE NOCASE
               OR nickname = $selector COLLATE NOCASE
            ORDER BY CASE WHEN account_id = $selector COLLATE NOCASE THEN 0 ELSE 1 END,
                     user_id
            LIMIT 1;
            """);
        Add(command, "$selector", accountIdOrNickname);
        object? value = command.ExecuteScalar();
        profile = value == null || value == DBNull.Value
            ? null
            : Load(connection, transaction, checked((uint)(long)value));
        transaction.Commit();
        return profile != null;
    }

    public bool TryGetPasswordCredential(
        string accountId,
        out PlayerPasswordCredential? credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.user_id, c.algorithm, c.iterations, c.salt, c.password_hash
            FROM players AS p
            JOIN player_credentials AS c ON c.user_id = p.user_id
            WHERE p.account_id = $account_id COLLATE NOCASE
            LIMIT 1;
            """;
        Add(command, "$account_id", accountId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            credential = null;
            return false;
        }

        credential = new PlayerPasswordCredential(
            ReadUInt32(reader, "user_id"),
            reader.GetString(reader.GetOrdinal("algorithm")),
            reader.GetInt32(reader.GetOrdinal("iterations")),
            (byte[])reader[reader.GetOrdinal("salt")],
            (byte[])reader[reader.GetOrdinal("password_hash")]);
        return true;
    }

    public void SetPasswordCredential(PlayerPasswordCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (credential.UserId == 0 ||
            string.IsNullOrWhiteSpace(credential.Algorithm) ||
            credential.Iterations <= 0 ||
            credential.Salt.Length == 0 ||
            credential.Hash.Length == 0)
        {
            throw new ArgumentException("Password credential is incomplete.", nameof(credential));
        }

        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO player_credentials (
                user_id, algorithm, iterations, salt, password_hash, updated_utc)
            VALUES (
                $user_id, $algorithm, $iterations, $salt, $password_hash, $updated_utc)
            ON CONFLICT(user_id) DO UPDATE SET
                algorithm = excluded.algorithm,
                iterations = excluded.iterations,
                salt = excluded.salt,
                password_hash = excluded.password_hash,
                updated_utc = excluded.updated_utc;
            """;
        Add(command, "$user_id", credential.UserId);
        Add(command, "$algorithm", credential.Algorithm);
        Add(command, "$iterations", credential.Iterations);
        Add(command, "$salt", credential.Salt);
        Add(command, "$password_hash", credential.Hash);
        Add(command, "$updated_utc",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException(
                $"SQLite user {credential.UserId} does not exist.");
        }
    }

    public void Save(LocalPlayerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        Save(connection, transaction, profile);
        transaction.Commit();
    }

    public void SaveWithStageScore(LocalPlayerProfile profile, StageScoreRecord score)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(score);
        profile.Validate();
        score.Validate();
        if (profile.UserId != score.UserId)
        {
            throw new InvalidDataException(
                $"Score user {score.UserId} does not match profile {profile.UserId}.");
        }

        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        Save(connection, transaction, profile);
        InsertStageScore(connection, transaction, score);
        transaction.Commit();
    }

    public IReadOnlyList<StageScoreRecord> StageScores(uint userId, int limit = 100)
    {
        if (userId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId));
        }
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT *
            FROM stage_scores
            WHERE user_id = $user_id
            ORDER BY played_utc DESC, score_id DESC
            LIMIT $limit;
            """;
        Add(command, "$user_id", userId);
        Add(command, "$limit", limit);
        using SqliteDataReader reader = command.ExecuteReader();
        List<StageScoreRecord> scores = [];
        while (reader.Read())
        {
            ushort[] judgments = new ushort[StageResult.JudgmentCount];
            for (int index = 0; index < judgments.Length; index++)
            {
                judgments[index] = ReadUInt16(reader, $"judgment_{index}");
            }
            scores.Add(new StageScoreRecord
            {
                Id = reader.GetInt64(reader.GetOrdinal("score_id")),
                UserId = ReadUInt32(reader, "user_id"),
                PlayedAt = DateTimeOffset.Parse(
                    reader.GetString(reader.GetOrdinal("played_utc")),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                SongId = ReadNullableUInt32(reader, "song_id"),
                CourseId = ReadNullableUInt16(reader, "course_id"),
                CourseStage = ReadNullableInt32(reader, "course_stage"),
                RoomId = ReadNullableUInt16(reader, "room_id"),
                KeyMode = ReadByte(reader, "key_mode"),
                Difficulty = ReadByte(reader, "difficulty"),
                MatchMode = ReadByte(reader, "match_mode"),
                SessionToken = ReadUInt32(reader, "session_token"),
                ClientFlags = ReadByte(reader, "client_flags"),
                TotalNotes = ReadUInt16(reader, "total_notes"),
                Judgments = judgments,
                Gauge = Convert.ToSingle(reader.GetDouble(reader.GetOrdinal("gauge"))),
                EncodedCombo = ReadUInt16(reader, "encoded_combo"),
                CurrentCombo = ReadUInt32(reader, "current_combo"),
                MaxCombo = ReadUInt32(reader, "max_combo"),
                AuxiliaryValue = ReadUInt16(reader, "auxiliary_value"),
                ResultState = ReadByte(reader, "result_state"),
                Tail = ReadByte(reader, "tail"),
                Failed = ReadBool(reader, "failed"),
                FullCombo = ReadBool(reader, "full_combo"),
                NotesHit = ReadUInt16(reader, "notes_hit"),
                Breaks = ReadUInt16(reader, "breaks"),
                Score = ReadUInt32(reader, "score"),
                BonusScore = ReadUInt32(reader, "bonus_score"),
                Accuracy = Convert.ToSingle(reader.GetDouble(reader.GetOrdinal("accuracy"))),
                Rank = (StageResultRank)ReadByte(reader, "rank"),
                MoneyAwarded = ReadUInt32(reader, "money_awarded"),
                ExperienceAwarded = ReadUInt32(reader, "experience_awarded")
            });
        }
        return scores;
    }

    /// <summary>
    /// Shared leaderboard for one course and channel. Every account keeps its own best in
    /// course_records; only the highest rows are read for the client's fixed 50-slot board.
    /// Keeping lower records in the database lets an out-of-ranking player improve later.
    /// </summary>
    public IReadOnlyList<CourseRankEntry> CourseRanking(
        ushort courseId,
        byte keyMode,
        int limit = OnCourseRankAckPacket.EntryCount)
    {
        if (limit is <= 0 or > OnCourseRankAckPacket.EntryCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                $"A course ranking limit must be from 1 to " +
                $"{OnCourseRankAckPacket.EntryCount}.");
        }

        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.user_id, p.nickname, c.score, c.combo
            FROM course_records AS c
            JOIN players AS p ON p.user_id = c.user_id
            WHERE c.course_id = $course_id AND c.key_mode = $key_mode
              AND c.clears > 0
            ORDER BY c.score DESC, c.combo DESC, c.user_id ASC
            LIMIT $limit;
            """;
        Add(command, "$course_id", courseId);
        Add(command, "$key_mode", keyMode);
        Add(command, "$limit", limit);

        using SqliteDataReader reader = command.ExecuteReader();
        List<CourseRankEntry> ranking = [];
        while (reader.Read())
        {
            ranking.Add(new CourseRankEntry(
                ReadUInt32(reader, "user_id"),
                reader.GetString(reader.GetOrdinal("nickname")),
                ReadUInt32(reader, "score"),
                ReadUInt32(reader, "combo")));
        }
        return ranking;
    }

    private void InitializeSchema()
    {
        using SqliteConnection connection = Open();
        using (SqliteCommand journal = connection.CreateCommand())
        {
            // WAL is persistent for the database and permits lobby reads while another
            // connection commits a player update. It must not be renegotiated on every
            // pooled connection because doing so can contend with an active transaction.
            journal.CommandText = "PRAGMA journal_mode = WAL;";
            journal.ExecuteScalar();
        }
        using (SqliteCommand versionCommand = connection.CreateCommand())
        {
            versionCommand.CommandText = "PRAGMA user_version;";
            int version = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (version > SchemaVersion)
            {
                throw new InvalidDataException(
                    $"Player database schema {version} is newer than supported " +
                    $"schema {SchemaVersion}.");
            }
        }

        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = CreateCommand(connection, transaction, SchemaSql);
        command.ExecuteNonQuery();
        AddAccountClassColumns(connection, transaction);
        AddCourseRecordKeyMode(connection, transaction);
        EnsureCourseRankingIndex(connection, transaction);
        using SqliteCommand setVersion = CreateCommand(
            connection, transaction, $"PRAGMA user_version = {SchemaVersion};");
        setVersion.ExecuteNonQuery();
        transaction.Commit();
    }

    /// <summary>
    /// Gives every known account-class flag its own column, so a privilege can be granted
    /// by setting a field to 1 instead of hand-packing <c>account_class</c>. Idempotent:
    /// each column is added only when absent, and back-filled from the packed value that
    /// row already had, so an existing database keeps every privilege it was granted.
    /// </summary>
    private static void AddAccountClassColumns(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        HashSet<string> existing = new(StringComparer.OrdinalIgnoreCase);
        using (SqliteCommand columns = CreateCommand(
                   connection, transaction, "PRAGMA table_info(players);"))
        using (SqliteDataReader reader = columns.ExecuteReader())
        {
            while (reader.Read())
            {
                existing.Add(reader.GetString(reader.GetOrdinal("name")));
            }
        }

        // Account lock state (0 none, 1 locked, 2 under review). Added here rather than
        // in the CREATE TABLE so an existing database picks it up on the next start.
        if (!existing.Contains("lock_state"))
        {
            using SqliteCommand addLock = CreateCommand(connection, transaction,
                "ALTER TABLE players ADD COLUMN lock_state INTEGER NOT NULL DEFAULT 0;");
            addLock.ExecuteNonQuery();
        }
        // The reason is for operators only. It is never sent to any client, never
        // broadcast, and never published by the status API - the client has no field
        // for it anyway; it renders its own fixed dialog from the reason CODE.
        if (!existing.Contains("lock_reason"))
        {
            using SqliteCommand addReason = CreateCommand(connection, transaction,
                "ALTER TABLE players ADD COLUMN lock_reason TEXT NOT NULL DEFAULT '';");
            addReason.ExecuteNonQuery();
        }

        foreach ((string column, AccountClassFlags flag) in AccountClassInfo.Columns)
        {
            if (existing.Contains(column))
            {
                continue;
            }
            using SqliteCommand add = CreateCommand(connection, transaction,
                $"ALTER TABLE players ADD COLUMN {column} INTEGER NOT NULL DEFAULT 0;");
            add.ExecuteNonQuery();
            using SqliteCommand backfill = CreateCommand(connection, transaction,
                $"UPDATE players SET {column} = " +
                $"CASE WHEN (account_class & {(uint)flag}) <> 0 THEN 1 ELSE 0 END;");
            backfill.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Splits course records by channel. SEOUL (5-key) and TOKYO (7-key) serve entirely
    /// different charts for the same course, so one record per course let a run on one
    /// channel overwrite the other's best and put both on a single board.
    ///
    /// Idempotent, and it does not discard anything: existing rows predate the split and
    /// are attributed to 5-key, which is the channel the server has always defaulted to.
    /// SQLite cannot widen a primary key in place, so the table is rebuilt.
    /// </summary>
    private static void AddCourseRecordKeyMode(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using (SqliteCommand columns = CreateCommand(
                   connection, transaction, "PRAGMA table_info(course_records);"))
        using (SqliteDataReader reader = columns.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(
                        reader.GetString(reader.GetOrdinal("name")),
                        "key_mode",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        foreach (string sql in new[]
                 {
                     """
                     CREATE TABLE course_records_v4 (
                         user_id   INTEGER NOT NULL REFERENCES players(user_id) ON DELETE CASCADE,
                         course_id INTEGER NOT NULL,
                         key_mode  INTEGER NOT NULL DEFAULT 5,
                         score     INTEGER NOT NULL,
                         combo     INTEGER NOT NULL,
                         clears    INTEGER NOT NULL,
                         PRIMARY KEY(user_id, course_id, key_mode)
                     );
                     """,
                     """
                     INSERT INTO course_records_v4
                         (user_id, course_id, key_mode, score, combo, clears)
                     SELECT user_id, course_id, 5, score, combo, clears
                     FROM course_records;
                     """,
                     "DROP TABLE course_records;",
                     "ALTER TABLE course_records_v4 RENAME TO course_records;"
                 })
        {
            using SqliteCommand command = CreateCommand(connection, transaction, sql);
            command.ExecuteNonQuery();
        }
    }

    private static void EnsureCourseRankingIndex(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using SqliteCommand command = CreateCommand(
            connection,
            transaction,
            """
            CREATE INDEX IF NOT EXISTS ix_course_records_ranking
            ON course_records(course_id, key_mode, score DESC, combo DESC, user_id);
            """);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// The flag columns own every KNOWN bit; <c>account_class</c> contributes only the
    /// bits no column covers, so nothing is lost and clearing a column actually clears
    /// the privilege rather than being re-OR'd back from the stale packed value.
    /// </summary>
    private static uint ReadAccountClass(SqliteDataReader reader)
    {
        uint value = ReadUInt32(reader, "account_class") & ~AccountClassInfo.KnownMask;
        foreach ((string column, AccountClassFlags flag) in AccountClassInfo.Columns)
        {
            if (reader.GetInt64(reader.GetOrdinal(column)) != 0)
            {
                value |= (uint)flag;
            }
        }
        return value;
    }

    private SqliteConnection Open()
    {
        SqliteConnection connection = new(_connectionString);
        connection.Open();
        using SqliteCommand pragmas = connection.CreateCommand();
        pragmas.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragmas.ExecuteNonQuery();
        return connection;
    }

    /// <summary>
    /// Mirrors the packed account class out into its per-flag columns. <c>account_class</c>
    /// itself is still written, so the raw value stays visible, but the columns are what
    /// <see cref="ReadAccountClass"/> reads back for every known bit.
    /// </summary>
    private static void WriteAccountClassColumns(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalPlayerProfile profile)
    {
        string assignments = string.Join(", ", AccountClassInfo.Columns.Select(
            entry => $"{entry.Column} = ${entry.Column}"));
        using SqliteCommand command = CreateCommand(connection, transaction,
            $"UPDATE players SET {assignments} WHERE user_id = $user_id;");
        foreach ((string column, AccountClassFlags flag) in AccountClassInfo.Columns)
        {
            Add(command, $"${column}", (profile.AccountClass & (uint)flag) != 0 ? 1 : 0);
        }
        Add(command, "$user_id", profile.UserId);
        command.ExecuteNonQuery();
    }

    private static LocalPlayerProfile? Load(
        SqliteConnection connection,
        SqliteTransaction transaction,
        uint userId)
    {
        LocalPlayerProfile profile;
        using (SqliteCommand command = CreateCommand(connection, transaction,
                   "SELECT * FROM players WHERE user_id = $user_id;"))
        {
            Add(command, "$user_id", userId);
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }
            profile = new LocalPlayerProfile
            {
                UserId = ReadUInt32(reader, "user_id"),
                AccountId = reader.GetString(reader.GetOrdinal("account_id")),
                SecondaryId = reader.GetString(reader.GetOrdinal("secondary_id")),
                Nickname = reader.GetString(reader.GetOrdinal("nickname")),
                State = ReadByte(reader, "state"),
                ProfileCode = ReadUInt16(reader, "profile_code"),
                Gender = ReadByte(reader, "gender"),
                IconId = ReadNullableUInt32(reader, "icon_id"),
                ProfileFlags = ReadUInt32(reader, "profile_flags"),
                AccountClass = ReadAccountClass(reader),
                LockState = (AccountLockState)ReadUInt32(reader, "lock_state"),
                LockReason = reader.GetString(reader.GetOrdinal("lock_reason")),
                Reserved = ReadUInt16(reader, "reserved"),
                Collection = [],
                AvailableCourseIds = [],
                CourseRecords = [],
                Messenger = new MessengerBook(),
                Roster = [],
                Progress = new LocalPlayerProgress(),
                Inventory = new LocalPlayerInventory
                {
                    State = ReadUInt32(reader, "inventory_state")
                }
            };
        }

        LoadProgress(connection, transaction, profile);
        LoadCollection(connection, transaction, profile);
        LoadCourses(connection, transaction, profile);
        LoadInventory(connection, transaction, profile);
        LoadMessenger(connection, transaction, profile);
        LoadRoster(connection, transaction, profile);
        profile.Validate();
        return profile;
    }

    private static void LoadProgress(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalPlayerProfile profile)
    {
        using SqliteCommand command = CreateCommand(connection, transaction,
            "SELECT * FROM player_progress WHERE user_id = $user_id;");
        Add(command, "$user_id", profile.UserId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidDataException(
                $"SQLite player {profile.UserId} has no progress record.");
        }
        uint[] misc = new uint[LocalPlayerProgress.MiscStatisticCount];
        for (int index = 0; index < misc.Length; index++)
        {
            misc[index] = ReadUInt32(reader, $"misc_{index}");
        }
        profile.Progress = new LocalPlayerProgress
        {
            Level = ReadUInt32(reader, "level"),
            Experience = ReadUInt32(reader, "experience"),
            Money = ReadUInt32(reader, "money"),
            Cash = ReadUInt32(reader, "cash"),
            AwardPoints = ReadUInt32(reader, "award_points"),
            Wins = ReadUInt32(reader, "wins"),
            Losses = ReadUInt32(reader, "losses"),
            Draws = ReadUInt32(reader, "draws"),
            FreemodeBest5Key = ReadUInt32(reader, "freemode_best_5k"),
            FreemodeBest7Key = ReadUInt32(reader, "freemode_best_7k"),
            RankingBest5Key = ReadUInt32(reader, "ranking_best_5k"),
            RankingBest7Key = ReadUInt32(reader, "ranking_best_7k"),
            MaxCombo = ReadUInt32(reader, "max_combo"),
            HighestAccuracy = reader.GetDouble(reader.GetOrdinal("highest_accuracy")),
            AverageAccuracy = reader.GetDouble(reader.GetOrdinal("average_accuracy")),
            MiscStatistics = misc
        };
    }

    private static void LoadCollection(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalPlayerProfile profile)
    {
        using SqliteCommand command = CreateCommand(connection, transaction,
            """
            SELECT code, value
            FROM collection_entries
            WHERE user_id = $user_id
            ORDER BY position;
            """);
        Add(command, "$user_id", profile.UserId);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            profile.Collection.Add(new CollectionEntry(
                ReadUInt16(reader, "code"), ReadUInt16(reader, "value")));
        }
    }

    private static void LoadCourses(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalPlayerProfile profile)
    {
        using (SqliteCommand command = CreateCommand(connection, transaction,
                   """
                   SELECT course_id
                   FROM available_courses
                   WHERE user_id = $user_id
                   ORDER BY position;
                   """))
        {
            Add(command, "$user_id", profile.UserId);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                profile.AvailableCourseIds.Add(ReadUInt16(reader, "course_id"));
            }
        }

        using (SqliteCommand command = CreateCommand(connection, transaction,
                   """
                   SELECT course_id, key_mode, score, combo, clears
                   FROM course_records
                   WHERE user_id = $user_id
                   ORDER BY course_id, key_mode;
                   """))
        {
            Add(command, "$user_id", profile.UserId);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                profile.CourseRecords.Add(new CourseRecord
                {
                    CourseId = ReadUInt16(reader, "course_id"),
                    KeyMode = (byte)ReadUInt32(reader, "key_mode"),
                    Score = ReadUInt32(reader, "score"),
                    Combo = ReadUInt32(reader, "combo"),
                    Clears = ReadUInt32(reader, "clears")
                });
            }
        }
    }

    private static void LoadInventory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalPlayerProfile profile)
    {
        using SqliteCommand command = CreateCommand(connection, transaction,
            """
            SELECT section, slot, item_id, expiration, sender_user_id
            FROM inventory_entries
            WHERE user_id = $user_id
            ORDER BY section, slot;
            """);
        Add(command, "$user_id", profile.UserId);
        using SqliteDataReader reader = command.ExecuteReader();
        List<TimedInventoryItem> itemBox = [];
        while (reader.Read())
        {
            string section = reader.GetString(reader.GetOrdinal("section"));
            int slot = reader.GetInt32(reader.GetOrdinal("slot"));
            uint itemId = ReadUInt32(reader, "item_id");
            uint expiration = ReadUInt32(reader, "expiration");
            uint sender = ReadUInt32(reader, "sender_user_id");
            switch (section)
            {
                case "default":
                    profile.Inventory.DefaultItems.Add(new InventoryItemSlot(slot, itemId));
                    break;
                case "event":
                    profile.Inventory.EventItems.Add(new InventoryItemSlot(slot, itemId));
                    break;
                case "shop":
                    profile.Inventory.ShopItems.Add(
                        new TimedInventorySlot(slot, itemId, expiration));
                    break;
                case "present":
                    profile.Inventory.PresentItems.Add(
                        new PresentInventorySlot(slot, itemId, sender, expiration));
                    break;
                case "mount":
                    profile.Inventory.MountItems.Add(
                        new TimedInventorySlot(slot, itemId, expiration));
                    break;
                case "booster":
                    profile.Inventory.ActiveBoosters.Add(itemId);
                    break;
                case "item_box":
                    while (itemBox.Count <= slot)
                    {
                        itemBox.Add(TimedInventoryItem.Empty);
                    }
                    itemBox[slot] = new TimedInventoryItem(itemId, expiration);
                    break;
                default:
                    throw new InvalidDataException(
                        $"SQLite player {profile.UserId} has unknown inventory section " +
                        $"'{section}'.");
            }
        }
        if (itemBox.Any(item => item == TimedInventoryItem.Empty))
        {
            throw new InvalidDataException(
                $"SQLite player {profile.UserId} has a sparse item-box ordering.");
        }
        profile.Inventory.SetItemBox(itemBox);
    }

    private static void LoadMessenger(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalPlayerProfile profile)
    {
        using (SqliteCommand command = CreateCommand(connection, transaction,
                   """
                   SELECT target_user_id, group_index
                   FROM messenger_contacts
                   WHERE user_id = $user_id
                   ORDER BY position;
                   """))
        {
            Add(command, "$user_id", profile.UserId);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                profile.Messenger.Contacts.Add(new MessengerContactEntry
                {
                    UserId = ReadUInt32(reader, "target_user_id"),
                    GroupIndex = ReadUInt16(reader, "group_index")
                });
            }
        }
        using (SqliteCommand command = CreateCommand(connection, transaction,
                   """
                   SELECT target_user_id
                   FROM messenger_blocked
                   WHERE user_id = $user_id
                   ORDER BY position;
                   """))
        {
            Add(command, "$user_id", profile.UserId);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                profile.Messenger.BlockedUserIds.Add(
                    ReadUInt32(reader, "target_user_id"));
            }
        }
        using (SqliteCommand command = CreateCommand(connection, transaction,
                   """
                   SELECT name
                   FROM messenger_groups
                   WHERE user_id = $user_id
                   ORDER BY position;
                   """))
        {
            Add(command, "$user_id", profile.UserId);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                profile.Messenger.GroupNames.Add(
                    reader.GetString(reader.GetOrdinal("name")));
            }
        }
    }

    private static void LoadRoster(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalPlayerProfile profile)
    {
        using SqliteCommand command = CreateCommand(connection, transaction,
            """
            SELECT roster_user_id, account_id, nickname, gender, icon_id,
                   level, account_class, online
            FROM roster_users
            WHERE owner_user_id = $user_id
            ORDER BY position;
            """);
        Add(command, "$user_id", profile.UserId);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            profile.Roster.Add(new RosterUser
            {
                UserId = ReadUInt32(reader, "roster_user_id"),
                AccountId = reader.GetString(reader.GetOrdinal("account_id")),
                Nickname = reader.GetString(reader.GetOrdinal("nickname")),
                Gender = ReadByte(reader, "gender"),
                IconId = ReadNullableUInt32(reader, "icon_id"),
                Level = ReadUInt32(reader, "level"),
                AccountClass = ReadUInt32(reader, "account_class"),
                Online = ReadBool(reader, "online")
            });
        }
    }

    private static void Save(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalPlayerProfile profile)
    {
        string timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        using (SqliteCommand command = CreateCommand(connection, transaction,
                   """
                   INSERT INTO players (
                       user_id, account_id, secondary_id, nickname, state, profile_code,
                       gender, icon_id, profile_flags, account_class, lock_state,
                       lock_reason, reserved, inventory_state, created_utc, updated_utc)
                   VALUES (
                       $user_id, $account_id, $secondary_id, $nickname, $state,
                       $profile_code, $gender, $icon_id, $profile_flags, $account_class,
                       $lock_state, $lock_reason, $reserved, $inventory_state,
                       $created_utc, $updated_utc)
                   ON CONFLICT(user_id) DO UPDATE SET
                       account_id = excluded.account_id,
                       secondary_id = excluded.secondary_id,
                       nickname = excluded.nickname,
                       state = excluded.state,
                       profile_code = excluded.profile_code,
                       gender = excluded.gender,
                       icon_id = excluded.icon_id,
                       profile_flags = excluded.profile_flags,
                       account_class = excluded.account_class,
                       lock_state = excluded.lock_state,
                       lock_reason = excluded.lock_reason,
                       reserved = excluded.reserved,
                       inventory_state = excluded.inventory_state,
                       updated_utc = excluded.updated_utc;
                   """))
        {
            Add(command, "$user_id", profile.UserId);
            Add(command, "$account_id", profile.AccountId);
            Add(command, "$secondary_id", profile.SecondaryId);
            Add(command, "$nickname", profile.Nickname);
            Add(command, "$state", profile.State);
            Add(command, "$profile_code", profile.ProfileCode);
            Add(command, "$gender", profile.Gender);
            Add(command, "$icon_id", profile.IconId);
            Add(command, "$profile_flags", profile.ProfileFlags);
            Add(command, "$account_class", profile.AccountClass);
            Add(command, "$lock_state", (uint)profile.LockState);
            Add(command, "$lock_reason", profile.LockReason);
            Add(command, "$reserved", profile.Reserved);
            Add(command, "$inventory_state", profile.Inventory.State);
            Add(command, "$created_utc", timestamp);
            Add(command, "$updated_utc", timestamp);
            command.ExecuteNonQuery();
        }

        WriteAccountClassColumns(connection, transaction, profile);
        SaveProgress(connection, transaction, profile);
        ReplaceCollections(connection, transaction, profile);
        ReplaceCourses(connection, transaction, profile);
        ReplaceInventory(connection, transaction, profile);
        ReplaceMessenger(connection, transaction, profile);
        ReplaceRoster(connection, transaction, profile);
    }

    private static void SaveProgress(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalPlayerProfile profile)
    {
        LocalPlayerProgress progress = profile.Progress;
        using SqliteCommand command = CreateCommand(connection, transaction,
            """
            INSERT INTO player_progress (
                user_id, level, experience, money, cash, award_points,
                wins, losses, draws, freemode_best_5k, freemode_best_7k,
                ranking_best_5k, ranking_best_7k, max_combo,
                highest_accuracy, average_accuracy,
                misc_0, misc_1, misc_2, misc_3, misc_4,
                misc_5, misc_6, misc_7, misc_8, misc_9)
            VALUES (
                $user_id, $level, $experience, $money, $cash, $award_points,
                $wins, $losses, $draws, $freemode_best_5k, $freemode_best_7k,
                $ranking_best_5k, $ranking_best_7k, $max_combo,
                $highest_accuracy, $average_accuracy,
                $misc_0, $misc_1, $misc_2, $misc_3, $misc_4,
                $misc_5, $misc_6, $misc_7, $misc_8, $misc_9)
            ON CONFLICT(user_id) DO UPDATE SET
                level = excluded.level,
                experience = excluded.experience,
                money = excluded.money,
                cash = excluded.cash,
                award_points = excluded.award_points,
                wins = excluded.wins,
                losses = excluded.losses,
                draws = excluded.draws,
                freemode_best_5k = excluded.freemode_best_5k,
                freemode_best_7k = excluded.freemode_best_7k,
                ranking_best_5k = excluded.ranking_best_5k,
                ranking_best_7k = excluded.ranking_best_7k,
                max_combo = excluded.max_combo,
                highest_accuracy = excluded.highest_accuracy,
                average_accuracy = excluded.average_accuracy,
                misc_0 = excluded.misc_0, misc_1 = excluded.misc_1,
                misc_2 = excluded.misc_2, misc_3 = excluded.misc_3,
                misc_4 = excluded.misc_4, misc_5 = excluded.misc_5,
                misc_6 = excluded.misc_6, misc_7 = excluded.misc_7,
                misc_8 = excluded.misc_8, misc_9 = excluded.misc_9;
            """);
        Add(command, "$user_id", profile.UserId);
        Add(command, "$level", progress.Level);
        Add(command, "$experience", progress.Experience);
        Add(command, "$money", progress.Money);
        Add(command, "$cash", progress.Cash);
        Add(command, "$award_points", progress.AwardPoints);
        Add(command, "$wins", progress.Wins);
        Add(command, "$losses", progress.Losses);
        Add(command, "$draws", progress.Draws);
        Add(command, "$freemode_best_5k", progress.FreemodeBest5Key);
        Add(command, "$freemode_best_7k", progress.FreemodeBest7Key);
        Add(command, "$ranking_best_5k", progress.RankingBest5Key);
        Add(command, "$ranking_best_7k", progress.RankingBest7Key);
        Add(command, "$max_combo", progress.MaxCombo);
        Add(command, "$highest_accuracy", progress.HighestAccuracy);
        Add(command, "$average_accuracy", progress.AverageAccuracy);
        for (int index = 0; index < LocalPlayerProgress.MiscStatisticCount; index++)
        {
            Add(command, $"$misc_{index}", progress.MiscStatistics[index]);
        }
        command.ExecuteNonQuery();
    }

    private static void ReplaceCollections(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalPlayerProfile profile)
    {
        DeleteChildren(connection, transaction, "collection_entries", profile.UserId);
        for (int position = 0; position < profile.Collection.Count; position++)
        {
            CollectionEntry entry = profile.Collection[position];
            using SqliteCommand command = CreateCommand(connection, transaction,
                """
                INSERT INTO collection_entries (user_id, position, code, value)
                VALUES ($user_id, $position, $code, $value);
                """);
            Add(command, "$user_id", profile.UserId);
            Add(command, "$position", position);
            Add(command, "$code", entry.Code);
            Add(command, "$value", entry.Value);
            command.ExecuteNonQuery();
        }
    }

    private static void ReplaceCourses(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalPlayerProfile profile)
    {
        DeleteChildren(connection, transaction, "available_courses", profile.UserId);
        DeleteChildren(connection, transaction, "course_records", profile.UserId);
        for (int position = 0; position < profile.AvailableCourseIds.Count; position++)
        {
            using SqliteCommand command = CreateCommand(connection, transaction,
                """
                INSERT INTO available_courses (user_id, position, course_id)
                VALUES ($user_id, $position, $course_id);
                """);
            Add(command, "$user_id", profile.UserId);
            Add(command, "$position", position);
            Add(command, "$course_id", profile.AvailableCourseIds[position]);
            command.ExecuteNonQuery();
        }
        foreach (CourseRecord record in profile.CourseRecords)
        {
            using SqliteCommand command = CreateCommand(connection, transaction,
                """
                INSERT INTO course_records
                    (user_id, course_id, key_mode, score, combo, clears)
                VALUES ($user_id, $course_id, $key_mode, $score, $combo, $clears);
                """);
            Add(command, "$user_id", profile.UserId);
            Add(command, "$course_id", record.CourseId);
            Add(command, "$key_mode", record.KeyMode);
            Add(command, "$score", record.Score);
            Add(command, "$combo", record.Combo);
            Add(command, "$clears", record.Clears);
            command.ExecuteNonQuery();
        }
    }

    private static void ReplaceInventory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalPlayerProfile profile)
    {
        DeleteChildren(connection, transaction, "inventory_entries", profile.UserId);
        foreach (InventoryItemSlot item in profile.Inventory.DefaultItems)
        {
            InsertInventory(connection, transaction, profile.UserId,
                "default", item.Slot, item.ItemId, 0, 0);
        }
        foreach (InventoryItemSlot item in profile.Inventory.EventItems)
        {
            InsertInventory(connection, transaction, profile.UserId,
                "event", item.Slot, item.ItemId, 0, 0);
        }
        foreach (TimedInventorySlot item in profile.Inventory.ShopItems)
        {
            InsertInventory(connection, transaction, profile.UserId,
                "shop", item.Slot, item.ItemId, item.Expiration, 0);
        }
        foreach (PresentInventorySlot item in profile.Inventory.PresentItems)
        {
            InsertInventory(connection, transaction, profile.UserId,
                "present", item.Slot, item.ItemId, item.Expiration, item.SenderUserId);
        }
        foreach (TimedInventorySlot item in profile.Inventory.MountItems)
        {
            InsertInventory(connection, transaction, profile.UserId,
                "mount", item.Slot, item.ItemId, item.Expiration, 0);
        }
        IReadOnlyList<TimedInventoryItem> box = profile.Inventory.ItemBoxItems();
        for (int slot = 0; slot < box.Count; slot++)
        {
            InsertInventory(connection, transaction, profile.UserId,
                "item_box", slot, box[slot].ItemId, box[slot].Expiration, 0);
        }
        // Boosters the player has already spent from the box but not yet used up in a
        // song. Stored as another section rather than a new table, so no migration.
        for (int slot = 0; slot < profile.Inventory.ActiveBoosters.Count; slot++)
        {
            InsertInventory(connection, transaction, profile.UserId,
                "booster", slot, profile.Inventory.ActiveBoosters[slot], 0, 0);
        }
    }

    private static void InsertInventory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        uint userId,
        string section,
        int slot,
        uint itemId,
        uint expiration,
        uint senderUserId)
    {
        using SqliteCommand command = CreateCommand(connection, transaction,
            """
            INSERT INTO inventory_entries (
                user_id, section, slot, item_id, expiration, sender_user_id)
            VALUES ($user_id, $section, $slot, $item_id, $expiration, $sender_user_id);
            """);
        Add(command, "$user_id", userId);
        Add(command, "$section", section);
        Add(command, "$slot", slot);
        Add(command, "$item_id", itemId);
        Add(command, "$expiration", expiration);
        Add(command, "$sender_user_id", senderUserId);
        command.ExecuteNonQuery();
    }

    private static void ReplaceMessenger(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalPlayerProfile profile)
    {
        DeleteChildren(connection, transaction, "messenger_contacts", profile.UserId);
        DeleteChildren(connection, transaction, "messenger_blocked", profile.UserId);
        DeleteChildren(connection, transaction, "messenger_groups", profile.UserId);
        for (int position = 0; position < profile.Messenger.Contacts.Count; position++)
        {
            MessengerContactEntry entry = profile.Messenger.Contacts[position];
            using SqliteCommand command = CreateCommand(connection, transaction,
                """
                INSERT INTO messenger_contacts (
                    user_id, position, target_user_id, group_index)
                VALUES ($user_id, $position, $target_user_id, $group_index);
                """);
            Add(command, "$user_id", profile.UserId);
            Add(command, "$position", position);
            Add(command, "$target_user_id", entry.UserId);
            Add(command, "$group_index", entry.GroupIndex);
            command.ExecuteNonQuery();
        }
        for (int position = 0; position < profile.Messenger.BlockedUserIds.Count; position++)
        {
            using SqliteCommand command = CreateCommand(connection, transaction,
                """
                INSERT INTO messenger_blocked (user_id, position, target_user_id)
                VALUES ($user_id, $position, $target_user_id);
                """);
            Add(command, "$user_id", profile.UserId);
            Add(command, "$position", position);
            Add(command, "$target_user_id", profile.Messenger.BlockedUserIds[position]);
            command.ExecuteNonQuery();
        }
        for (int position = 0; position < profile.Messenger.GroupNames.Count; position++)
        {
            using SqliteCommand command = CreateCommand(connection, transaction,
                """
                INSERT INTO messenger_groups (user_id, position, name)
                VALUES ($user_id, $position, $name);
                """);
            Add(command, "$user_id", profile.UserId);
            Add(command, "$position", position);
            Add(command, "$name", profile.Messenger.GroupNames[position]);
            command.ExecuteNonQuery();
        }
    }

    private static void ReplaceRoster(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalPlayerProfile profile)
    {
        using (SqliteCommand delete = CreateCommand(connection, transaction,
                   "DELETE FROM roster_users WHERE owner_user_id = $user_id;"))
        {
            Add(delete, "$user_id", profile.UserId);
            delete.ExecuteNonQuery();
        }
        for (int position = 0; position < profile.Roster.Count; position++)
        {
            RosterUser user = profile.Roster[position];
            using SqliteCommand command = CreateCommand(connection, transaction,
                """
                INSERT INTO roster_users (
                    owner_user_id, position, roster_user_id, account_id, nickname,
                    gender, icon_id, level, account_class, online)
                VALUES (
                    $owner_user_id, $position, $roster_user_id, $account_id, $nickname,
                    $gender, $icon_id, $level, $account_class, $online);
                """);
            Add(command, "$owner_user_id", profile.UserId);
            Add(command, "$position", position);
            Add(command, "$roster_user_id", user.UserId);
            Add(command, "$account_id", user.AccountId);
            Add(command, "$nickname", user.Nickname);
            Add(command, "$gender", user.Gender);
            Add(command, "$icon_id", user.IconId);
            Add(command, "$level", user.Level);
            Add(command, "$account_class", user.AccountClass);
            Add(command, "$online", user.Online);
            command.ExecuteNonQuery();
        }
    }

    private static void InsertStageScore(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StageScoreRecord score)
    {
        using SqliteCommand command = CreateCommand(connection, transaction,
            """
            INSERT INTO stage_scores (
                user_id, played_utc, song_id, course_id, course_stage, room_id,
                key_mode, difficulty, match_mode, session_token, client_flags,
                total_notes,
                judgment_0, judgment_1, judgment_2, judgment_3, judgment_4,
                judgment_5, judgment_6, judgment_7, judgment_8, judgment_9,
                judgment_10, judgment_11, judgment_12,
                gauge, encoded_combo, current_combo, max_combo, auxiliary_value,
                result_state, tail, failed, full_combo, notes_hit, breaks,
                score, bonus_score, accuracy, rank, money_awarded, experience_awarded)
            VALUES (
                $user_id, $played_utc, $song_id, $course_id, $course_stage, $room_id,
                $key_mode, $difficulty, $match_mode, $session_token, $client_flags,
                $total_notes,
                $judgment_0, $judgment_1, $judgment_2, $judgment_3, $judgment_4,
                $judgment_5, $judgment_6, $judgment_7, $judgment_8, $judgment_9,
                $judgment_10, $judgment_11, $judgment_12,
                $gauge, $encoded_combo, $current_combo, $max_combo, $auxiliary_value,
                $result_state, $tail, $failed, $full_combo, $notes_hit, $breaks,
                $score, $bonus_score, $accuracy, $rank, $money_awarded,
                $experience_awarded);
            """);
        Add(command, "$user_id", score.UserId);
        Add(command, "$played_utc",
            score.PlayedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Add(command, "$song_id", score.SongId);
        Add(command, "$course_id", score.CourseId);
        Add(command, "$course_stage", score.CourseStage);
        Add(command, "$room_id", score.RoomId);
        Add(command, "$key_mode", score.KeyMode);
        Add(command, "$difficulty", score.Difficulty);
        Add(command, "$match_mode", score.MatchMode);
        Add(command, "$session_token", score.SessionToken);
        Add(command, "$client_flags", score.ClientFlags);
        Add(command, "$total_notes", score.TotalNotes);
        for (int index = 0; index < score.Judgments.Length; index++)
        {
            Add(command, $"$judgment_{index}", score.Judgments[index]);
        }
        Add(command, "$gauge", score.Gauge);
        Add(command, "$encoded_combo", score.EncodedCombo);
        Add(command, "$current_combo", score.CurrentCombo);
        Add(command, "$max_combo", score.MaxCombo);
        Add(command, "$auxiliary_value", score.AuxiliaryValue);
        Add(command, "$result_state", score.ResultState);
        Add(command, "$tail", score.Tail);
        Add(command, "$failed", score.Failed);
        Add(command, "$full_combo", score.FullCombo);
        Add(command, "$notes_hit", score.NotesHit);
        Add(command, "$breaks", score.Breaks);
        Add(command, "$score", score.Score);
        Add(command, "$bonus_score", score.BonusScore);
        Add(command, "$accuracy", score.Accuracy);
        Add(command, "$rank", (byte)score.Rank);
        Add(command, "$money_awarded", score.MoneyAwarded);
        Add(command, "$experience_awarded", score.ExperienceAwarded);
        command.ExecuteNonQuery();
    }

    private static void DeleteChildren(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        uint userId)
    {
        // Table names are internal constants supplied only by this class; values remain
        // parameters. Keeping this helper avoids accidentally omitting a child section.
        using SqliteCommand command = CreateCommand(connection, transaction,
            $"DELETE FROM {table} WHERE user_id = $user_id;");
        Add(command, "$user_id", userId);
        command.ExecuteNonQuery();
    }

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string text)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        return command;
    }

    private static void Add(SqliteCommand command, string name, object? value)
    {
        object databaseValue = value switch
        {
            null => DBNull.Value,
            uint unsigned => (long)unsigned,
            ushort unsigned => (long)unsigned,
            byte unsigned => (long)unsigned,
            bool boolean => boolean ? 1L : 0L,
            _ => value
        };
        command.Parameters.AddWithValue(name, databaseValue);
    }

    private static uint ReadUInt32(SqliteDataReader reader, string name) =>
        checked((uint)reader.GetInt64(reader.GetOrdinal(name)));

    private static ushort ReadUInt16(SqliteDataReader reader, string name) =>
        checked((ushort)reader.GetInt64(reader.GetOrdinal(name)));

    private static byte ReadByte(SqliteDataReader reader, string name) =>
        checked((byte)reader.GetInt64(reader.GetOrdinal(name)));

    private static bool ReadBool(SqliteDataReader reader, string name) =>
        reader.GetInt64(reader.GetOrdinal(name)) != 0;

    private static uint? ReadNullableUInt32(SqliteDataReader reader, string name)
    {
        int ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : checked((uint)reader.GetInt64(ordinal));
    }

    private static ushort? ReadNullableUInt16(SqliteDataReader reader, string name)
    {
        int ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : checked((ushort)reader.GetInt64(ordinal));
    }

    private static int? ReadNullableInt32(SqliteDataReader reader, string name)
    {
        int ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private const string SchemaSql =
        """
        CREATE TABLE IF NOT EXISTS players (
            user_id          INTEGER PRIMARY KEY CHECK(user_id BETWEEN 1 AND 4294967295),
            account_id       TEXT NOT NULL COLLATE NOCASE UNIQUE,
            secondary_id     TEXT NOT NULL DEFAULT '',
            nickname         TEXT NOT NULL COLLATE NOCASE UNIQUE,
            state            INTEGER NOT NULL,
            profile_code     INTEGER NOT NULL,
            gender           INTEGER NOT NULL,
            icon_id          INTEGER NULL,
            profile_flags    INTEGER NOT NULL,
            account_class    INTEGER NOT NULL,
            reserved         INTEGER NOT NULL,
            inventory_state  INTEGER NOT NULL,
            created_utc      TEXT NOT NULL,
            updated_utc      TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS player_progress (
            user_id             INTEGER PRIMARY KEY REFERENCES players(user_id) ON DELETE CASCADE,
            level               INTEGER NOT NULL,
            experience          INTEGER NOT NULL,
            money               INTEGER NOT NULL,
            cash                INTEGER NOT NULL,
            award_points        INTEGER NOT NULL,
            wins                INTEGER NOT NULL,
            losses              INTEGER NOT NULL,
            draws               INTEGER NOT NULL,
            freemode_best_5k    INTEGER NOT NULL,
            freemode_best_7k    INTEGER NOT NULL,
            ranking_best_5k     INTEGER NOT NULL,
            ranking_best_7k     INTEGER NOT NULL,
            max_combo           INTEGER NOT NULL,
            highest_accuracy    REAL NOT NULL,
            average_accuracy    REAL NOT NULL,
            misc_0 INTEGER NOT NULL, misc_1 INTEGER NOT NULL,
            misc_2 INTEGER NOT NULL, misc_3 INTEGER NOT NULL,
            misc_4 INTEGER NOT NULL, misc_5 INTEGER NOT NULL,
            misc_6 INTEGER NOT NULL, misc_7 INTEGER NOT NULL,
            misc_8 INTEGER NOT NULL, misc_9 INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS player_credentials (
            user_id       INTEGER PRIMARY KEY REFERENCES players(user_id) ON DELETE CASCADE,
            algorithm     TEXT NOT NULL,
            iterations    INTEGER NOT NULL CHECK(iterations > 0),
            salt          BLOB NOT NULL,
            password_hash BLOB NOT NULL,
            updated_utc   TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS collection_entries (
            user_id   INTEGER NOT NULL REFERENCES players(user_id) ON DELETE CASCADE,
            position  INTEGER NOT NULL,
            code      INTEGER NOT NULL,
            value     INTEGER NOT NULL,
            PRIMARY KEY(user_id, position)
        );

        CREATE TABLE IF NOT EXISTS available_courses (
            user_id   INTEGER NOT NULL REFERENCES players(user_id) ON DELETE CASCADE,
            position  INTEGER NOT NULL,
            course_id INTEGER NOT NULL,
            PRIMARY KEY(user_id, position),
            UNIQUE(user_id, course_id)
        );

        CREATE TABLE IF NOT EXISTS course_records (
            user_id   INTEGER NOT NULL REFERENCES players(user_id) ON DELETE CASCADE,
            course_id INTEGER NOT NULL,
            -- 5 = SEOUL, 7 = TOKYO. Part of the key: the two channels serve different
            -- charts, so each keeps its own best score, combo and clear count.
            key_mode  INTEGER NOT NULL DEFAULT 5,
            score     INTEGER NOT NULL,
            combo     INTEGER NOT NULL,
            clears    INTEGER NOT NULL,
            PRIMARY KEY(user_id, course_id, key_mode)
        );

        CREATE TABLE IF NOT EXISTS inventory_entries (
            user_id        INTEGER NOT NULL REFERENCES players(user_id) ON DELETE CASCADE,
            section        TEXT NOT NULL CHECK(section IN
                               ('default','event','shop','present','mount','item_box')),
            slot           INTEGER NOT NULL,
            item_id        INTEGER NOT NULL,
            expiration     INTEGER NOT NULL DEFAULT 0,
            sender_user_id INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(user_id, section, slot)
        );
        CREATE INDEX IF NOT EXISTS ix_inventory_item
            ON inventory_entries(user_id, item_id);

        CREATE TABLE IF NOT EXISTS messenger_contacts (
            user_id        INTEGER NOT NULL REFERENCES players(user_id) ON DELETE CASCADE,
            position       INTEGER NOT NULL,
            target_user_id INTEGER NOT NULL,
            group_index    INTEGER NOT NULL,
            PRIMARY KEY(user_id, position),
            UNIQUE(user_id, target_user_id)
        );

        CREATE TABLE IF NOT EXISTS messenger_blocked (
            user_id        INTEGER NOT NULL REFERENCES players(user_id) ON DELETE CASCADE,
            position       INTEGER NOT NULL,
            target_user_id INTEGER NOT NULL,
            PRIMARY KEY(user_id, position),
            UNIQUE(user_id, target_user_id)
        );

        CREATE TABLE IF NOT EXISTS messenger_groups (
            user_id   INTEGER NOT NULL REFERENCES players(user_id) ON DELETE CASCADE,
            position  INTEGER NOT NULL,
            name      TEXT NOT NULL,
            PRIMARY KEY(user_id, position)
        );

        CREATE TABLE IF NOT EXISTS roster_users (
            owner_user_id  INTEGER NOT NULL REFERENCES players(user_id) ON DELETE CASCADE,
            position       INTEGER NOT NULL,
            roster_user_id INTEGER NOT NULL,
            account_id     TEXT NOT NULL,
            nickname       TEXT NOT NULL COLLATE NOCASE,
            gender         INTEGER NOT NULL,
            icon_id        INTEGER NULL,
            level          INTEGER NOT NULL,
            account_class  INTEGER NOT NULL,
            online         INTEGER NOT NULL,
            PRIMARY KEY(owner_user_id, roster_user_id),
            UNIQUE(owner_user_id, position),
            UNIQUE(owner_user_id, nickname)
        );

        CREATE TABLE IF NOT EXISTS stage_scores (
            score_id              INTEGER PRIMARY KEY AUTOINCREMENT,
            user_id               INTEGER NOT NULL REFERENCES players(user_id) ON DELETE CASCADE,
            played_utc            TEXT NOT NULL,
            song_id               INTEGER NULL,
            course_id             INTEGER NULL,
            course_stage          INTEGER NULL,
            room_id               INTEGER NULL,
            key_mode              INTEGER NOT NULL,
            difficulty            INTEGER NOT NULL,
            match_mode            INTEGER NOT NULL,
            session_token         INTEGER NOT NULL,
            client_flags          INTEGER NOT NULL,
            total_notes           INTEGER NOT NULL,
            judgment_0 INTEGER NOT NULL, judgment_1 INTEGER NOT NULL,
            judgment_2 INTEGER NOT NULL, judgment_3 INTEGER NOT NULL,
            judgment_4 INTEGER NOT NULL, judgment_5 INTEGER NOT NULL,
            judgment_6 INTEGER NOT NULL, judgment_7 INTEGER NOT NULL,
            judgment_8 INTEGER NOT NULL, judgment_9 INTEGER NOT NULL,
            judgment_10 INTEGER NOT NULL, judgment_11 INTEGER NOT NULL,
            judgment_12 INTEGER NOT NULL,
            gauge                 REAL NOT NULL,
            encoded_combo         INTEGER NOT NULL,
            current_combo         INTEGER NOT NULL,
            max_combo             INTEGER NOT NULL,
            auxiliary_value       INTEGER NOT NULL,
            result_state          INTEGER NOT NULL,
            tail                  INTEGER NOT NULL,
            failed                INTEGER NOT NULL,
            full_combo            INTEGER NOT NULL,
            notes_hit             INTEGER NOT NULL,
            breaks                INTEGER NOT NULL,
            score                 INTEGER NOT NULL,
            bonus_score           INTEGER NOT NULL,
            accuracy              REAL NOT NULL,
            rank                  INTEGER NOT NULL,
            money_awarded         INTEGER NOT NULL,
            experience_awarded    INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_stage_scores_user_time
            ON stage_scores(user_id, played_utc DESC);
        CREATE INDEX IF NOT EXISTS ix_stage_scores_song_best
            ON stage_scores(user_id, song_id, key_mode, difficulty, score DESC);
        """;
}
