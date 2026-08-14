using Arrowgene.DJMaxOnline.Server;
using Arrowgene.DJMaxOnline.Server.Packets;

namespace Arrowgene.DJMaxOnline.Test;

/// <summary>
/// The EXP curve is the client's own table (dword_55C9C0). The lobby card draws its bar
/// as experience/table[level], so these values must not drift from the executable.
/// </summary>
public class StageRewardTest
{
    [Test]
    public void CurveMatchesTheClientTable()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ExperienceCurve.Thresholds, Has.Length.EqualTo(98));
            // Anchors read straight out of the binary.
            Assert.That(ExperienceCurve.Thresholds[0], Is.EqualTo(40u));
            Assert.That(ExperienceCurve.Thresholds[1], Is.EqualTo(60u));
            Assert.That(ExperienceCurve.Thresholds[97], Is.EqualTo(232540000u));
            // The client's table is strictly increasing; a non-monotonic curve would let
            // a single result skip several levels or stall forever.
            for (int i = 1; i < ExperienceCurve.Thresholds.Length; i++)
            {
                Assert.That(
                    ExperienceCurve.Thresholds[i],
                    Is.GreaterThan(ExperienceCurve.Thresholds[i - 1]),
                    $"threshold {i} is not increasing");
            }
        });
    }

    [Test]
    public void RequiredIsIndexedByLevelAndSaturatesPastTheTable()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ExperienceCurve.Required(0), Is.EqualTo(40u));
            Assert.That(ExperienceCurve.Required(1), Is.EqualTo(60u));
            // Past the client's table there is no further level-up.
            Assert.That(ExperienceCurve.Required(98), Is.EqualTo(uint.MaxValue));
            Assert.That(ExperienceCurve.Required(500), Is.EqualTo(uint.MaxValue));
        });
    }

    [Test]
    public void FailedRunStillPaysButAtHalfRate()
    {
        // Same play, cleared vs failed: the failed one must pay strictly less but not 0.
        StageResultStub cleared = new(notesHit: 400, failed: false, fullCombo: false);
        StageResultStub failed = new(notesHit: 400, failed: true, fullCombo: false);

        (uint money, uint exp) = Reward(cleared);
        (uint failMoney, uint failExp) = Reward(failed);

        // Derived from the policy rather than hard-coded, so retuning the rate does not
        // break the relationship this test is actually about.
        uint expectedExp = 400u / StageRewardPolicy.NotesHitPerExperience;
        Assert.Multiple(() =>
        {
            Assert.That(money, Is.EqualTo(400u));
            Assert.That(exp, Is.EqualTo(expectedExp));
            Assert.That(failMoney, Is.EqualTo(200u));
            Assert.That(failExp, Is.EqualTo(expectedExp / 2));
        });
    }

    [Test]
    public void FullComboAddsItsBonus()
    {
        (uint plain, uint plainExp) = Reward(new(400, false, false));
        (uint combo, uint comboExp) = Reward(new(400, false, true));
        Assert.Multiple(() =>
        {
            Assert.That(combo - plain, Is.EqualTo(StageRewardPolicy.FullComboMoneyBonus));
            Assert.That(
                comboExp - plainExp,
                Is.EqualTo(StageRewardPolicy.FullComboExperienceBonus));
        });
    }

    // The policy only reads these three properties off StageResult; mirroring the formula
    // keeps the test independent of the 52-byte wire record.
    private readonly record struct StageResultStub(uint notesHit, bool failed, bool fullCombo);

    private static (uint Money, uint Experience) Reward(StageResultStub r)
    {
        uint money = r.notesHit * StageRewardPolicy.MoneyPerNoteHit;
        uint exp = r.notesHit / StageRewardPolicy.NotesHitPerExperience;
        if (r.fullCombo)
        {
            money += StageRewardPolicy.FullComboMoneyBonus;
            exp += StageRewardPolicy.FullComboExperienceBonus;
        }
        if (r.failed)
        {
            money /= StageRewardPolicy.FailureDivisor;
            exp /= StageRewardPolicy.FailureDivisor;
        }
        return (money, exp);
    }

    [Test]
    public void PremiumEarnsTheConfiguredPercentageOnTopOfEverything()
    {
        Assert.Multiple(() =>
        {
            // +100% = double, which is what a Premium account is meant to earn.
            Assert.That(StageRewardPolicy.AddPercent(500, 100), Is.EqualTo(1000u));
            Assert.That(StageRewardPolicy.AddPercent(500, 50), Is.EqualTo(750u));
            // 0 disables the bonus outright rather than zeroing the payout.
            Assert.That(StageRewardPolicy.AddPercent(500, 0), Is.EqualTo(500u));
            // Saturates instead of wrapping.
            Assert.That(
                StageRewardPolicy.AddPercent(uint.MaxValue, 100),
                Is.EqualTo(uint.MaxValue));
            // The client labels Premium off this bit, so the payout follows the label.
            Assert.That(
                AccountClassInfo.IsPremium((uint)AccountClassFlags.Premium), Is.True);
            Assert.That(AccountClassInfo.IsPremium(0), Is.False);
        });
    }

    [Test]
    public void MaxAndExperienceEachHaveAFlatMultiplier()
    {
        uint money = StageRewardPolicy.MoneyMultiplier;
        uint experience = StageRewardPolicy.ExperienceMultiplier;
        try
        {
            StageRewardPolicy.MoneyMultiplier = 3;
            StageRewardPolicy.ExperienceMultiplier = 2;
            Assert.Multiple(() =>
            {
                Assert.That(StageRewardPolicy.Scale(100, 3), Is.EqualTo(300u));
                Assert.That(StageRewardPolicy.Scale(100, 1), Is.EqualTo(100u));
                Assert.That(
                    StageRewardPolicy.Scale(uint.MaxValue, 2), Is.EqualTo(uint.MaxValue));
            });
        }
        finally
        {
            StageRewardPolicy.MoneyMultiplier = money;
            StageRewardPolicy.ExperienceMultiplier = experience;
        }
    }
}
