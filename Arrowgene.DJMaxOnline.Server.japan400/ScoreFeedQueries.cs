using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Arrowgene.DJMaxOnline.Server.Japan400;

/// <summary>
/// Read-only score queries for the local status API.
///
/// Kept apart from <see cref="SqlitePlayerRepository"/> on purpose: that class owns
/// writes and credentials, this one only ever reads and only ever projects into
/// <see cref="ScoreStatus"/>. The SELECTs name their columns explicitly and none of them
/// names account_id, secondary_id or anything from player_credentials - so a nickname is
/// the only identity that can come out.
///
/// The connection is opened read-only, which also keeps the API from blocking a player
/// mid-song behind a write lock.
///
/// ZERO-BASED IDS: stage_scores stores what the client sent - a DISC INDEX and a COURSE
/// INDEX, both counting from 0 - while DiscStock.csv and CourseSection.ini both number
/// from 1. The server's own log spells the song case out as "Started disc index 219 ->
/// song 220", and a course run of "Let's Begin" (CourseNo 1) is stored as course_id 0.
/// The API publishes CATALOG ids for both, so callers can look a play up directly.
/// Conversion happens here, in both directions, and nowhere else.
///
/// course_stage stays zero-based: it is an offset within the run, not a catalog key.
/// </summary>
public sealed class ScoreFeedQueries
{
    /// <summary>DiscStock is 1-based; the stored disc index is 0-based.</summary>
    private const int CatalogOffset = 1;

    private const string Columns = """
        s.score_id, p.nickname, s.played_utc, s.song_id, s.course_id, s.course_stage,
        s.room_id,
        s.key_mode, s.difficulty, s.match_mode, s.total_notes, s.max_combo, s.notes_hit,
        s.breaks, s.score, s.accuracy, s.rank, s.failed, s.full_combo,
        s.money_awarded, s.experience_awarded
        """;

    private readonly string _databasePath;

    public ScoreFeedQueries(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
    }

    private SqliteConnection Open()
    {
        SqliteConnection connection = new(
            new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared
            }.ToString());
        connection.Open();
        return connection;
    }

    private static int Clamp(int value, int fallback, int maximum) =>
        value <= 0 ? fallback : Math.Min(value, maximum);

    /// <summary>
    /// SOLO plays newer than <paramref name="afterScoreId"/>, oldest first.
    ///
    /// Plays from a multiplayer room are deliberately absent: they are published whole by
    /// the match feed, which reports the room, the song and every player's placing in one
    /// record. Returning them here as well made a consumer post the same song once per
    /// player AND again as a summary, so the split is drawn here rather than leaving every
    /// consumer to re-derive it.
    ///
    /// "Multiplayer" is exactly what the match feed records: two or more players finishing
    /// the same song in the same room. Every row from one finish is written with the same
    /// room and timestamp, so another player's row sharing both identifies one.
    /// </summary>
    public IReadOnlyList<ScoreStatus> Recent(long afterScoreId, int limit)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns}
            FROM stage_scores AS s
            JOIN players AS p ON p.user_id = s.user_id
            WHERE s.score_id > $after
              AND NOT EXISTS (
                  SELECT 1
                  FROM stage_scores AS o
                  WHERE o.room_id IS NOT NULL
                    AND o.room_id = s.room_id
                    AND o.played_utc = s.played_utc
                    AND o.user_id <> s.user_id)
            ORDER BY s.score_id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$after", afterScoreId);
        command.Parameters.AddWithValue("$limit", Clamp(limit, 25, 100));
        return Read(command);
    }

    /// <summary>A player's best runs, by nickname. Unknown nickname yields an empty list.</summary>
    public IReadOnlyList<ScoreStatus> ForPlayer(string nickname, int limit)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns}
            FROM stage_scores AS s
            JOIN players AS p ON p.user_id = s.user_id
            WHERE p.nickname = $nickname COLLATE NOCASE
            ORDER BY s.score_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$nickname", nickname ?? string.Empty);
        command.Parameters.AddWithValue("$limit", Clamp(limit, 10, 50));
        return Read(command);
    }

    /// <summary>Best run per player for one song.</summary>
    public IReadOnlyList<ScoreStatus> ForSong(uint songId, byte keyMode, int limit)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns}
            FROM stage_scores AS s
            JOIN players AS p ON p.user_id = s.user_id
            JOIN (
                SELECT user_id, MAX(score) AS best
                FROM stage_scores
                WHERE song_id = $song AND ($keys = 0 OR key_mode = $keys)
                GROUP BY user_id
            ) AS top ON top.user_id = s.user_id AND top.best = s.score
            WHERE s.song_id = $song AND ($keys = 0 OR s.key_mode = $keys)
            GROUP BY s.user_id
            ORDER BY s.score DESC, s.accuracy DESC
            LIMIT $limit;
            """;
        // Callers pass a DiscStock (catalog) id; the column holds the disc index.
        command.Parameters.AddWithValue("$song", (long)songId - CatalogOffset);
        command.Parameters.AddWithValue("$keys", keyMode);
        command.Parameters.AddWithValue("$limit", Clamp(limit, 10, 50));
        return Read(command);
    }

    public long LatestScoreId()
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(score_id), 0) FROM stage_scores;";
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<ScoreStatus> Read(SqliteCommand command)
    {
        List<ScoreStatus> scores = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            scores.Add(new ScoreStatus(
                ScoreId: reader.GetInt64(reader.GetOrdinal("score_id")),
                Nickname: reader.GetString(reader.GetOrdinal("nickname")),
                PlayedUtc: reader.GetString(reader.GetOrdinal("played_utc")),
                // Republish as the catalog id so a consumer can look it up directly.
                SongId: Nullable(reader, "song_id") is { } disc
                    ? disc + CatalogOffset
                    : null,
                CourseId: Nullable(reader, "course_id") is { } course
                    ? (ushort)(course + CatalogOffset)
                    : null,
                CourseStage: Nullable(reader, "course_stage") is { } stage ? (int)stage : null,
                RoomId: Nullable(reader, "room_id") is { } room ? (ushort)room : null,
                KeyMode: (byte)reader.GetInt64(reader.GetOrdinal("key_mode")),
                Difficulty: (byte)reader.GetInt64(reader.GetOrdinal("difficulty")),
                MatchMode: (byte)reader.GetInt64(reader.GetOrdinal("match_mode")),
                TotalNotes: (ushort)reader.GetInt64(reader.GetOrdinal("total_notes")),
                MaxCombo: (uint)reader.GetInt64(reader.GetOrdinal("max_combo")),
                NotesHit: (uint)reader.GetInt64(reader.GetOrdinal("notes_hit")),
                Breaks: (uint)reader.GetInt64(reader.GetOrdinal("breaks")),
                Score: (uint)reader.GetInt64(reader.GetOrdinal("score")),
                Accuracy: reader.GetDouble(reader.GetOrdinal("accuracy")),
                Rank: (byte)reader.GetInt64(reader.GetOrdinal("rank")),
                Failed: reader.GetInt64(reader.GetOrdinal("failed")) != 0,
                FullCombo: reader.GetInt64(reader.GetOrdinal("full_combo")) != 0,
                MoneyAwarded: (uint)reader.GetInt64(reader.GetOrdinal("money_awarded")),
                ExperienceAwarded: (uint)reader.GetInt64(
                    reader.GetOrdinal("experience_awarded"))));
        }
        return scores;
    }

    private static uint? Nullable(SqliteDataReader reader, string column)
    {
        int ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : (uint)reader.GetInt64(ordinal);
    }
}
