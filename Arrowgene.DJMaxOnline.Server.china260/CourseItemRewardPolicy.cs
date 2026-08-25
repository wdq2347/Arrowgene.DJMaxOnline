namespace Arrowgene.DJMaxOnline.Server.China260;

/// <summary>Selects the single random item a cleared course may award.</summary>
public static class CourseItemRewardPolicy
{
    /// <summary>
    /// Selects from one 0..99 roll using the Itemnum chances as consecutive ranges.
    /// Any percentage left after the declared ranges is the no-item outcome.
    /// </summary>
    public static CourseItemReward? Select(
        IReadOnlyList<CourseItemReward> rewards,
        int roll)
    {
        ArgumentNullException.ThrowIfNull(rewards);
        if (roll is < 0 or >= 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(roll), roll, "A course reward roll must be from 0 through 99.");
        }

        int upperExclusive = 0;
        foreach (CourseItemReward reward in rewards)
        {
            upperExclusive = Math.Min(100,
                upperExclusive + reward.ChancePercent);
            if (roll < upperExclusive)
            {
                return reward;
            }
        }

        return null;
    }

    public static CourseItemReward? Roll(IReadOnlyList<CourseItemReward> rewards) =>
        Select(rewards, Random.Shared.Next(100));
}
