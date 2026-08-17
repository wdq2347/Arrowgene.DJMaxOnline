using Arrowgene.DJMaxOnline.Server.Korea400.Packets;

namespace Arrowgene.DJMaxOnline.Test;

public class SpecialIconCatalogTest
{
    [Test]
    public void HiddenCatalogHasDistinctClientIconIds()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SpecialIconCatalog.Entries, Has.Count.EqualTo(24));
            Assert.That(
                SpecialIconCatalog.Entries.Select(icon => icon.IconId).Distinct().Count(),
                Is.EqualTo(SpecialIconCatalog.Entries.Count));
            Assert.That(
                SpecialIconCatalog.Entries.Select(icon => icon.Alias).Distinct(
                    StringComparer.OrdinalIgnoreCase).Count(),
                Is.EqualTo(SpecialIconCatalog.Entries.Count));
        });
    }

    [TestCase("djball", 0x1009u)]
    [TestCase("2:10", 0x1009u)]
    [TestCase("0x1009", 0x1009u)]
    [TestCase("4096", 0x1000u)]
    [TestCase("battler1", 0xA80Cu)]
    public void ResolverAcceptsAliasesCatalogCoordinatesAndIds(
        string value,
        uint expectedIconId)
    {
        Assert.That(SpecialIconCatalog.TryFind(value, out SpecialIconInfo icon), Is.True);
        Assert.That(icon.IconId, Is.EqualTo(expectedIconId));
    }

    [Test]
    public void DjBallIsTheRetailOperatorOnlyEntry()
    {
        SpecialIconInfo icon = SpecialIconCatalog.Entries.Single(icon =>
            icon.Alias == "djball");

        Assert.Multiple(() =>
        {
            Assert.That(icon.OperatorOnly, Is.True);
            Assert.That(icon.Type, Is.EqualTo(2));
            Assert.That(icon.Number, Is.EqualTo(10));
            Assert.That(icon.IconId, Is.EqualTo(0x1009u));
        });
    }
}
