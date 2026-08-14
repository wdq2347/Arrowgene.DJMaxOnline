namespace Arrowgene.DJMaxOnline.Server;

/// <summary>
/// The last N finished multiplayer matches, in memory, for the local status API.
///
/// Why not the database: <c>stage_scores</c> stores one row per player per song and says
/// nothing about who won. Placement is decided in <see cref="LocalLobby.FinishPlay"/> by
/// <see cref="StagePlacementPolicy"/> - the ladder for a free-for-all, the winning SIDE for
/// a team battle - and that verdict is the thing worth publishing. Re-deriving it from
/// stored rows would be guesswork, and for item battle it would be wrong: the mode is
/// decided by gauge, not by score.
///
/// So this captures the verdict the server already reached, at the moment it reaches it.
///
/// Deliberately NOT persisted. It is a live feed for tooling, not a record of account
/// history; a restart starting from an empty buffer is correct behaviour. Bounded so a
/// long-running server cannot grow without limit.
/// </summary>
public sealed class MatchHistory
{
    public const int DefaultCapacity = 200;

    private readonly object _lock = new();
    private readonly Queue<MatchStatus> _matches = new();
    private readonly int _capacity;
    private long _nextId;

    public MatchHistory(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>The id of the newest recorded match; 0 when nothing has finished yet.</summary>
    public long LatestMatchId
    {
        get
        {
            lock (_lock)
            {
                return _nextId;
            }
        }
    }

    /// <summary>
    /// Records one finished match. <paramref name="build"/> receives the id to stamp on it,
    /// so the caller never has to guess a value that only this class can hand out.
    /// </summary>
    public MatchStatus Record(Func<long, MatchStatus> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        lock (_lock)
        {
            MatchStatus match = build(_nextId + 1);
            _nextId = match.MatchId;
            _matches.Enqueue(match);
            while (_matches.Count > _capacity)
            {
                _matches.Dequeue();
            }
            return match;
        }
    }

    /// <summary>
    /// Matches newer than <paramref name="afterMatchId"/>, oldest first - the same cursor
    /// shape the score feed uses, so a poller can advance through both the same way.
    /// </summary>
    public IReadOnlyList<MatchStatus> Recent(long afterMatchId, int limit)
    {
        int take = limit <= 0 ? 25 : Math.Min(limit, 100);
        lock (_lock)
        {
            return _matches
                .Where(match => match.MatchId > afterMatchId)
                .OrderBy(match => match.MatchId)
                .Take(take)
                .ToArray();
        }
    }
}
