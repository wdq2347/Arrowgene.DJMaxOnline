using System.Text;
using Arrowgene.DJMaxOnline.Server;
using Microsoft.VisualStudio.TestPlatform.Utilities;

namespace Arrowgene.DJMaxOnline.Test;

public class DjMaxCryptoTest
{
    [Test]
    public void Test1()
    {
        DjMaxCrypto crypto = DjMaxCrypto.Init();

        Span<byte> test = Encoding.UTF8.GetBytes("This is a test");
        Span<byte> test2 = Encoding.UTF8.GetBytes("With a 2nd part");

        crypto.Encrypt(ref test);
        crypto.Decrypt(ref test);
        crypto.Encrypt(ref test2);
        crypto.Decrypt(ref test2);

        Assert.That(Encoding.UTF8.GetString(test), Is.EqualTo("This is a test"));
        Assert.That(Encoding.UTF8.GetString(test2), Is.EqualTo("With a 2nd part"));
    }

    [TestCase(0x34, 0x12, 0x00001234u)]
    [TestCase(0x80, 0xFF, 0xFFFFFF80u)]
    public void SumSeedIsSignedLittleEndian16AtOffset28(
        byte low,
        byte high,
        uint expected)
    {
        byte[] mtSeed = new byte[32];
        mtSeed[28] = low;
        mtSeed[29] = high;

        Assert.That(DjMaxCrypto.DeriveSumSeed(mtSeed), Is.EqualTo(expected));
    }
}
