using Arrowgene.DJMaxOnline.Server.Korea400;
using NUnit.Framework;

namespace Arrowgene.DJMaxOnline.Test;

public sealed class BattleItemComboRewardTrackerTest
{
    /// <summary>
    /// Combo per item DROP. The rest of these assert the mechanism, not the number, so
    /// the interval can be retuned in settings.ini without rewriting the suite.
    /// </summary>
    private const ushort Interval = BattleItemComboRewardTracker.DefaultComboInterval;

    [Test]
    public void AnItemDropsEveryFiftyComboByDefault()
    {
        // Drop timing is server policy: the client has no drop rule, and its own 30
        // counter is note hits driving item LEVEL-UPS.
        Assert.That(BattleItemComboRewardTracker.DefaultComboInterval, Is.EqualTo(50));
        Assert.That(new BattleItemComboRewardTracker().ComboInterval, Is.EqualTo(50));
    }

    [Test]
    public void TheIntervalIsConfigurable()
    {
        BattleItemComboRewardTracker tracker = new(comboInterval: 25);

        Assert.That(tracker.ComboInterval, Is.EqualTo(25));
        Assert.That(tracker.Observe(24, 2400), Is.Zero);
        Assert.That(tracker.Observe(25, 2500), Is.EqualTo(1));
        Assert.That(tracker.Observe(50, 5000), Is.EqualTo(2));
    }

    [Test]
    public void ANonPositiveIntervalFallsBackToTheDefault()
    {
        // A mistyped setting must not award on every report.
        Assert.That(new BattleItemComboRewardTracker(0).ComboInterval, Is.EqualTo(Interval));
        Assert.That(new BattleItemComboRewardTracker(-5).ComboInterval, Is.EqualTo(Interval));
    }

    [Test]
    public void UnbrokenStreakRewardsEveryOneIntervalOfCombo()
    {
        BattleItemComboRewardTracker tracker = new();

        Assert.That(tracker.Observe(Interval - 1, (Interval - 1) * 100), Is.Zero);
        Assert.That(tracker.Observe(Interval, Interval * 100), Is.EqualTo(1));
        Assert.That(tracker.Observe(
            (ushort)(Interval * 2 - 1), (Interval * 2 - 1) * 100), Is.EqualTo(1));
        Assert.That(tracker.Observe(
            (ushort)(Interval * 2), Interval * 2 * 100), Is.EqualTo(2));
    }

    [Test]
    public void SecondIntervalAfterBreakEarnsAnotherItem()
    {
        BattleItemComboRewardTracker tracker = new();

        Assert.That(tracker.Observe(Interval, Interval * 100), Is.EqualTo(1));
        // BREAK/MISS itself contributes zero points, so it becomes observable when
        // the restarted combo scores without raising the old song maximum.
        Assert.That(tracker.Observe(Interval, Interval * 150), Is.EqualTo(1));
        Assert.That(tracker.HasObservedBreak, Is.True);
        // Half an interval of score growth is all that is provable so far.
        Assert.That(tracker.EstimatedCurrentStreakCombo, Is.EqualTo(Interval / 2));
        Assert.That(tracker.Observe(Interval, Interval * 200), Is.EqualTo(2));
        Assert.That(tracker.EstimatedCurrentStreakCombo, Is.EqualTo(Interval));
    }

    [Test]
    public void ScoreGrowthAtAFlatMaximumAlsoProvesABreak()
    {
        BattleItemComboRewardTracker tracker = new();

        Assert.That(tracker.Observe(Interval, Interval * 100), Is.EqualTo(1));
        // The gauge recovered before the next five-second snapshot, but scoring while
        // the song maximum remains flat proves that the current combo restarted.
        Assert.That(tracker.Observe(Interval, Interval * 100 + 100), Is.EqualTo(1));
        Assert.That(tracker.HasObservedBreak, Is.True);
        Assert.That(tracker.Observe(Interval, Interval * 200), Is.EqualTo(2));
    }

    [Test]
    public void ConservativeScoreEstimateNeverRewardsBeforeAFullInterval()
    {
        BattleItemComboRewardTracker tracker = new();

        tracker.Observe(Interval, Interval * 100);
        tracker.Observe(Interval, Interval * 100 + 1);

        Assert.That(tracker.Observe(Interval, Interval * 200 - 1), Is.EqualTo(1));
        Assert.That(tracker.Observe(Interval, Interval * 200), Is.EqualTo(2));
    }

    [Test]
    public void ResetStartsANewSongRewardSequence()
    {
        BattleItemComboRewardTracker tracker = new();

        Assert.That(tracker.Observe(Interval, Interval * 100), Is.EqualTo(1));
        tracker.Reset();

        Assert.That(tracker.RewardsEarned, Is.Zero);
        Assert.That(tracker.Observe(Interval, Interval * 100), Is.EqualTo(1));
    }
}

