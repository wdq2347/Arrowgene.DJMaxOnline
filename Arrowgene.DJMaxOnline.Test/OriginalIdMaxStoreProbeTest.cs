using Arrowgene.DJMaxOnline.Server;

namespace Arrowgene.DJMaxOnline.Test;

public class OriginalIdMaxStoreProbeTest
{
    private ShopCatalog _catalog = null!;

    [OneTimeSetUp]
    public void LoadCatalog()
    {
        string directory = ShopCatalog.FindDataDirectory(
            Directory.GetCurrentDirectory(),
            TestContext.CurrentContext.TestDirectory) ??
            throw new DirectoryNotFoundException("Test shop DATA was not found.");
        _catalog = ShopCatalog.Load(directory);
    }

    [TestCase(0x0401, 2000)] // regular female avatar
    [TestCase(0x8401, 2000)] // native MAX female avatar
    [TestCase(0xF801, 2000)] // original event avatar
    [TestCase(0x2405, 5000)] // Oblivion regular-ID gear
    [TestCase(0xA401, 5000)] // native MAX gear
    [TestCase(0x2801, 3000)] // regular-ID note
    [TestCase(0xA801, 3000)] // native MAX note
    public void OriginalIconIdIsAnUnrestrictedMaxPurchase(int id, int expectedPrice)
    {
        ShopItemDefinition item = _catalog.Get((ushort)id);

        Assert.Multiple(() =>
        {
            Assert.That(_catalog.TryGetListing((ushort)id, out ShopListing? listing), Is.True);
            Assert.That(listing!.Kind, Is.EqualTo(ShopListingKind.InGame));
            Assert.That(item.Currency, Is.EqualTo(ShopCurrency.Money));
            Assert.That(item.Price, Is.EqualTo((uint)expectedPrice));
            Assert.That(item.RequiredLevel, Is.EqualTo(99));
            Assert.That(item.Released, Is.True);
            Assert.That(_catalog.IsNormalPurchase(item), Is.True);
        });
    }
}
