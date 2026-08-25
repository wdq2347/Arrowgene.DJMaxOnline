using Microsoft.Data.Sqlite;

namespace Arrowgene.DJMaxOnline.Server.Korea400;

/// <summary>One course ranking row removed because its recorded attempts never cleared.</summary>
public sealed record RemovedInvalidCourseClear(
    uint UserId,
    string Nickname,
    ushort CourseId,
    string CourseName,
    byte KeyMode,
    uint Score,
    uint Combo,
    uint Clears,
    int Attempts);

/// <summary>Summary returned by the one-shot invalid-course maintenance command.</summary>
public sealed record InvalidCourseClearRemovalReport(
    int Examined,
    int Removed,
    int Valid,
    int Unverifiable,
    IReadOnlyList<RemovedInvalidCourseClear> RemovedRecords);

/// <summary>
/// Reconstructs course attempts from detailed stage history and removes ranking records
/// which can be proven to have come only from runs that missed the course objectives.
/// </summary>
public static class CourseRecordMaintenance
{
    public static InvalidCourseClearRemovalReport RemoveInvalidCourseClears(
        this SqlitePlayerRepository repository,
        CourseCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.Count == 0)
        {
            throw new InvalidDataException(
                "Cannot validate course records without CourseSection.ini.");
        }

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = repository.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = true,
            DefaultTimeout = 5
        };
        using SqliteConnection connection = new(builder.ToString());
        connection.Open();
        using SqliteTransaction transaction = connection.BeginTransaction();

        List<StoredCourseRecord> records = ReadRankingRecords(connection, transaction);
        List<RemovedInvalidCourseClear> removed = [];
        int valid = 0;
        int unverifiable = 0;

        foreach (StoredCourseRecord record in records)
        {
            if (!catalog.TryGet(record.CourseId, out CourseDefinition? course) ||
                course == null || course.Stages.Count == 0)
            {
                unverifiable++;
                continue;
            }

            List<CompletedCourseAttempt> attempts = ReconstructAttempts(
                connection, transaction, record, course);
            if (attempts.Any(attempt => attempt.Passed))
            {
                valid++;
                continue;
            }

            // course_records has no timestamp or attempt id. Only delete when the detailed
            // history accounts for every claimed clear and reproduces both independently
            // stored ranking maxima. Anything less is ambiguous old data and is preserved.
            bool completeEvidence =
                (ulong)attempts.Count == record.Clears &&
                attempts.Count > 0 &&
                attempts.Max(attempt => attempt.Score) == record.Score &&
                attempts.Max(attempt => attempt.Combo) == record.Combo;
            if (!completeEvidence)
            {
                unverifiable++;
                continue;
            }

            DeleteRecord(connection, transaction, record);
            removed.Add(new RemovedInvalidCourseClear(
                record.UserId,
                record.Nickname,
                record.CourseId,
                course.Name,
                record.KeyMode,
                record.Score,
                record.Combo,
                record.Clears,
                attempts.Count));
        }

        transaction.Commit();
        return new InvalidCourseClearRemovalReport(
            records.Count, removed.Count, valid, unverifiable, removed);
    }

    private static List<StoredCourseRecord> ReadRankingRecords(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT c.user_id, p.nickname, c.course_id, c.key_mode,
                   c.score, c.combo, c.clears
            FROM course_records AS c
            JOIN players AS p ON p.user_id = c.user_id
            WHERE c.clears > 0
            ORDER BY c.user_id, c.course_id, c.key_mode;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        List<StoredCourseRecord> records = [];
        while (reader.Read())
        {
            records.Add(new StoredCourseRecord(
                checked((uint)reader.GetInt64(0)),
                reader.GetString(1),
                checked((ushort)reader.GetInt64(2)),
                checked((byte)reader.GetInt64(3)),
                checked((uint)reader.GetInt64(4)),
                checked((uint)reader.GetInt64(5)),
                checked((uint)reader.GetInt64(6))));
        }
        return records;
    }

    private static List<CompletedCourseAttempt> ReconstructAttempts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredCourseRecord record,
        CourseDefinition course)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT course_stage, song_id, difficulty, failed, breaks,
                   score, bonus_score, accuracy, max_combo
            FROM stage_scores
            WHERE user_id = $user_id
              AND course_id = $course_id
              AND key_mode = $key_mode
              AND course_stage IS NOT NULL
              AND song_id IS NOT NULL
            ORDER BY played_utc, score_id;
            """;
        command.Parameters.AddWithValue("$user_id", (long)record.UserId);
        command.Parameters.AddWithValue("$course_id", (long)record.CourseId);
        command.Parameters.AddWithValue("$key_mode", (long)record.KeyMode);

        using SqliteDataReader reader = command.ExecuteReader();
        List<StoredCourseStage> rows = [];
        while (reader.Read())
        {
            rows.Add(new StoredCourseStage(
                reader.GetInt32(0),
                checked((uint)reader.GetInt64(1)),
                checked((byte)reader.GetInt64(2)),
                reader.GetInt64(3) != 0,
                checked((ushort)reader.GetInt64(4)),
                checked((uint)reader.GetInt64(5)),
                checked((uint)reader.GetInt64(6)),
                reader.GetDouble(7),
                checked((uint)reader.GetInt64(8))));
        }

        List<CompletedCourseAttempt> attempts = [];
        List<StoredCourseStage>? running = null;
        foreach (StoredCourseStage row in rows)
        {
            if (row.CourseStage == 0)
            {
                running = [];
            }
            if (running == null)
            {
                continue;
            }

            int expectedIndex = running.Count;
            if (row.CourseStage != expectedIndex || expectedIndex >= course.Stages.Count)
            {
                running = null;
                continue;
            }

            CourseStage expected = course.Stages[expectedIndex];
            if (row.SongId != expected.DiscId || row.Difficulty != expected.Difficulty)
            {
                // A changed catalog cannot safely be used as evidence against an old clear.
                running = null;
                continue;
            }

            running.Add(row);
            if (row.Failed)
            {
                running = null;
                continue;
            }
            if (running.Count != course.Stages.Count)
            {
                continue;
            }

            attempts.Add(Summarize(course, running));
            running = null;
        }
        return attempts;
    }

    private static CompletedCourseAttempt Summarize(
        CourseDefinition course,
        IReadOnlyList<StoredCourseStage> stages)
    {
        uint score = 0;
        uint bonusScore = 0;
        uint breaks = 0;
        uint maxCombo = 0;
        double accuracy = 0;
        foreach (StoredCourseStage stage in stages)
        {
            score = AddSaturating(score, stage.Score);
            bonusScore = AddSaturating(bonusScore, stage.BonusScore);
            breaks = AddSaturating(breaks, stage.Breaks);
            maxCombo = Math.Max(maxCombo, stage.MaxCombo);
            accuracy += double.IsFinite(stage.Accuracy) ? stage.Accuracy : 0;
        }

        double meanAccuracy = accuracy / stages.Count;
        ushort objectiveBreaks = breaks > ushort.MaxValue
            ? ushort.MaxValue
            : (ushort)breaks;
        CourseObjectives objectives = course.Objectives;
        bool passed =
            Met(objectives.Accuracy, meanAccuracy, inactiveValue: 1) &&
            Met(objectives.Score, (double)score + bonusScore) &&
            Met(objectives.Breaks, objectiveBreaks) &&
            Met(objectives.Combo, maxCombo);
        return new CompletedCourseAttempt(
            AddSaturating(score, bonusScore), maxCombo, passed);
    }

    private static bool Met(
        CourseCondition condition,
        double actual,
        uint inactiveValue = 0) =>
        !condition.IsActive(inactiveValue) || condition.IsMet(actual);

    private static uint AddSaturating(uint value, uint amount) =>
        amount > uint.MaxValue - value ? uint.MaxValue : value + amount;

    private static void DeleteRecord(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredCourseRecord record)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            DELETE FROM course_records
            WHERE user_id = $user_id
              AND course_id = $course_id
              AND key_mode = $key_mode;
            """;
        command.Parameters.AddWithValue("$user_id", (long)record.UserId);
        command.Parameters.AddWithValue("$course_id", (long)record.CourseId);
        command.Parameters.AddWithValue("$key_mode", (long)record.KeyMode);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidDataException(
                $"Course record {record.UserId}/{record.CourseId}/{record.KeyMode} " +
                "changed during invalid-clear maintenance.");
        }
    }

    private sealed record StoredCourseRecord(
        uint UserId,
        string Nickname,
        ushort CourseId,
        byte KeyMode,
        uint Score,
        uint Combo,
        uint Clears);

    private sealed record StoredCourseStage(
        int CourseStage,
        uint SongId,
        byte Difficulty,
        bool Failed,
        ushort Breaks,
        uint Score,
        uint BonusScore,
        double Accuracy,
        uint MaxCombo);

    private sealed record CompletedCourseAttempt(uint Score, uint Combo, bool Passed);
}
