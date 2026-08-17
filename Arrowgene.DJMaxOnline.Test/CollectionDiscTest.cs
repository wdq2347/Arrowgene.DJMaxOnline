using Arrowgene.DJMaxOnline.Server.Korea400;

namespace Arrowgene.DJMaxOnline.Test;

/// <summary>
/// Collection discs earned by hitting an EXACT judgment percentage. Retail's set is
/// Ruby 10%, Sapphire 1%, Devil 66.6% (note count a multiple of 5) and Rainbow MAX 77.7%
/// (multiple of 10) - the multiples are what make those percentages reachable.
/// </summary>
public class CollectionDiscTest
{
    private static List<AccuracyDiscRule> Rules() => CollectionDiscs.Default();

    [Test]
    public void AnExactPercentageEarnsItsDisc()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                CollectionDiscs.Earned(Rules(), 10.0, 400)?.Name, Is.EqualTo("Ruby Disc"));
            Assert.That(
                CollectionDiscs.Earned(Rules(), 1.0, 400)?.Name,
                Is.EqualTo("Sapphire Disc"));
            // A hair off earns nothing - that is the whole point of the award.
            Assert.That(CollectionDiscs.Earned(Rules(), 10.02, 400), Is.Null);
            Assert.That(CollectionDiscs.Earned(Rules(), 9.98, 400), Is.Null);
            // The client reports a float, so an exact-equality test would never fire.
            Assert.That(
                CollectionDiscs.Earned(Rules(), 10.0 + 0.004, 400)?.Name,
                Is.EqualTo("Ruby Disc"));
        });
    }

    [Test]
    public void TheNoteCountGatesTheTwoRepeatingPercentages()
    {
        Assert.Multiple(() =>
        {
            // 66.6% only counts on a chart whose note count is a multiple of 5.
            Assert.That(
                CollectionDiscs.Earned(Rules(), 66.6, 500)?.Name, Is.EqualTo("Devil Disc"));
            Assert.That(CollectionDiscs.Earned(Rules(), 66.6, 501), Is.Null);
            // 77.7% wants a multiple of 10.
            Assert.That(
                CollectionDiscs.Earned(Rules(), 77.7, 500)?.Name,
                Is.EqualTo("Rainbow MAX"));
            Assert.That(CollectionDiscs.Earned(Rules(), 77.7, 505), Is.Null);
            Assert.That(CollectionDiscs.Earned(Rules(), 77.7, 0), Is.Null);
        });
    }

    [Test]
    public void OnlyCodesTheDialogCanDrawAreAccepted()
    {
        Assert.Multiple(() =>
        {
            // sub_465AD1 maps 0x400..0x413 through a switch and 0x420..0x42D through
            // code-1035; anything else in the band resolves to no component.
            Assert.That(CollectionDiscs.Renders(0x400), Is.True);
            Assert.That(CollectionDiscs.Renders(0x413), Is.True);
            Assert.That(CollectionDiscs.Renders(0x420), Is.True);
            Assert.That(CollectionDiscs.Renders(0x42D), Is.True);
            // The gap between the two drawable bands, and the medal band below them.
            Assert.That(CollectionDiscs.Renders(0x414), Is.False);
            Assert.That(CollectionDiscs.Renders(0x41F), Is.False);
            Assert.That(CollectionDiscs.Renders(0x42E), Is.False);
            Assert.That(CollectionDiscs.Renders(0x3FF), Is.False);
            // Every shipped default has to be drawable or the award is invisible.
            Assert.That(
                Rules().Select(rule => CollectionDiscs.Renders(rule.Code)),
                Is.All.True);
        });
    }

}
