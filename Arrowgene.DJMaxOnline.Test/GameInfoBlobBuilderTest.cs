using Arrowgene.DJMaxOnline.Server.Korea400;

namespace Arrowgene.DJMaxOnline.Test;

public class GameInfoBlobBuilderTest
{
    [Test]
    public void DefaultConfigBlockMatchesCapturedOnlineServerValues()
    {
        byte[] expected = Convert.FromHexString(
            "07000000ffffffff3300000032000000300000002c0000002a00000028000000" +
            "2600000024000000200000001c0000001800000010000000000000000000c0c0" +
            "000000000ad7233c0ad7a33c8fc2f53c0ad7233dcdcc4c3d8fc2753d295c8f3d" +
            "0ad7a33d9a99193e9a99993e9a991940");

        Assert.That(GameInfoBlobBuilder.DefaultConfigBlock, Is.EqualTo(expected));
    }
}
