namespace Arrowgene.DJMaxOnline.Server.Korea400;

/// <summary>
/// The client's scroll-speed multipliers, read out of the Korea client rather than guessed.
///
/// Speed is "effect 20" in the client's generic effect system: <c>sub_426950(20, index)</c>
/// sets it, <c>sub_411450(20)</c> reads it back as <c>*(this + 10*n + 187)</c>, and
/// <c>sub_425260</c> clamps the index to 0..18 when SPEED UP / SPEED DOWN step it. The 19
/// multipliers those indices select sit at VA 0x0055CD18 as consecutive floats.
///
/// The mapping is confirmed by the battle items landing where their names say they should:
/// HLF.SPEED is index 4 and resolves to exactly 0.5 - an off-by-one in the table base would
/// have produced 0.4 or 0.6 instead.
/// </summary>
public static class ScrollSpeedTable
{
    /// <summary>The 19 multipliers at VA 0x0055CD18, in index order.</summary>
    public static readonly IReadOnlyList<double> Multipliers =
    [
        0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.8,   // 0-6   below the selectable range
        1.0, 1.5, 2.0, 2.5, 3.0, 3.5, 4.0, 4.5, 5.0,   // 7-15  the player's x1..x5
        6.0,    // 16  SUP.SPEED
        10.0,   // 17
        99.0    // 18  HYP.SPEED
    ];

    /// <summary>First index a player can select in the room (x1.0).</summary>
    public const int FirstPlayerIndex = 7;

    /// <summary>Last index a player can select in the room (x5.0).</summary>
    public const int LastPlayerIndex = 15;

    /// <summary>The nine speeds the room UI offers, x1.0 through x5.0 in 0.5 steps.</summary>
    public static IEnumerable<double> PlayerSpeeds =>
        Multipliers.Skip(FirstPlayerIndex).Take(LastPlayerIndex - FirstPlayerIndex + 1);

    public static double At(int index) =>
        Multipliers[Math.Clamp(index, 0, Multipliers.Count - 1)];

    /// <summary>
    /// Effective scroll rate: the chart's BPM times the multiplier. This is what decides how
    /// fast notes actually travel, which is why the same speed setting feels different on a
    /// 90 BPM chart and a 180 BPM one.
    /// </summary>
    public static double Effective(double bpm, double multiplier) => bpm * multiplier;

    /// <summary>
    /// The two selectable speeds whose effective rate sits nearest <paramref name="target"/>
    /// on a chart of <paramref name="bpm"/>, returned in ascending speed order.
    ///
    /// Two rather than one because a target rarely lands on a step: with 0.5 increments the
    /// answer is usually "between x2.5 and x3.0", and which side a player prefers is taste.
    /// Reporting a single nearest value hides that the other option exists.
    /// </summary>
    public static IReadOnlyList<double> NearestPlayerSpeeds(double bpm, double target)
    {
        if (bpm <= 0)
        {
            return [];
        }

        return
        [
            .. PlayerSpeeds
                .OrderBy(multiplier => Math.Abs(Effective(bpm, multiplier) - target))
                .Take(2)
                .OrderBy(multiplier => multiplier)
        ];
    }
}
