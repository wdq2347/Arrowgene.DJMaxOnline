using Arrowgene.DJMaxOnline.Server.Korea400;
using Arrowgene.DJMaxOnline.Server.Korea400.Packets;
using NUnit.Framework;

namespace Arrowgene.DJMaxOnline.Test;

/// <summary>
/// Level is stored ZERO-BASED. The client renders it plus one, and the experience curve
/// indexes its table by it, so the two have to agree or a new account both displays the
/// wrong number and starts on the wrong rung.
/// </summary>
[TestFixture]
public class PlayerLevelTest
{
    [Test]
    public void ANewAccountStartsAtLevelZero()
    {
        Assert.That(new LocalPlayerProgress().Level, Is.Zero,
            "a stored 1 renders as 'Lv 2' on the client");
    }

    [Test]
    public void TheCurveIsIndexedByTheStoredLevel()
    {
        // First rung: a brand new player needs the first threshold, not the second.
        Assert.That(ExperienceCurve.Required(0), Is.EqualTo(ExperienceCurve.Thresholds[0]));
        Assert.That(ExperienceCurve.Required(1), Is.EqualTo(ExperienceCurve.Thresholds[1]));
    }

    [Test]
    public void ANewAccountNeedsTheCheapestLevelUp()
    {
        LocalPlayerProgress progress = new();
        Assert.That(ExperienceCurve.Required(progress.Level),
            Is.EqualTo(ExperienceCurve.Thresholds.Min()),
            "starting at 1 skipped the cheapest rung of the curve");
    }

    [Test]
    public void TheCurveStopsAtTheClientsCeiling()
    {
        Assert.That(ExperienceCurve.Required(ExperienceCurve.MaxLevel),
            Is.EqualTo(uint.MaxValue));
    }
}
