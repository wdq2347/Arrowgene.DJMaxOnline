using System.Buffers.Binary;
using Arrowgene.DJMaxOnline.Server.Korea400;

namespace Arrowgene.DJMaxOnline.Test;

/// <summary>
/// The 13 judgment windows ride in the game-info payload's config block (payload+16,
/// block offset 4). The client has no judgment table of its own for online play, so these
/// values ARE the game's timing - which makes the captured retail block a byte-exact
/// contract, not a default to be casually edited.
/// </summary>
public class JudgmentWindowTest
{
    /// <summary>
    /// Decrypted from blade_stream_02's captured OnGameInfoInf. If this ever fails, the
    /// server is shipping different timing than retail did.
    /// </summary>
    private const string RetailJudgmentDeltaHex =
        "ffffffff3300000032000000300000002c0000002a00000028000000" +
        "2600000024000000200000001c0000001800000010000000";

    [Test]
    public void RetailWindowsStillMatchTheCapturedBlock()
    {
        byte[] expected = Convert.FromHexString(RetailJudgmentDeltaHex);
        byte[] actual = new byte[expected.Length];
        JudgmentWindows.Retail.WriteTo(actual);

        Assert.Multiple(() =>
        {
            Assert.That(actual, Is.EqualTo(expected));
            // Entry 0 is a sentinel, never a window.
            Assert.That(JudgmentWindows.Retail.Values[0], Is.EqualTo(-1));
            Assert.That(JudgmentWindows.Retail.Values, Has.Count.EqualTo(13));
            // The real windows descend, tightest last.
            Assert.That(JudgmentWindows.Retail.Values[1], Is.EqualTo(51));
            Assert.That(JudgmentWindows.Retail.Values[12], Is.EqualTo(16));
        });
    }

    [Test]
    public void AdjustWidensAndTightensEveryWindowButTheSentinel()
    {
        JudgmentWindows wider = JudgmentWindows.Retail.Adjust(6);
        JudgmentWindows tighter = JudgmentWindows.Retail.Adjust(-4);

        Assert.Multiple(() =>
        {
            Assert.That(wider.Values[0], Is.EqualTo(-1), "sentinel is not a window");
            Assert.That(wider.Values[1], Is.EqualTo(57));
            Assert.That(wider.Values[12], Is.EqualTo(22));
            Assert.That(tighter.Values[0], Is.EqualTo(-1));
            Assert.That(tighter.Values[1], Is.EqualTo(47));
            Assert.That(tighter.Values[12], Is.EqualTo(12));
            // Zero is a no-op and must not allocate a different result.
            Assert.That(JudgmentWindows.Retail.Adjust(0), Is.SameAs(JudgmentWindows.Retail));
        });
    }

    [Test]
    public void NoWindowCanBeTightenedOutOfExistence()
    {
        // A 0 ms window cannot be hit at all, so an over-large tightening clamps.
        JudgmentWindows impossible = JudgmentWindows.Retail.Adjust(-1000);

        Assert.Multiple(() =>
        {
            Assert.That(impossible.Values[0], Is.EqualTo(-1));
            Assert.That(
                impossible.Values.Skip(1),
                Is.All.EqualTo(JudgmentWindows.MinimumWindowMs));
        });
    }

    [Test]
    public void TheConfigBlockCarriesTheWindowsAtOffsetFour()
    {
        JudgmentWindows windows = JudgmentWindows.Retail.Adjust(2);
        byte[] block = GameInfoBlobBuilder.BuildConfigBlock(windows);

        Assert.Multiple(() =>
        {
            Assert.That(block, Has.Length.EqualTo(112));
            Assert.That(BinaryPrimitives.ReadInt32LittleEndian(block), Is.EqualTo(7));
            Assert.That(
                BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(4)), Is.EqualTo(-1));
            Assert.That(
                BinaryPrimitives.ReadInt32LittleEndian(block.AsSpan(8)), Is.EqualTo(53));
            // The gauge rates and the trailing 2.4f are untouched by a window change.
            Assert.That(
                BinaryPrimitives.ReadSingleLittleEndian(block.AsSpan(108)),
                Is.EqualTo(2.4f));
        });
    }

    [Test]
    public void TheDefaultBlockIsStillTheRetailOne()
    {
        Assert.That(
            GameInfoBlobBuilder.DefaultConfigBlock,
            Is.EqualTo(GameInfoBlobBuilder.BuildConfigBlock(JudgmentWindows.Retail)));
    }
}
