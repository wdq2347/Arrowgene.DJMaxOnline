using Arrowgene.DJMaxOnline.Server.Japan400.Packets;

namespace Arrowgene.DJMaxOnline.Server.Japan400;

/// <summary>A persisted account exposed by the multi-user player repository.</summary>
public sealed record PlayerAccountSummary(
    uint UserId,
    string AccountId,
    string Nickname,
    uint Level);

/// <summary>Opaque password-verifier material stored independently from profile data.</summary>
public sealed record PlayerPasswordCredential(
    uint UserId,
    string Algorithm,
    int Iterations,
    byte[] Salt,
    byte[] Hash);

/// <summary>
/// One completed chart. Unlike the aggregate values in <see cref="LocalPlayerProgress"/>,
/// this preserves the full result so score history and per-song bests can be queried later.
/// </summary>
public sealed record StageScoreRecord
{
    public long Id { get; init; }
    public uint UserId { get; init; }
    public DateTimeOffset PlayedAt { get; init; } = DateTimeOffset.UtcNow;
    public uint? SongId { get; init; }
    public ushort? CourseId { get; init; }
    public int? CourseStage { get; init; }
    public ushort? RoomId { get; init; }
    public byte KeyMode { get; init; }
    public byte Difficulty { get; init; }
    public byte MatchMode { get; init; }
    public uint SessionToken { get; init; }
    public byte ClientFlags { get; init; }
    public ushort TotalNotes { get; init; }
    public ushort[] Judgments { get; init; } = new ushort[StageResult.JudgmentCount];
    public float Gauge { get; init; }
    public ushort EncodedCombo { get; init; }
    public uint CurrentCombo { get; init; }
    public uint MaxCombo { get; init; }
    public ushort AuxiliaryValue { get; init; }
    public byte ResultState { get; init; }
    public byte Tail { get; init; }
    public bool Failed { get; init; }
    public bool FullCombo { get; init; }
    public ushort NotesHit { get; init; }
    public ushort Breaks { get; init; }
    public uint Score { get; init; }
    public uint BonusScore { get; init; }
    public float Accuracy { get; init; }
    public StageResultRank Rank { get; init; }
    public uint MoneyAwarded { get; init; }
    public uint ExperienceAwarded { get; init; }

    public void Validate()
    {
        if (UserId == 0)
        {
            throw new InvalidDataException("A stage score requires a non-zero user id.");
        }
        if (Judgments == null || Judgments.Length != StageResult.JudgmentCount)
        {
            throw new InvalidDataException(
                $"A stage score requires exactly {StageResult.JudgmentCount} judgments.");
        }
        if (!float.IsFinite(Gauge) || !float.IsFinite(Accuracy))
        {
            throw new InvalidDataException("Stage score floating-point values must be finite.");
        }
    }
}

/// <summary>Persistence boundary used by the live player store.</summary>
public interface IPlayerRepository
{
    string DatabasePath { get; }
    int UserCount { get; }

    IReadOnlyList<PlayerAccountSummary> ListUsers();
    LocalPlayerProfile Create(string accountId, string nickname, byte gender = 1);
    bool TryLoad(uint userId, out LocalPlayerProfile? profile);
    bool TryLoad(string accountIdOrNickname, out LocalPlayerProfile? profile);
    bool TryGetPasswordCredential(
        string accountId,
        out PlayerPasswordCredential? credential);
    void SetPasswordCredential(PlayerPasswordCredential credential);
    void Save(LocalPlayerProfile profile);
    void SaveWithStageScore(LocalPlayerProfile profile, StageScoreRecord score);
    IReadOnlyList<StageScoreRecord> StageScores(uint userId, int limit = 100);
    IReadOnlyList<CourseRankEntry> CourseRanking(
        ushort courseId,
        byte keyMode,
        int limit = OnCourseRankAckPacket.EntryCount);
}

public sealed record PlayerDatabaseSelection(
    LocalPlayerProfile Profile,
    bool ImportedLegacyJson,
    bool CreatedDefault);

/// <summary>One-time JSON import and active-account selection for the CLI.</summary>
public static class PlayerDatabaseBootstrap
{
    /// <summary>
    /// Seeds an empty database and nothing else. Returns null when there was already at
    /// least one account, in which case no profile is read at all - starting the server
    /// must not load somebody's account just to throw it away.
    /// </summary>
    public static PlayerDatabaseSelection? EnsureSeeded(
        IPlayerRepository repository,
        string legacyJsonPath)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyJsonPath);

        if (repository.UserCount != 0)
        {
            return null;
        }

        bool imported = File.Exists(legacyJsonPath);
        LocalPlayerProfile seed = imported
            ? LocalPlayerProfileFile.LoadOrCreate(legacyJsonPath)
            : LocalPlayerProfile.Default;
        repository.Save(seed);
        return new PlayerDatabaseSelection(seed, imported, !imported);
    }

    public static PlayerDatabaseSelection Select(
        IPlayerRepository repository,
        string legacyJsonPath,
        string? selector = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyJsonPath);

        if (repository.UserCount == 0)
        {
            bool imported = File.Exists(legacyJsonPath);
            LocalPlayerProfile seed = imported
                ? LocalPlayerProfileFile.LoadOrCreate(legacyJsonPath)
                : LocalPlayerProfile.Default;
            repository.Save(seed);
            return new PlayerDatabaseSelection(seed, imported, !imported);
        }

        if (!string.IsNullOrWhiteSpace(selector))
        {
            LocalPlayerProfile? selected;
            bool found = uint.TryParse(selector, out uint userId)
                ? repository.TryLoad(userId, out selected)
                : repository.TryLoad(selector, out selected);
            if (!found || selected == null)
            {
                throw new KeyNotFoundException(
                    $"No SQLite player matches '{selector}'. Available accounts: " +
                    string.Join(", ", repository.ListUsers().Select(user =>
                        $"{user.UserId}:{user.AccountId}/{user.Nickname}")));
            }
            return new PlayerDatabaseSelection(selected, false, false);
        }

        PlayerAccountSummary first = repository.ListUsers()
            .OrderBy(user => user.UserId)
            .First();
        repository.TryLoad(first.UserId, out LocalPlayerProfile? profile);
        return new PlayerDatabaseSelection(
            profile ?? throw new InvalidDataException(
                $"SQLite user {first.UserId} disappeared during selection."),
            false,
            false);
    }
}
