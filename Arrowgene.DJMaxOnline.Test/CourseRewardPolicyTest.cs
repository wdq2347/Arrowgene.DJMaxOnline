using Arrowgene.DJMaxOnline.Server.Korea400;

namespace Arrowgene.DJMaxOnline.Test;

public class CourseRewardPolicyTest
{
    [Test]
    public void ExperienceBonusUsesTheStageExpNotTheMaxPayout()
    {
        CourseRewards rewards = new(MoneyPercent: 50, ExperiencePercent: 150, Items: []);

        (uint money, uint experience) = CourseRewardPolicy.Calculate(
            stageMoney: 1_000, stageExperience: 40, rewards);

        Assert.Multiple(() =>
        {
            Assert.That(money, Is.EqualTo(500u));
            Assert.That(experience, Is.EqualTo(60u));
        });
    }
}
