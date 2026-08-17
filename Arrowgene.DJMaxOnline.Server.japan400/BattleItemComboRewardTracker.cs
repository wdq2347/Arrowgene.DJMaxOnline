namespace Arrowgene.DJMaxOnline.Server.Japan400;

/// <summary>
/// Infers repeat item-battle rewards from the live state the retail client exposes.
/// The client sends its song-local maximum combo rather than its current combo, so a
/// break cannot be represented by that field decreasing. While the player is still at
/// the lifetime peak, score progress without enough peak growth proves that a break
/// occurred. After that, base-score growth gives a safe lower bound on the new streak
/// because one combo-preserving judgment is worth at most 100.
/// </summary>
public sealed class BattleItemComboRewardTracker
{
    /// <summary>
    /// Combo needed for one item DROP. Drops are entirely server policy: the client has
    /// no drop rule of its own, it only asks for an item (GetItemReq) when it hits a note
    /// the server marked with OnCrItemInf. Its own 30-counter (sub_4268A0) drives item
    /// LEVEL-UPS and counts note hits rather than combo, so it is not this interval.
    /// </summary>
    public const ushort DefaultComboInterval = 50;

    private const float MaximumBaseScorePerJudgment = 100.0f;
    private const float FloatTolerance = 0.01f;

    private readonly ushort _comboInterval;

    public BattleItemComboRewardTracker(int comboInterval = DefaultComboInterval)
    {
        // A zero or negative interval would award on every report, so fall back rather
        // than let a mistyped setting flood the room with pickups.
        _comboInterval = comboInterval is > 0 and <= ushort.MaxValue
            ? (ushort)comboInterval
            : DefaultComboInterval;
    }

    /// <summary>Combo per drop this tracker is running with.</summary>
    public ushort ComboInterval => _comboInterval;

    private bool _hasSnapshot;
    private bool _atSongMaximum;
    private ushort _lastMaximumCombo;
    private float _lastBaseScore;
    private ushort _maximumBeforeCurrentStreak;
    private float _currentStreakBaseScore;
    private int _knownCurrentStreakCombo;
    private int _currentStreakRewardCount;

    /// <summary>Total retail 30-combo rewards earned during the current song.</summary>
    public int RewardsEarned { get; private set; }

    /// <summary>
    /// Conservative current-streak estimate used after a break. Before the first
    /// observed break this is the exact song-local maximum reported by the client.
    /// </summary>
    public ushort EstimatedCurrentStreakCombo { get; private set; }

    /// <summary>True once at least one combo break has been inferred this song.</summary>
    public bool HasObservedBreak { get; private set; }

    public void Reset()
    {
        _hasSnapshot = false;
        _atSongMaximum = false;
        _lastMaximumCombo = 0;
        _lastBaseScore = 0;
        _maximumBeforeCurrentStreak = 0;
        _currentStreakBaseScore = 0;
        _knownCurrentStreakCombo = 0;
        _currentStreakRewardCount = 0;
        RewardsEarned = 0;
        EstimatedCurrentStreakCombo = 0;
        HasObservedBreak = false;
    }

    /// <summary>
    /// Observes one five-second PlayStateInf snapshot and returns the cumulative number
    /// of room item signals this player has earned during the song.
    /// </summary>
    public int Observe(ushort maximumCombo, float baseScore)
    {
        if (!float.IsFinite(baseScore))
        {
            return RewardsEarned;
        }

        if (!_hasSnapshot)
        {
            _hasSnapshot = true;
            _atSongMaximum = true;
            _lastMaximumCombo = maximumCombo;
            _lastBaseScore = baseScore;
            EstimatedCurrentStreakCombo = maximumCombo;
            AwardCurrentStreakTiers(maximumCombo);
            return RewardsEarned;
        }

        float scoreGrowth = Math.Max(0.0f, baseScore - _lastBaseScore);
        int maximumGrowth = Math.Max(0, maximumCombo - _lastMaximumCombo);

        // When the current streak owns the song maximum, every combo-preserving note
        // raises both fields. Since one judgment contributes at most 100 base points,
        // extra score growth proves that the combo stopped and restarted.
        bool scoreProvesBreak = _atSongMaximum &&
                                scoreGrowth > maximumGrowth * MaximumBaseScorePerJudgment +
                                FloatTolerance;
        if (scoreProvesBreak)
        {
            float unexplainedScoreGrowth =
                scoreGrowth - maximumGrowth * MaximumBaseScorePerJudgment;
            // Preserve the entire unexplained score (including a sub-100 remainder) so
            // conservative progress is not lost at the five-second snapshot boundary.
            BeginNewStreak(
                maximumCombo,
                baseScore - unexplainedScoreGrowth,
                knownCombo: 0);
        }

        if (HasObservedBreak)
        {
            float scoreSinceObservedBreak = Math.Max(0.0f, baseScore - _currentStreakBaseScore);
            int conservativeCombo = _knownCurrentStreakCombo + (int)MathF.Floor(
                scoreSinceObservedBreak / MaximumBaseScorePerJudgment);

            // Once the new streak exceeds the old song maximum, the maximum-combo field
            // becomes an exact lower bound again. Until then, the score-derived value is
            // deliberately conservative and can only award late, never before 50 hits.
            if (maximumCombo > _maximumBeforeCurrentStreak)
            {
                conservativeCombo = Math.Max(conservativeCombo, maximumCombo);
                _atSongMaximum = true;
            }

            EstimatedCurrentStreakCombo = (ushort)Math.Min(ushort.MaxValue, conservativeCombo);
            AwardCurrentStreakTiers(EstimatedCurrentStreakCombo);
        }
        else
        {
            EstimatedCurrentStreakCombo = maximumCombo;
            AwardCurrentStreakTiers(maximumCombo);
        }

        _lastMaximumCombo = maximumCombo;
        _lastBaseScore = baseScore;
        return RewardsEarned;
    }

    private void BeginNewStreak(ushort maximumCombo, float baseScore, int knownCombo)
    {
        HasObservedBreak = true;
        _atSongMaximum = false;
        _maximumBeforeCurrentStreak = maximumCombo;
        _currentStreakBaseScore = baseScore;
        _knownCurrentStreakCombo = Math.Max(0, knownCombo);
        _currentStreakRewardCount = 0;
        EstimatedCurrentStreakCombo =
            (ushort)Math.Min(ushort.MaxValue, _knownCurrentStreakCombo);
    }

    private void AwardCurrentStreakTiers(ushort combo)
    {
        int earnedTiers = combo / _comboInterval;
        if (earnedTiers <= _currentStreakRewardCount)
        {
            return;
        }

        RewardsEarned += earnedTiers - _currentStreakRewardCount;
        _currentStreakRewardCount = earnedTiers;
    }
}
