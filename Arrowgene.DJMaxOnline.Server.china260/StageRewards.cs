using Arrowgene.DJMaxOnline.Server.China260.Packets;

namespace Arrowgene.DJMaxOnline.Server.China260;

/// <summary>
/// The client's own experience curve, read from the table at <c>dword_55C9C0</c>. The
/// lobby profile card draws its EXP bar as <c>experience / table[level]</c>
/// (sub_405790 -> sub_4625B8, with sub_427410 returning the level), so the server must
/// level up against the SAME table or the bar and the level disagree. The table is
/// indexed by level directly and terminated by 0xFFFFFFFF.
/// </summary>
public static class ExperienceCurve
{
    /// <summary>Experience needed to advance, indexed by the player's current level.</summary>
    public static readonly uint[] Thresholds =
    [
        40, 60, 80, 100, 140, 280, 420, 560, 840, 980,
        1260, 1540, 1960, 2380, 2800, 3220, 3780, 4340, 5040, 5740,
        6440, 7280, 8120, 8960, 10080, 11760, 13720, 15680, 17920, 27440,
        31920, 36680, 41720, 46900, 55720, 62440, 70560, 78400, 86940, 122080,
        136640, 152040, 168280, 185080, 212100, 233520, 256060, 279300, 303800, 409780,
        460880, 514500, 570780, 630000, 712040, 784840, 860720, 939400, 1021300, 1320480,
        1463560, 1612800, 1768340, 1929900, 2147740, 2340660, 2541000, 2748760, 2963800, 3701600,
        5894980, 6376160, 6875540, 7393260, 7929320, 8920380, 9542400, 10189620, 10855320, 12824560,
        14724080, 16089780, 17507980, 18978680, 20501740, 23069060, 24815980, 26621840, 28486640, 30410380,
        38954860, 46599840, 54573960, 73357200, 95347140, 142632000, 194747980, 232540000
    ];

    /// <summary>The client's table ends at 98 entries; 99 is the ceiling it can render.</summary>
    public const uint MaxLevel = 99;

    public static uint Required(uint level) =>
        level < Thresholds.Length ? Thresholds[level] : uint.MaxValue;

    /// <summary>
    /// Consumes completed rungs from a stored progress record. Every EXP payout, including
    /// a course-clear bonus, must take this path before its state is sent to the client.
    /// </summary>
    /// <returns><c>true</c> when one or more levels were gained.</returns>
    public static bool ApplyLevelUps(LocalPlayerProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        uint previousLevel = progress.Level;
        while (progress.Level < MaxLevel)
        {
            uint required = Required(progress.Level);
            if (required == uint.MaxValue || progress.Experience < required)
            {
                break;
            }
            progress.Experience -= required;
            progress.Level++;
        }
        return progress.Level > previousLevel;
    }
}

/// <summary>
/// How a finished song converts into money and experience. These amounts are a SERVER
/// POLICY choice — the retail economy is not encoded anywhere in the client, so nothing
/// here is reverse-engineered. Tune the constants freely; only the level-up thresholds
/// (see <see cref="ExperienceCurve"/>) have to match the client.
/// </summary>
public static class StageRewardPolicy
{
    /// <summary>Applies the configured base rates. See <c>Setting.RewardRates</c>.</summary>
    public static void Configure(RewardRateSetting? rates)
    {
        RewardRateSetting r = (rates ?? new RewardRateSetting()).Validated();
        MoneyPerNoteHit = r.MoneyPerNoteHit;
        NotesHitPerExperience = r.NotesHitPerExperience;
        FullComboMoneyBonus = r.FullComboMoneyBonus;
        FullComboExperienceBonus = r.FullComboExperienceBonus;
        FailureDivisor = r.FailureDivisor;
    }

    public static uint MoneyPerNoteHit { get; set; } = 1;

    /// <summary>
    /// Experience is one point per this many notes hit. The client's curve is shallow at
    /// the start (40/60/80/100 for levels 1-4) but climbs hard - level 20 wants 6,440 and
    /// level 30 wants 27,440 - so a rate tuned to feel right for the first few levels
    /// stalls completely later. At /30 a ~450-note chart paid ~15 points, which is roughly
    /// 430 songs to reach level 30; /3 pays ~150 and keeps later levels reachable while the
    /// curve itself still does the pacing.
    /// </summary>
    public static uint NotesHitPerExperience { get; set; } = 3;

    public static uint FullComboMoneyBonus { get; set; } = 100;
    public static uint FullComboExperienceBonus { get; set; } = 50;

    /// <summary>
    /// Scales the final experience award. This is the knob to turn for progression speed -
    /// the thresholds themselves cannot change, because the client draws its EXP bar from
    /// its own copy of the table.
    /// </summary>
    public static uint ExperienceMultiplier { get; set; } = 1;

    /// <summary>
    /// Scales the final MAX (money) award, the same way <see cref="ExperienceMultiplier"/>
    /// scales experience. MAX has no client-side curve to disagree with, so this is a
    /// straight payout rate.
    /// </summary>
    public static uint MoneyMultiplier { get; set; } = 1;

    /// <summary>A failed run still pays, at half rate, so practice is not worthless.</summary>
    public static uint FailureDivisor { get; set; } = 2;

    public static (uint Money, uint Experience) Calculate(StageResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        uint hits = result.NotesHit;
        uint money = hits * MoneyPerNoteHit;
        uint experience = hits / NotesHitPerExperience;

        if (result.FullCombo)
        {
            money += FullComboMoneyBonus;
            experience += FullComboExperienceBonus;
        }

        if (result.Failed)
        {
            money /= FailureDivisor;
            experience /= FailureDivisor;
        }

        // Saturate rather than wrap; a multiplier is a tuning knob, not a cap.
        return (Scale(money, MoneyMultiplier), Scale(experience, ExperienceMultiplier));
    }

    public static uint Scale(uint value, uint multiplier)
    {
        ulong scaled = (ulong)value * multiplier;
        return scaled > uint.MaxValue ? uint.MaxValue : (uint)scaled;
    }

    /// <summary>Applies a percentage bonus (100 = double) without wrapping.</summary>
    public static uint AddPercent(uint value, int percent)
    {
        if (percent <= 0 || value == 0)
        {
            return value;
        }
        ulong scaled = value + ((ulong)value * (uint)percent / 100);
        return scaled > uint.MaxValue ? uint.MaxValue : (uint)scaled;
    }
}
