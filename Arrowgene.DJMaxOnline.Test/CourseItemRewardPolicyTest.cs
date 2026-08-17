using Arrowgene.DJMaxOnline.Server.Korea400;

namespace Arrowgene.DJMaxOnline.Test;

public class CourseItemRewardPolicyTest
{
    private static readonly CourseItemReward[] Rewards =
    [
        new(0xF401, 30),
        new(0xF501, 20)
    ];

    [TestCase(0, 0xF401)]
    [TestCase(29, 0xF401)]
    [TestCase(30, 0xF501)]
    [TestCase(49, 0xF501)]
    public void OneRollSelectsExactlyOneWeightedItem(int roll, int expected)
    {
        CourseItemReward? selected = CourseItemRewardPolicy.Select(Rewards, roll);
        Assert.That(selected?.CatalogId, Is.EqualTo((ushort)expected));
    }

    [TestCase(50)]
    [TestCase(99)]
    public void UnusedPercentageIsTheNoItemOutcome(int roll)
    {
        Assert.That(CourseItemRewardPolicy.Select(Rewards, roll), Is.Null);
    }

    [Test]
    public void AZeroChanceEntryNeverWins()
    {
        CourseItemReward[] rewards =
        [
            new(0xF401, 0),
            new(0xF501, 100)
        ];

        Assert.That(
            CourseItemRewardPolicy.Select(rewards, 0)?.CatalogId,
            Is.EqualTo(0xF501));
    }

    [TestCase(-1)]
    [TestCase(100)]
    public void RejectsRollsOutsideTheRetailPercentageRange(int roll)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CourseItemRewardPolicy.Select(Rewards, roll));
    }
}
